namespace Salt.Api;

/// <summary>
/// Options for a <see cref="SaltClient"/>.
/// </summary>
/// <remarks>
/// <para>
/// The client always sends <c>Accept: application/json</c>; that is not an option. Without it, Salt's
/// rest_cherrypy answers a failed login with HTTP 200 and an HTML page instead of a 401.
/// </para>
/// <para>
/// Read-only enforcement is done by this client, not by the server. A Salt account with full rights can
/// run any function on any minion, so set <see cref="ReadOnly"/> to <see langword="true"/> for any consumer
/// that only reads (dashboards, reports).
/// </para>
/// </remarks>
public class SaltClientOptions
{
	/// <summary>
	/// The base URL of the Salt API, for example <c>https://salt.example.com</c>. Required, and must use https.
	/// There is no default: the package never assumes which estate it is talking to.
	/// </summary>
	public string BaseUrl { get; set; } = string.Empty;

	/// <summary>
	/// The Salt API username. Required.
	/// </summary>
	public string Username { get; set; } = string.Empty;

	/// <summary>
	/// The Salt API password. Supply it from a secret store at runtime, never from source code.
	/// Either this or <see cref="PasswordProvider"/> must be set. It is never logged or included in exception messages.
	/// </summary>
	public string Password { get; set; } = string.Empty;

	/// <summary>
	/// An optional callback that returns the password at each login, so that a rotated secret is picked up
	/// on the next re-login without rebuilding the client. Takes precedence over <see cref="Password"/>.
	/// </summary>
	public Func<CancellationToken, ValueTask<string>>? PasswordProvider { get; set; }

	/// <summary>
	/// The Salt external authentication backend. Defaults to <c>file</c>.
	/// </summary>
	public string Eauth { get; set; } = "file";

	/// <summary>
	/// When <see langword="true"/>, only an exact, fixed allow-list of read-only Salt functions and endpoints may
	/// be called; anything else throws <see cref="SaltReadOnlyViolationException"/> before any request is sent.
	/// Defaults to <see langword="false"/>. See the README for the allow-list.
	/// </summary>
	/// <remarks>
	/// Even when this is <see langword="false"/>, a real patch apply needs an explicit <see cref="PatchApplyRequest"/>
	/// with exact minion ids, a change reference, a confirmation flag and a recent successful dry run, and arbitrary
	/// lowstate needs <see cref="AllowRawLowstate"/>.
	/// </remarks>
	public bool ReadOnly { get; set; }

	/// <summary>
	/// When <see langword="true"/>, <see cref="Lowstate.Raw"/> lowstates may be executed. Defaults to <see langword="false"/>.
	/// In read-only mode a raw lowstate must still pass the read-only allow-list.
	/// </summary>
	public bool AllowRawLowstate { get; set; }

	/// <summary>
	/// The timeout for one HTTP request, in seconds. Defaults to 150, longer than <see cref="DefaultMinionTimeoutSeconds"/>,
	/// because a synchronous Salt call blocks until the minions answer. Must be greater than <see cref="DefaultMinionTimeoutSeconds"/>.
	/// </summary>
	public int HttpClientTimeoutSeconds { get; set; } = 150;

	/// <summary>
	/// The lowstate <c>timeout</c> sent with synchronous <c>local</c> calls: how long the master waits for minions. Defaults to 120.
	/// Too short a value makes busy minions report as not returned. Keep it under about 240 seconds when a proxy closes requests after 5 minutes.
	/// </summary>
	public int DefaultMinionTimeoutSeconds { get; set; } = 120;

	/// <summary>
	/// How often a job is polled while waiting for it, in seconds. Defaults to 5.
	/// </summary>
	public int JobPollIntervalSeconds { get; set; } = 5;

	/// <summary>
	/// The maximum number of attempts for a request that receives HTTP 429, or (for read-only requests only) 502, 503 or 504.
	/// Defaults to 5. Requests that may change state are never retried after they could have reached the server.
	/// </summary>
	public int MaxAttemptCount { get; set; } = 5;

	/// <summary>
	/// The first back-off delay after HTTP 429, in seconds. Defaults to 5.
	/// </summary>
	public double InitialBackOffDelaySeconds { get; set; } = 5;

	/// <summary>
	/// The factor by which the back-off delay grows on each attempt. Defaults to 2.0.
	/// </summary>
	public double BackOffDelayFactor { get; set; } = 2.0;

