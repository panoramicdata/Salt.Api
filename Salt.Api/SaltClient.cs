namespace Salt.Api;

/// <summary>
/// A client for the Salt Project REST API (rest_cherrypy).
/// </summary>
/// <remarks>
/// <para>
/// The client logs in once, on first use, and reuses the token until shortly before it expires. If Salt answers
/// HTTP 401 (for example because salt-api restarted and lost its sessions) it logs in again once and retries once.
/// Create one client per estate and reuse it; do not create one per request.
/// </para>
/// <para>
/// Salt answers HTTP 200 even when minions fail. Per-minion results are therefore returned as
/// <see cref="MinionResultDictionary{T}"/>, where a minion that returned an error string, did not return, or matched nothing
/// is a failure, never an empty "nothing to do".
/// </para>
/// </remarks>
/// <example>
/// <code>
/// using var client = new SaltClient(new SaltClientOptions
/// {
///     BaseUrl = "https://salt.example.com",
///     Username = "api-user",
///     Password = passwordFromSecretStore,
///     ReadOnly = true,
/// });
///
/// var status = await client.GetPatchStatusAsync(MinionTarget.List("web-01", "web-02"), cancellationToken);
/// foreach (var minion in status.Values)
/// {
///     Console.WriteLine(minion.Succeeded
///         ? $"{minion.MinionId}: {minion.Value!.PendingCount} pending, reboot required: {minion.Value.RebootRequired}"
///         : $"{minion.MinionId}: FAILED: {minion.FailureText}");
/// }
/// </code>
/// </example>
public sealed partial class SaltClient : IDisposable
{
	private const int MaxMinionsPerApplyByDefault = 8;
	private const int RunningJobsToInspect = 20;
	private const string NoAcceptedKeyText = "The minion did not match an accepted key; nothing ran on it.";
	private const string DefaultPatchStatusFunction = "patchreport.status";

	private readonly SaltClientOptions _options;
	private readonly ILogger _logger;
	private readonly HttpClient _httpClient;
	private readonly SaltAuthenticatingHandler _handler;
	private readonly ISaltApi _api;
	private readonly TimeProvider _timeProvider;
	private readonly Func<TimeSpan, CancellationToken, Task> _delay;
	private readonly Dictionary<string, DateTimeOffset> _successfulDryRuns = new(StringComparer.Ordinal);
	private readonly Lock _dryRunLock = new();

	/// <summary>
	/// Creates a client without logging. Certificates are always validated; there is no option to turn that off.
	/// </summary>
	/// <param name="options">The options. They are validated and copied: later changes to the object have no effect.</param>
	/// <exception cref="SaltConfigurationException">The options are not valid.</exception>
	public SaltClient(SaltClientOptions options)
		: this(options, null)
	{
	}

	/// <summary>
	/// Creates a client. Certificates are always validated; there is no option to turn that off.
	/// </summary>
	/// <param name="options">The options. They are validated and copied: later changes to the object have no effect.</param>
	/// <param name="logger">A logger, or <see langword="null"/>. Passwords, tokens and bodies are never logged.</param>
	/// <exception cref="SaltConfigurationException">The options are not valid.</exception>
	public SaltClient(SaltClientOptions options, ILogger? logger)
		: this(options, logger, new HttpClientHandler { UseCookies = false }, null, null)
	{
	}

	internal SaltClient(
		SaltClientOptions options,
		ILogger? logger,
		HttpMessageHandler innerHandler,
		TimeProvider? timeProvider,
		Func<TimeSpan, CancellationToken, Task>? delay)
	{
		ArgumentNullException.ThrowIfNull(options);
		options.Validate();
		_options = options.Snapshot();
		_logger = logger ?? NullLogger.Instance;
		_timeProvider = timeProvider ?? TimeProvider.System;
		_delay = delay ?? Task.Delay;

		var userAgent = _options.UserAgent ?? $"Salt.Api/{ThisAssembly.AssemblyInformationalVersion}";
		_handler = new SaltAuthenticatingHandler(_options, _logger, innerHandler, _timeProvider, _delay, userAgent);
		_httpClient = new HttpClient(_handler)
		{
			BaseAddress = new Uri(_options.BaseUrl.TrimEnd('/')),
			// The handler applies HttpClientTimeoutSeconds to each attempt; this outer limit must not cut short its back-off.
			Timeout = System.Threading.Timeout.InfiniteTimeSpan,
		};

		_api = RestService.For<ISaltApi>(_httpClient, new RefitSettings
		{
			ContentSerializer = new SystemTextJsonContentSerializer(SaltJson.Options),
		});
	}

