using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
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
	private const int JitterResolution = 1_000_000;

	private const string NoTokenMessage =
		"The login returned HTTP 200 without a token. rest_cherrypy answers a failed login like this when it takes the client for a browser.";

	/// <summary>What to do after a failed response.</summary>
	internal enum RetryAction
	{
		/// <summary>Give up and throw.</summary>
		Fail,

		/// <summary>Log in again, then retry once.</summary>
		Relogin,

		/// <summary>Wait, then retry.</summary>
		BackOff,
	}

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

	protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
	{
		var relativePath = GetRelativePath(request.RequestUri);
		var body = await BufferContentAsync(request, cancellationToken).ConfigureAwait(false);
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
			if (response.IsSuccessStatusCode)
			{
				return response;
			}

			var status = (int)response.StatusCode;
			var errorText = await ReadErrorTextAsync(response, cancellationToken).ConfigureAwait(false);
			response.Dispose();

			switch (DecideRetry(status, reloggedIn, isReadOnlySafe, attempt))
			{
				case RetryAction.Relogin:
					// The session may have been lost (salt-api restarted) or expired: log in again once, then retry once.
					reloggedIn = true;
					attempt--;
					LogRelogin(_logger, request.Method.Method, relativePath);
					await RefreshTokenAsync(token, cancellationToken).ConfigureAwait(false);
					break;
				case RetryAction.BackOff:
					await BackOffAsync(request.Method.Method, relativePath, status, attempt, cancellationToken).ConfigureAwait(false);
					break;
				default:
					throw CreateFailure(request.Method, relativePath, status, attempt, errorText);
			}
		}
	}

	/// <summary>
	/// Replaces the content with its bytes, so that the body that is checked is exactly the body that is sent, it can be
	/// sent again on a retry, and it has a Content-Length (rest_cherrypy answers a chunked JSON body with HTTP 500).
	/// </summary>
	/// <returns>The body as text, or <see langword="null"/> when there is none.</returns>
	private static async Task<string?> BufferContentAsync(HttpRequestMessage request, CancellationToken cancellationToken)
	{
		if (request.Content is null)
		{
			return null;
		}

		var bytes = await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
		var buffered = new ByteArrayContent(bytes);
		foreach (var header in request.Content.Headers.Where(h => h.Key != "Content-Length"))
		{
			buffered.Headers.TryAddWithoutValidation(header.Key, header.Value);
		}

		request.Content.Dispose();
		request.Content = buffered;
		return Encoding.UTF8.GetString(bytes);
	}

	/// <summary>
	/// What to do after a failed response. A request that could change state (<paramref name="isReadOnlySafe"/> false) is
	/// retried only on 401 and 429, which mean it was refused before it ran.
	/// </summary>
	internal RetryAction DecideRetry(int status, bool reloggedIn, bool isReadOnlySafe, int attempt)
	{
		var canRetry = attempt < _options.MaxAttemptCount;
		return status switch
		{
			401 => reloggedIn ? RetryAction.Fail : RetryAction.Relogin,
			429 => canRetry ? RetryAction.BackOff : RetryAction.Fail,
			502 or 503 or 504 => canRetry && isReadOnlySafe ? RetryAction.BackOff : RetryAction.Fail,
			_ => RetryAction.Fail,
		};
	}

	private static SaltException CreateFailure(HttpMethod method, string relativePath, int status, int attempt, string errorText)
		=> status == 401
			? new SaltAuthenticationException($"Salt refused {method} {relativePath} with HTTP 401 after a fresh login: {errorText}")
			: new SaltApiException(
				$"Salt returned HTTP {status} for {method} {relativePath} on attempt {attempt}: {errorText}",
				(HttpStatusCode)status,
				errorText);

	protected override void Dispose(bool disposing)
	{
		if (disposing)
		{
			_loginLock.Dispose();
		}

		base.Dispose(disposing);
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
		var jitter = RandomNumberGenerator.GetInt32(JitterResolution) / (double)JitterResolution;
		var delay = CalculateBackOffDelay(attempt, _options.InitialBackOffDelaySeconds, _options.BackOffDelayFactor, _options.MaxBackOffDelaySeconds, jitter);
		LogBackOff(_logger, method, relativePath, status, attempt, _options.MaxAttemptCount, delay.TotalSeconds);
		await _delay(delay, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// The back-off delay: the initial delay grown by the factor for each attempt, capped, plus up to 50% jitter (never above the cap),
	/// so that clients behind one shared address do not all retry at the same moment.
	/// </summary>
	/// <param name="attempt">The attempt that failed, from 1.</param>
	/// <param name="initialSeconds">The first delay.</param>
	/// <param name="factor">The growth per attempt.</param>
	/// <param name="maxSeconds">The cap.</param>
	/// <param name="jitter">A fraction in [0, 1) choosing where in the jitter range the delay falls.</param>
	internal static TimeSpan CalculateBackOffDelay(int attempt, double initialSeconds, double factor, int maxSeconds, double jitter)
	{
		var seconds = Math.Min(initialSeconds * Math.Pow(factor, attempt - 1), maxSeconds);
		var ceiling = Math.Min(seconds * 1.5, maxSeconds);
		return TimeSpan.FromSeconds(seconds + (jitter * (ceiling - seconds)));
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

	[LoggerMessage(Message = "Salt {Method} {Path} returned {Status} in {ElapsedMilliseconds} ms (attempt {Attempt})")]
	private static partial void LogRequest(ILogger logger, LogLevel level, string method, string path, int status, long elapsedMilliseconds, int attempt);

	[LoggerMessage(Level = LogLevel.Information, Message = "Salt login for {Username} succeeded; the token expires at {Expire:u}")]
	private static partial void LogLogin(ILogger logger, string username, DateTimeOffset expire);

	[LoggerMessage(Level = LogLevel.Information, Message = "Salt returned 401 for {Method} {Path}; logging in again and retrying once")]
	private static partial void LogRelogin(ILogger logger, string method, string path);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Salt returned {Status} for {Method} {Path} on attempt {Attempt}/{MaxAttempts}; waiting {DelaySeconds:N1} s")]
	private static partial void LogBackOff(ILogger logger, string method, string path, int status, int attempt, int maxAttempts, double delaySeconds);
}
