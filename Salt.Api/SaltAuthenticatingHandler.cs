using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;

namespace Salt.Api;

/// <summary>
/// Sends every request to the Salt API. It enforces read-only mode on the bytes about to be sent, adds
/// <c>Accept: application/json</c>, logs in once and reuses the token, logs in again and retries once on HTTP 401,
/// and backs off on HTTP 429.
/// </summary>
/// <remarks>
/// A request that could change state is never retried after it may have reached Salt: only HTTP 401 and 429 (which
/// Salt or the proxy refused before running anything) are retried for those. Read-only requests are also retried on
/// 502, 503 and 504.
/// </remarks>
internal sealed partial class SaltAuthenticatingHandler : DelegatingHandler
{
	private const string TokenHeader = "X-Auth-Token";
	private const int MaxErrorTextLength = 300;

	private readonly SaltClientOptions _options;
	private readonly ILogger _logger;
	private readonly Uri _baseUri;
	private readonly string _basePath;
	private readonly string _userAgent;
	private readonly TimeProvider _timeProvider;
	private readonly Func<TimeSpan, CancellationToken, Task> _delay;
	private readonly SemaphoreSlim _loginLock = new(1, 1);

	private string? _token;
	private DateTimeOffset _tokenExpiry;
	private int _loginCount;

	internal SaltAuthenticatingHandler(
		SaltClientOptions options,
		ILogger logger,
		HttpMessageHandler innerHandler,
		TimeProvider timeProvider,
		Func<TimeSpan, CancellationToken, Task> delay,
		string userAgent)
		: base(innerHandler)
	{
		_options = options;
		_logger = logger;
		_baseUri = new Uri(options.BaseUrl.TrimEnd('/') + "/");
		_basePath = _baseUri.AbsolutePath.TrimEnd('/');
		_timeProvider = timeProvider;
		_delay = delay;
		_userAgent = userAgent;
	}

	/// <summary>The number of logins made. Used by tests to prove the token is reused.</summary>
	internal int LoginCount => Volatile.Read(ref _loginCount);

	/// <summary>Whether a token is held.</summary>
	internal bool HasToken => Volatile.Read(ref _token) is not null;

	/// <summary>Forgets the token (after a logout).</summary>
	internal void ClearToken() => Volatile.Write(ref _token, null);

	protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
	{
		var relativePath = GetRelativePath(request.RequestUri);
		string? body = null;
		if (request.Content is not null)
		{
			// Replace the content with its bytes, so that the body that is checked is exactly the body that is sent, it can
			// be sent again on a retry, and it has a Content-Length (rest_cherrypy answers a chunked JSON body with HTTP 500).
			var bytes = await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
			body = Encoding.UTF8.GetString(bytes);
			var buffered = new ByteArrayContent(bytes);
			foreach (var header in request.Content.Headers.Where(h => h.Key != "Content-Length"))
			{
				buffered.Headers.TryAddWithoutValidation(header.Key, header.Value);
			}

			request.Content.Dispose();
			request.Content = buffered;
		}

		var readOnlyRefusal = ReadOnlyPolicy.CheckRequest(request.Method, relativePath, body);
		if (_options.ReadOnly && readOnlyRefusal is not null)
		{
			throw new SaltReadOnlyViolationException(readOnlyRefusal);
		}

		var isReadOnlySafe = readOnlyRefusal is null;
		PrepareHeaders(request);

		var reloggedIn = false;
		var attempt = 0;
		while (true)
		{
			attempt++;
			var token = await GetTokenAsync(cancellationToken).ConfigureAwait(false);
			request.Headers.Remove(TokenHeader);
			request.Headers.TryAddWithoutValidation(TokenHeader, token);

			var response = await SendOnceAsync(request, relativePath, attempt, cancellationToken).ConfigureAwait(false);
			var status = (int)response.StatusCode;
			if (response.IsSuccessStatusCode)
			{
				return response;
			}

			var errorText = await ReadErrorTextAsync(response, cancellationToken).ConfigureAwait(false);
			response.Dispose();

			switch (status)
			{
				case 401 when !reloggedIn:
					// The session may have been lost (salt-api restarted) or expired: log in again once, then retry once.
					reloggedIn = true;
					attempt--;
					LogRelogin(_logger, request.Method.Method, relativePath);
					await RefreshTokenAsync(token, cancellationToken).ConfigureAwait(false);
					continue;
				case 401:
					throw new SaltAuthenticationException(
						$"Salt refused {request.Method} {relativePath} with HTTP 401 after a fresh login: {errorText}");
				case 429 when attempt < _options.MaxAttemptCount:
					await BackOffAsync(request.Method.Method, relativePath, status, attempt, cancellationToken).ConfigureAwait(false);
					continue;
				case 502 or 503 or 504 when isReadOnlySafe && attempt < _options.MaxAttemptCount:
					await BackOffAsync(request.Method.Method, relativePath, status, attempt, cancellationToken).ConfigureAwait(false);
					continue;
				default:
					throw new SaltApiException(
						$"Salt returned HTTP {status} for {request.Method} {relativePath} on attempt {attempt}: {errorText}",
						(HttpStatusCode)status,
						errorText);
			}
		}
	}