	/// <summary>
	/// The maximum back-off delay, in seconds. Defaults to 30.
	/// </summary>
	public int MaxBackOffDelaySeconds { get; set; } = 30;

	/// <summary>
	/// How long before the token's <c>expire</c> time the client logs in again, in seconds. Defaults to 60.
	/// </summary>
	public int TokenRefreshMarginSeconds { get; set; } = 60;

	/// <summary>
	/// How long a successful dry run remains valid as the precondition for a real apply on the same minions, in minutes. Defaults to 60.
	/// </summary>
	public int DryRunValidityMinutes { get; set; } = 60;

	/// <summary>
	/// An optional User-Agent. Defaults to <c>Salt.Api/{version}</c>.
	/// </summary>
	public string? UserAgent { get; set; }

	/// <summary>
	/// The level at which each request's method, path, status and duration are logged. Defaults to <see cref="LogLevel.Debug"/>.
	/// Request and response bodies, passwords and tokens are never logged.
	/// </summary>
	public LogLevel RequestLogLevel { get; set; } = LogLevel.Debug;

	/// <summary>
	/// Validates the options.
	/// </summary>
	/// <exception cref="SaltConfigurationException">The options are not valid.</exception>
	public void Validate()
	{
		if (string.IsNullOrWhiteSpace(BaseUrl))
		{
			throw new SaltConfigurationException($"{nameof(BaseUrl)} must be set.");
		}

		if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
		{
			throw new SaltConfigurationException($"{nameof(BaseUrl)} must be an absolute https URL.");
		}

		if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.UserInfo))
		{
			throw new SaltConfigurationException($"{nameof(BaseUrl)} must not contain a query, fragment or user information.");
		}

		if (string.IsNullOrWhiteSpace(Username))
		{
			throw new SaltConfigurationException($"{nameof(Username)} must be set.");
		}

		if (PasswordProvider is null && string.IsNullOrEmpty(Password))
		{
			throw new SaltConfigurationException($"Either {nameof(Password)} or {nameof(PasswordProvider)} must be set.");
		}

		if (string.IsNullOrWhiteSpace(Eauth))
		{
			throw new SaltConfigurationException($"{nameof(Eauth)} must be set.");
		}

		if (DefaultMinionTimeoutSeconds <= 0)
		{
			throw new SaltConfigurationException($"{nameof(DefaultMinionTimeoutSeconds)} must be greater than zero.");
		}

		if (HttpClientTimeoutSeconds <= DefaultMinionTimeoutSeconds)
		{
			throw new SaltConfigurationException($"{nameof(HttpClientTimeoutSeconds)} must be greater than {nameof(DefaultMinionTimeoutSeconds)}.");
		}

		if (JobPollIntervalSeconds <= 0)
		{
			throw new SaltConfigurationException($"{nameof(JobPollIntervalSeconds)} must be greater than zero.");
		}

		if (MaxAttemptCount < 1)
		{
			throw new SaltConfigurationException($"{nameof(MaxAttemptCount)} must be at least 1.");
		}

		if (InitialBackOffDelaySeconds < 0 || MaxBackOffDelaySeconds < 0 || BackOffDelayFactor < 1.0)
		{
			throw new SaltConfigurationException("Back-off delays must not be negative, and the back-off factor must be at least 1.0.");
		}

		if (TokenRefreshMarginSeconds < 0)
		{
			throw new SaltConfigurationException($"{nameof(TokenRefreshMarginSeconds)} must not be negative.");
		}

		if (DryRunValidityMinutes <= 0)
		{
			throw new SaltConfigurationException($"{nameof(DryRunValidityMinutes)} must be greater than zero.");
		}
	}

	/// <summary>
	/// A copy, so that changing the caller's options after the client is built (for example turning off
	/// <see cref="ReadOnly"/>) has no effect on it.
	/// </summary>
	internal SaltClientOptions Snapshot() => (SaltClientOptions)MemberwiseClone();

	/// <summary>
	/// Returns a description of the options without the password.
	/// </summary>
	public override string ToString()
		=> $"{nameof(SaltClientOptions)} {{ {nameof(BaseUrl)} = {BaseUrl}, {nameof(Username)} = {Username}, {nameof(ReadOnly)} = {ReadOnly} }}";
}
