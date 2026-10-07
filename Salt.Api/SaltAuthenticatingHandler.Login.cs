using System.Text;

namespace Salt.Api;

internal sealed partial class SaltAuthenticatingHandler
{
	/// <summary>The number of logins made. Used by tests to prove the token is reused.</summary>
	internal int LoginCount => Volatile.Read(ref _loginCount);

	/// <summary>Whether a token is held.</summary>
	internal bool HasToken => Volatile.Read(ref _token) is not null;

	/// <summary>Forgets the token (after a logout).</summary>
	internal void ClearToken() => Volatile.Write(ref _token, null);

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
		try
		{
			using var document = JsonDocument.Parse(text);
			var login = SaltResponseParser.GetReturnElement(document.RootElement, 0);
			return (ReadToken(login), ReadExpire(login), HasPermissions(login));
		}
		catch (Exception ex) when (ex is JsonException or SaltApiException)
		{
			throw new SaltAuthenticationException(NoTokenMessage, ex);
		}
	}

	private static string ReadToken(JsonElement login)
	{
		var token = login.ValueKind == JsonValueKind.Object
			&& login.TryGetProperty("token", out var tokenElement)
			&& tokenElement.ValueKind == JsonValueKind.String
				? tokenElement.GetString()
				: null;
		return string.IsNullOrEmpty(token) ? throw new SaltAuthenticationException(NoTokenMessage) : token;
	}

	private static DateTimeOffset ReadExpire(JsonElement login)
		=> login.TryGetProperty("expire", out var expire) && expire.ValueKind == JsonValueKind.Number
			? DateTimeOffset.FromUnixTimeMilliseconds((long)(expire.GetDouble() * 1000))
			: throw new SaltAuthenticationException("The login response has no 'expire' time.");

	private static bool HasPermissions(JsonElement login)
		=> login.TryGetProperty("perms", out var perms)
			&& perms.ValueKind == JsonValueKind.Array
			&& perms.GetArrayLength() > 0;

	private sealed record LoginRequest(
		[property: JsonPropertyName("username")] string Username,
		[property: JsonPropertyName("password")] string Password,
		[property: JsonPropertyName("eauth")] string Eauth);
}