	protected override void Dispose(bool disposing)
	{
		if (disposing)
		{
			_loginLock.Dispose();
		}

		base.Dispose(disposing);
	}

	private async Task<string> GetTokenAsync(CancellationToken cancellationToken)
	{
		if (IsTokenValid())
		{
			return _token!;
		}

		await _loginLock.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			if (!IsTokenValid())
			{
				await LoginAsync(cancellationToken).ConfigureAwait(false);
			}

			return _token!;
		}
		finally
		{
			_loginLock.Release();
		}
	}

	private async Task RefreshTokenAsync(string staleToken, CancellationToken cancellationToken)
	{
		await _loginLock.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			// Another request may already have logged in again; only log in if the token is still the stale one.
			if (_token is null || _token == staleToken)
			{
				await LoginAsync(cancellationToken).ConfigureAwait(false);
			}
		}
		finally
		{
			_loginLock.Release();
		}
	}

	private bool IsTokenValid()
		=> Volatile.Read(ref _token) is not null
			&& _timeProvider.GetUtcNow() < _tokenExpiry - TimeSpan.FromSeconds(_options.TokenRefreshMarginSeconds);

	private async Task LoginAsync(CancellationToken cancellationToken)
	{
		var password = _options.PasswordProvider is not null
			? await _options.PasswordProvider(cancellationToken).ConfigureAwait(false)
			: _options.Password;

		var attempt = 0;
		while (true)
		{
			attempt++;
			// A string body, so that it is sent with a Content-Length: rest_cherrypy answers a chunked JSON body with HTTP 500.
			using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseUri, "login"))
			{
				Content = new StringContent(
					JsonSerializer.Serialize(new LoginRequest(_options.Username, password, _options.Eauth), SaltJson.Options),
					Encoding.UTF8,
					"application/json"),
			};
			PrepareHeaders(request);
			Interlocked.Increment(ref _loginCount);

			using var response = await SendOnceAsync(request, "/login", attempt, cancellationToken).ConfigureAwait(false);
			var status = (int)response.StatusCode;
			if (status == 429 && attempt < _options.MaxAttemptCount)
			{
				await BackOffAsync("POST", "/login", status, attempt, cancellationToken).ConfigureAwait(false);
				continue;
			}

			if (status == 401)
			{
				throw new SaltAuthenticationException(
					$"Salt refused the login for '{_options.Username}' (HTTP 401): wrong username, password or eauth.");
			}

			if (!response.IsSuccessStatusCode)
			{
				var errorText = await ReadErrorTextAsync(response, cancellationToken).ConfigureAwait(false);
				throw new SaltApiException($"Salt returned HTTP {status} for POST /login: {errorText}", response.StatusCode, errorText);
			}

			var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
			var (token, expire, hasPermissions) = ParseLogin(text);
			if (!hasPermissions)
			{
				throw new SaltAuthenticationException(
					$"The login for '{_options.Username}' succeeded but the account has no permissions (empty 'perms'): a server configuration error.");
			}

			Volatile.Write(ref _token, token);
			_tokenExpiry = expire;
			LogLogin(_logger, _options.Username, expire);
			return;
		}
	}

	/// <summary>
	/// Reads a login response. Success means a JSON body with a non-empty <c>return[0].token</c>, whatever the status code.
	/// </summary>
	internal static (string Token, DateTimeOffset Expire, bool HasPermissions) ParseLogin(string text)
	{
		const string noToken = "The login returned HTTP 200 without a token. rest_cherrypy answers a failed login like this when it takes the client for a browser.";
		try
		{
			using var document = JsonDocument.Parse(text);
			var login = SaltResponseParser.GetReturnElement(document.RootElement, 0);
			if (login.ValueKind != JsonValueKind.Object
				|| !login.TryGetProperty("token", out var tokenElement)
				|| tokenElement.ValueKind != JsonValueKind.String
				|| string.IsNullOrEmpty(tokenElement.GetString()))
			{
				throw new SaltAuthenticationException(noToken);
			}

			var expire = login.TryGetProperty("expire", out var expireElement) && expireElement.ValueKind == JsonValueKind.Number
				? DateTimeOffset.FromUnixTimeMilliseconds((long)(expireElement.GetDouble() * 1000))
				: throw new SaltAuthenticationException("The login response has no 'expire' time.");

			var hasPermissions = login.TryGetProperty("perms", out var perms)
				&& perms.ValueKind == JsonValueKind.Array
				&& perms.GetArrayLength() > 0;

			return (tokenElement.GetString()!, expire, hasPermissions);
		}
		catch (Exception ex) when (ex is JsonException or SaltApiException)
		{
			throw new SaltAuthenticationException(noToken, ex);
		}
	}

	private async Task<HttpResponseMessage> SendOnceAsync(HttpRequestMessage request, string relativePath, int attempt, CancellationToken cancellationToken)
	{
		using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeoutSource.CancelAfter(TimeSpan.FromSeconds(_options.HttpClientTimeoutSeconds));
		var stopwatch = Stopwatch.StartNew();
		try
		{
			var response = await base.SendAsync(request, timeoutSource.Token).ConfigureAwait(false);
			LogRequest(_logger, _options.RequestLogLevel, request.Method.Method, relativePath, (int)response.StatusCode, stopwatch.ElapsedMilliseconds, attempt);
			return response;
		}
		catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
		{
			throw new TimeoutException(
				$"Salt did not answer {request.Method} {relativePath} within {_options.HttpClientTimeoutSeconds} seconds. A state-changing request may still be running on the minions.");
		}
	}

	private async Task BackOffAsync(string method, string relativePath, int status, int attempt, CancellationToken cancellationToken)
	{
		var delay = CalculateBackOffDelay(attempt, _options.InitialBackOffDelaySeconds, _options.BackOffDelayFactor, _options.MaxBackOffDelaySeconds, Random.Shared);
		LogBackOff(_logger, method, relativePath, status, attempt, _options.MaxAttemptCount, delay.TotalSeconds);
		await _delay(delay, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// The back-off delay: the initial delay grown by the factor for each attempt, capped, plus up to 50% jitter (never above the cap),
	/// so that clients behind one shared address do not all retry at the same moment.
	/// </summary>
	internal static TimeSpan CalculateBackOffDelay(int attempt, double initialSeconds, double factor, int maxSeconds, Random random)
	{
		var seconds = Math.Min(initialSeconds * Math.Pow(factor, attempt - 1), maxSeconds);
		var ceiling = Math.Min(seconds * 1.5, maxSeconds);
		return TimeSpan.FromSeconds(seconds + (random.NextDouble() * (ceiling - seconds)));
	}

	private void PrepareHeaders(HttpRequestMessage request)
	{
		request.Headers.Accept.Clear();
		request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
		request.Headers.UserAgent.Clear();
		request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
	}

	private string GetRelativePath(Uri? requestUri)
	{
		var path = requestUri?.AbsolutePath ?? "/";
		if (_basePath.Length > 0 && path.StartsWith(_basePath, StringComparison.Ordinal))
		{
			path = path[_basePath.Length..];
		}

		return path.Length == 0 ? "/" : path;
	}

	private static async Task<string> ReadErrorTextAsync(HttpResponseMessage response, CancellationToken cancellationToken)
	{
		var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
		text = text.Trim();
		return text.Length <= MaxErrorTextLength
			? text
			: string.Create(CultureInfo.InvariantCulture, $"{text[..MaxErrorTextLength]}...");
	}

	private sealed record LoginRequest(
		[property: JsonPropertyName("username")] string Username,
		[property: JsonPropertyName("password")] string Password,
		[property: JsonPropertyName("eauth")] string Eauth);

	[LoggerMessage(Message = "Salt {Method} {Path} returned {Status} in {ElapsedMilliseconds} ms (attempt {Attempt})")]
	private static partial void LogRequest(ILogger logger, LogLevel level, string method, string path, int status, long elapsedMilliseconds, int attempt);

	[LoggerMessage(Level = LogLevel.Information, Message = "Salt login for {Username} succeeded; the token expires at {Expire:u}")]
	private static partial void LogLogin(ILogger logger, string username, DateTimeOffset expire);

	[LoggerMessage(Level = LogLevel.Information, Message = "Salt returned 401 for {Method} {Path}; logging in again and retrying once")]
	private static partial void LogRelogin(ILogger logger, string method, string path);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Salt returned {Status} for {Method} {Path} on attempt {Attempt}/{MaxAttempts}; waiting {DelaySeconds:N1} s")]
	private static partial void LogBackOff(ILogger logger, string method, string path, int status, int attempt, int maxAttempts, double delaySeconds);
}