	/// <summary>Whether this client is in read-only mode.</summary>
	public bool IsReadOnly => _options.ReadOnly;

	/// <summary>The number of logins this client has made.</summary>
	public int LoginCount => _handler.LoginCount;

	/// <inheritdoc/>
	public void Dispose() => _httpClient.Dispose();

	private async Task<JsonElement> RunSingleAsync(Lowstate lowstate, CancellationToken cancellationToken)
	{
		var root = await RunAsync([lowstate], cancellationToken).ConfigureAwait(false);
		return SaltResponseParser.GetReturnElement(root, 0);
	}

	private async Task<JsonElement> RunAsync(IReadOnlyList<Lowstate> lowstates, CancellationToken cancellationToken)
	{
		if (lowstates.Count == 0)
		{
			throw new ArgumentException("At least one lowstate is required.", nameof(lowstates));
		}

		if (!_options.AllowRawLowstate && lowstates.Any(l => l.IsRaw))
		{
			throw new SaltConfigurationException($"A raw lowstate needs {nameof(SaltClientOptions)}.{nameof(SaltClientOptions.AllowRawLowstate)}.");
		}

		IReadOnlyList<Lowstate> prepared = [.. lowstates.Select(l => l.WithDefaultTimeout(_options.DefaultMinionTimeoutSeconds))];
		if (_options.ReadOnly)
		{
			// Checked here, before any request is built, and again by the handler on the bytes about to be sent.
			ReadOnlyPolicy.EnsureAllowed(prepared);
		}

		return await CallAsync(() => _api.RunAsync(prepared, cancellationToken)).ConfigureAwait(false);
	}

	/// <summary>
	/// Calls Refit and rethrows the exception the handler raised (Refit wraps it in <see cref="ApiRequestException"/>),
	/// so that callers see the typed Salt exceptions.
	/// </summary>
	private static async Task<T> CallAsync<T>(Func<Task<T>> call)
	{
		try
		{
			return await call().ConfigureAwait(false);
		}
		catch (ApiRequestException ex) when (ex.InnerException is not null)
		{
			System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
			throw;
		}
	}

	private static T ConvertObject<T>(JsonElement value)
		=> value.ValueKind == JsonValueKind.Object
			? value.Deserialize<T>(SaltJson.Options)!
			: throw new FormatException($"Expected an object but found {value.ValueKind}.");

	private static T DeserializeOrThrow<T>(JsonElement element, string what)
	{
		try
		{
			return element.Deserialize<T>(SaltJson.Options)
				?? throw new SaltApiException($"{what} returned null.");
		}
		catch (JsonException ex)
		{
			throw new SaltApiException($"{what} returned an unexpected shape: {ex.Message}", ex);
		}
	}

	private static IReadOnlyList<string> ReadStringArray(JsonElement element, string what)
		=> element.ValueKind == JsonValueKind.Array && element.EnumerateArray().All(e => e.ValueKind == JsonValueKind.String)
			? [.. element.EnumerateArray().Select(e => e.GetString()!)]
			: throw new SaltApiException($"{what} returned {element.ValueKind}, not a list of minion ids.");

	[LoggerMessage(Level = LogLevel.Warning, Message = "Submitting a REAL patch apply on {Target} under change {ChangeReference}")]
	private static partial void LogApply(ILogger logger, string target, string changeReference);
}
