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
	/// Creates a client. Certificates are always validated; there is no option to turn that off.
	/// </summary>
	/// <param name="options">The options. They are validated and copied: later changes to the object have no effect.</param>
	/// <param name="logger">An optional logger. Passwords, tokens and bodies are never logged.</param>
	/// <exception cref="SaltConfigurationException">The options are not valid.</exception>
	public SaltClient(SaltClientOptions options, ILogger? logger = null)
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

	/// <summary>
	/// Runs <c>test.ping</c>. A minion that does not answer <c>true</c> is a failure.
	/// </summary>
	public async Task<MinionResultDictionary<bool>> PingAsync(MinionTarget target, CancellationToken cancellationToken = default)
	{
		var lowstate = Lowstate.Ping(target);
		var element = await RunSingleAsync(lowstate, cancellationToken).ConfigureAwait(false);
		return SaltResponseParser.ParseMinions<bool>(element, target.MinionIds, stringIsFailure: true, falseIsFailure: true);
	}

	/// <summary>
	/// Runs the patch status function, <see cref="SaltClientOptions.PatchStatusFunction"/> (by default <c>patchreport.status</c>):
	/// pending upgrades, kept-back and held packages, reboot flags and the last upgrade time.
	/// A minion without the module, or that did not return, is a failure, never "nothing pending".
	/// </summary>
	/// <exception cref="SaltReadOnlyViolationException">
	/// The client is read-only and the configured function is not on the fixed read-only allow-list. Nothing was sent.
	/// </exception>
	public async Task<MinionResultDictionary<PatchStatus>> GetPatchStatusAsync(MinionTarget target, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(target);
		var lowstate = _options.PatchStatusFunction == DefaultPatchStatusFunction
			? Lowstate.PatchStatus(target)
			: Lowstate.LocalFunction(target, _options.PatchStatusFunction);
		var element = await RunSingleAsync(lowstate, cancellationToken).ConfigureAwait(false);
		return SaltResponseParser.ParseMinions<PatchStatus>(element, target.MinionIds, stringIsFailure: true, falseIsFailure: true, ConvertObject<PatchStatus>);
	}

	/// <summary>
	/// Runs <c>grains.get</c> for one grain. The value is returned as JSON because a grain can be any type; a grain that is
	/// not set returns an empty string. Only a missing minion is reported as a failure, since a string is a valid grain value.
	/// </summary>
	public async Task<MinionResultDictionary<JsonElement>> GetGrainAsync(MinionTarget target, string grain, CancellationToken cancellationToken = default)
	{
		var element = await RunSingleAsync(Lowstate.GrainsGet(target, grain), cancellationToken).ConfigureAwait(false);
		return SaltResponseParser.ParseMinions<JsonElement>(element, target.MinionIds, stringIsFailure: false, falseIsFailure: false);
	}

	/// <summary>
	/// Runs the runner <c>manage.up</c>: the ids of the connected minions. This is the cheap way to list minions.
	/// </summary>
	public async Task<IReadOnlyList<string>> GetMinionsUpAsync(CancellationToken cancellationToken = default)
	{
		var element = await RunSingleAsync(Lowstate.ManageUp(), cancellationToken).ConfigureAwait(false);
		return ReadStringArray(element, "manage.up");
	}

	/// <summary>
	/// Runs the runner <c>manage.status</c>: the minions that are up and down.
	/// </summary>
	public async Task<MinionStatus> GetMinionStatusAsync(CancellationToken cancellationToken = default)
	{
		var element = await RunSingleAsync(Lowstate.ManageStatus(), cancellationToken).ConfigureAwait(false);
		return DeserializeOrThrow<MinionStatus>(element, "manage.status");
	}

	/// <summary>
	/// Reads every minion key by state (<c>GET /keys</c>).
	/// </summary>
	public async Task<SaltKeys> GetKeysAsync(CancellationToken cancellationToken = default)
	{
		var root = await CallAsync(() => _api.GetKeysAsync(cancellationToken)).ConfigureAwait(false);
		return DeserializeOrThrow<SaltKeys>(SaltResponseParser.GetReturnValue(root), "GET /keys");
	}

	/// <summary>
	/// Reads every grain of one minion (<c>GET /minions/{id}</c>, several kilobytes). Prefer <see cref="GetGrainAsync"/>.
	/// There is deliberately no call for every minion's grains, which grows with the estate.
	/// </summary>
	/// <exception cref="ArgumentException">The id is not an exact minion id.</exception>
	public async Task<MinionResult<JsonElement>> GetMinionAsync(string minionId, CancellationToken cancellationToken = default)
	{
		MinionTarget.EnsureExactMinionId(minionId, nameof(minionId));
		var root = await CallAsync(() => _api.GetMinionAsync(minionId, cancellationToken)).ConfigureAwait(false);
		var results = SaltResponseParser.ParseMinions<JsonElement>(
			SaltResponseParser.GetReturnElement(root, 0), [minionId], stringIsFailure: true, falseIsFailure: true);
		return results[minionId];
	}

	/// <summary>
	/// Lists recent jobs (<c>GET /jobs</c>), newest first. Note that every job lookup is itself recorded as a job.
	/// </summary>
	public async Task<IReadOnlyList<JobSummary>> GetJobsAsync(CancellationToken cancellationToken = default)
	{
		var root = await CallAsync(() => _api.GetJobsAsync(cancellationToken)).ConfigureAwait(false);
		return SaltResponseParser.ParseJobList(root);
	}

	/// <summary>
	/// Reads a job and the returns so far (<c>GET /jobs/{jid}</c>).
	/// </summary>
	public async Task<SaltJobResult> GetJobAsync(string jid, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(jid);
		var root = await CallAsync(() => _api.GetJobAsync(jid, cancellationToken)).ConfigureAwait(false);
		return SaltResponseParser.ParseJobResult(root, jid);
	}

	/// <summary>
	/// Submits a <c>local</c> lowstate as an async job (<c>local_async</c>) and returns at once with its jid.
	/// Use this, with <see cref="WaitForJobAsync"/>, for anything that may take more than a couple of minutes.
	/// </summary>
	/// <exception cref="SaltException">The target matched no minion, so nothing was started.</exception>
	public async Task<SaltJob> SubmitAsync(Lowstate lowstate, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(lowstate);
		return await SubmitCoreAsync(lowstate.AsAsync(), cancellationToken).ConfigureAwait(false)
			?? throw new SaltException($"The target of {lowstate} matched no minion; nothing was started.");
	}

	/// <summary>
	/// Polls a job until every minion it was sent to has returned.
	/// </summary>
	/// <param name="job">The job.</param>
	/// <param name="timeout">The overall deadline.</param>
	/// <param name="cancellationToken">A cancellation token.</param>
	/// <exception cref="SaltJobTimeoutException">The deadline passed; the exception holds the partial result.</exception>
	public async Task<SaltJobResult> WaitForJobAsync(SaltJob job, TimeSpan timeout, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(job);
		var (result, complete) = await PollJobAsync(job, timeout, cancellationToken).ConfigureAwait(false);
		return complete
			? result
			: throw new SaltJobTimeoutException(
				$"Job {job.Jid} did not complete within {timeout}. Not returned: {string.Join(", ", result.MissingMinions)}.",
				result);
	}

	/// <summary>
	/// Runs a patch dry run: <c>state.apply</c> of <see cref="SaltClientOptions.PatchStateName"/> (by default <c>patch.apply</c>)
	/// with <c>test=True</c>, as an async job, and waits for it.
	/// </summary>
	/// <remarks>
	/// <para>Not permitted in read-only mode: a dry run refreshes apt and takes the apt lock on each minion.</para>
	/// <para>
	/// A state that would change something has <see cref="StateResult.Result"/> <see langword="null"/>.
	/// <see cref="PatchStateRun.PackageChanges"/> lists the packages that would be upgraded, read from the <c>pkg</c> state whose
	/// id is <see cref="SaltClientOptions.PackageStateId"/>. The dry run is not an exact
	/// preview: it can list kept-back packages that the real apply will not install, so treat it as the larger set.
	/// </para>
	/// <para>
	/// A successful dry run with no failed state is recorded for each minion, and is the precondition for
	/// <see cref="PatchApplyAsync"/> on that minion.
	/// </para>
	/// </remarks>
	/// <exception cref="SaltReadOnlyViolationException">The client is read-only. Nothing was sent.</exception>
	public async Task<PatchRunResult> PatchDryRunAsync(PatchDryRunRequest request, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);
		ArgumentNullException.ThrowIfNull(request.Target);
		if (_options.ReadOnly)
		{
			throw new SaltReadOnlyViolationException(
				"Read-only mode refused the patch dry run: state.apply is not permitted, even with test=True, because it refreshes apt and takes the apt lock.");
		}

		var result = await RunPatchStateAsync(request.Target, test: true, request.Timeout, cancellationToken).ConfigureAwait(false);
		RecordDryRun(result);
		return result;
	}

	/// <summary>
	/// Runs a REAL patch apply: <c>state.apply</c> of <see cref="SaltClientOptions.PatchStateName"/> (by default <c>patch.apply</c>),
	/// which installs packages as root, as an async job, and waits for it.
	/// </summary>
	/// <remarks>
	/// <para>
	/// The guards are listed on <see cref="PatchApplyRequest"/>: every one is checked before anything is sent. Workflow
	/// rules: a dry run comes first; a production apply needs an approved change and exact minion ids; Salt never
	/// reboots, so read <see cref="GetPatchStatusAsync"/> afterwards and arrange any reboot separately; never start an
	/// apply while another runs on the same minion.
	/// </para>
	/// <para>
	/// The submit is never retried after it may have reached Salt. A real apply over HTTP has not yet been captured
	/// against a live server; the result shape is expected to match the dry run with <see cref="StateResult.Result"/>
	/// <see langword="true"/> and the changes filled in.
	/// </para>
	/// </remarks>
	/// <exception cref="SaltReadOnlyViolationException">The client is read-only. Nothing was sent.</exception>
	/// <exception cref="SaltPatchGuardException">A guard refused the apply. No apply was sent.</exception>
	public async Task<PatchRunResult> PatchApplyAsync(PatchApplyRequest request, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);
		if (_options.ReadOnly)
		{
			throw new SaltReadOnlyViolationException("Read-only mode refused the patch apply.");
		}

		var target = ValidateApplyRequest(request);
		await EnsureNoPatchRunningAsync(target.MinionIds!, cancellationToken).ConfigureAwait(false);

		LogApply(_logger, target.Expression, request.ChangeReference);
		lock (_dryRunLock)
		{
			// A later apply needs a fresh dry run.
			foreach (var id in target.MinionIds!)
			{
				_successfulDryRuns.Remove(id);
			}
		}

		return await RunPatchStateAsync(target, test: false, request.Timeout, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Runs one lowstate and returns its <c>return</c> element as <typeparamref name="T"/>. Subject to read-only mode.
	/// </summary>
	public async Task<T> ExecuteAsync<T>(Lowstate lowstate, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(lowstate);
		var element = await RunSingleAsync(lowstate, cancellationToken).ConfigureAwait(false);
		return DeserializeOrThrow<T>(element, lowstate.ToString());
	}

	/// <summary>
	/// Runs one <c>local</c> lowstate and returns per-minion results. Subject to read-only mode.
	/// A string is a failure unless <typeparamref name="T"/> is <see cref="string"/> or <see cref="JsonElement"/>;
	/// <c>false</c> is a failure unless it is <see cref="bool"/> or <see cref="JsonElement"/>.
	/// </summary>
	public async Task<MinionResultDictionary<T>> ExecuteOnMinionsAsync<T>(Lowstate lowstate, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(lowstate);
		var element = await RunSingleAsync(lowstate, cancellationToken).ConfigureAwait(false);
		return SaltResponseParser.ParseMinions<T>(
			element,
			SaltResponseParser.GetListMinionIds(lowstate),
			SaltResponseParser.StringIsFailureFor<T>(),
			SaltResponseParser.FalseIsFailureFor<T>());
	}

	/// <summary>
	/// Runs several lowstates in one request and returns each <c>return</c> element in order. Subject to read-only mode:
	/// one refused lowstate refuses the whole request.
	/// </summary>
	public async Task<IReadOnlyList<JsonElement>> ExecuteAsync(IReadOnlyList<Lowstate> lowstates, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(lowstates);
		var root = await RunAsync(lowstates, cancellationToken).ConfigureAwait(false);
		return [.. Enumerable.Range(0, lowstates.Count).Select(i => SaltResponseParser.GetReturnElement(root, i))];
	}

	/// <summary>
	/// Logs out (<c>POST /logout</c>), if logged in. The token is then refused by Salt; the next call logs in again.
	/// </summary>
	public async Task LogoutAsync(CancellationToken cancellationToken = default)
	{
		if (!_handler.HasToken)
		{
			return;
		}

		await CallAsync(() => _api.LogoutAsync(cancellationToken)).ConfigureAwait(false);
		_handler.ClearToken();
	}

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

	private async Task<SaltJob?> SubmitCoreAsync(Lowstate asyncLowstate, CancellationToken cancellationToken)
	{
		var element = await RunSingleAsync(asyncLowstate, cancellationToken).ConfigureAwait(false);
		return SaltResponseParser.ParseSubmittedJob(element);
	}

	private async Task<(SaltJobResult Result, bool Complete)> PollJobAsync(SaltJob job, TimeSpan timeout, CancellationToken cancellationToken)
	{
		var deadline = _timeProvider.GetUtcNow() + timeout;
		var pollInterval = TimeSpan.FromSeconds(_options.JobPollIntervalSeconds);
		while (true)
		{
			var result = await GetJobAsync(job.Jid, cancellationToken).ConfigureAwait(false);
			if (job.Minions.All(result.Returns.ContainsKey))
			{
				return (result, true);
			}

			var remaining = deadline - _timeProvider.GetUtcNow();
			if (remaining <= TimeSpan.Zero)
			{
				return (result, false);
			}

			await _delay(remaining < pollInterval ? remaining : pollInterval, cancellationToken).ConfigureAwait(false);
		}
	}

	private async Task<PatchRunResult> RunPatchStateAsync(MinionTarget target, bool test, TimeSpan timeout, CancellationToken cancellationToken)
	{
		if (timeout <= TimeSpan.Zero)
		{
			throw new ArgumentOutOfRangeException(nameof(timeout), "The timeout must be positive.");
		}

		var job = await SubmitCoreAsync(Lowstate.PatchStateApply(target, _options.PatchStateName, test), cancellationToken).ConfigureAwait(false);
		if (job is null)
		{
			return new PatchRunResult
			{
				IsDryRun = test,
				Minions = new MinionResultDictionary<PatchStateRun>(
					(target.MinionIds ?? []).Select(id => MinionResult.Failure<PatchStateRun>(
						id,
						MinionFailureKind.NotReturned,
						"The minion did not match an accepted key; nothing ran on it.")),
					noMinionsMatched: true),
			};
		}

		var (result, complete) = await PollJobAsync(job, timeout, cancellationToken).ConfigureAwait(false);
		var results = new List<MinionResult<PatchStateRun>>();
		foreach (var minionId in job.Minions.Union(target.MinionIds ?? [], StringComparer.Ordinal))
		{
			if (!result.Returns.TryGetValue(minionId, out var minionReturn))
			{
				results.Add(MinionResult.Failure<PatchStateRun>(
					minionId,
					MinionFailureKind.NotReturned,
					job.Minions.Contains(minionId, StringComparer.Ordinal)
						? $"The minion did not return before the deadline; job {job.Jid} may still be running on it."
						: "The minion did not match an accepted key; nothing ran on it."));
				continue;
			}

			try
			{
				results.Add(MinionResult.Success(minionId, SaltResponseParser.ParseStateRun(minionReturn, _options.PackageStateId)));
			}
			catch (FormatException ex)
			{
				results.Add(MinionResult.Failure<PatchStateRun>(minionId, MinionFailureKind.StringResponse, ex.Message));
			}
		}

		return new PatchRunResult
		{
			Jid = job.Jid,
			IsDryRun = test,
			TimedOut = !complete,
			Minions = new MinionResultDictionary<PatchStateRun>(results, noMinionsMatched: false),
		};
	}

	private void RecordDryRun(PatchRunResult result)
	{
		var now = _timeProvider.GetUtcNow();
		lock (_dryRunLock)
		{
			foreach (var minion in result.Minions.Values)
			{
				if (minion.Succeeded && !minion.Value!.HasFailures)
				{
					_successfulDryRuns[minion.MinionId] = now;
				}
				else
				{
					_successfulDryRuns.Remove(minion.MinionId);
				}
			}
		}
	}

	private MinionTarget ValidateApplyRequest(PatchApplyRequest request)
	{
		if (!request.ConfirmRealApply)
		{
			throw new SaltPatchGuardException($"A real apply needs {nameof(PatchApplyRequest.ConfirmRealApply)} = true.");
		}

		if (string.IsNullOrWhiteSpace(request.ChangeReference))
		{
			throw new SaltPatchGuardException($"A real apply needs a {nameof(PatchApplyRequest.ChangeReference)}.");
		}

		if (request.MinionIds is null || request.MinionIds.Count == 0)
		{
			throw new SaltPatchGuardException("A real apply needs at least one exact minion id.");
		}

		var invalid = request.MinionIds.Where(id => !MinionTarget.IsExactMinionId(id)).ToList();
		if (invalid.Count > 0)
		{
			throw new SaltPatchGuardException($"A real apply takes exact minion ids only, not: {string.Join(", ", invalid.Select(i => $"'{i}'"))}.");
		}

		var target = MinionTarget.List(request.MinionIds);
		if (target.MinionIds!.Count > MaxMinionsPerApplyByDefault && !request.AllowMoreThanEightMinions)
		{
			throw new SaltPatchGuardException(
				$"A real apply on {target.MinionIds.Count} minions needs {nameof(PatchApplyRequest.AllowMoreThanEightMinions)} = true.");
		}

		var validFrom = _timeProvider.GetUtcNow() - TimeSpan.FromMinutes(_options.DryRunValidityMinutes);
		List<string> withoutDryRun;
		lock (_dryRunLock)
		{
			withoutDryRun = [.. target.MinionIds.Where(id => !_successfulDryRuns.TryGetValue(id, out var at) || at < validFrom)];
		}

		if (withoutDryRun.Count > 0)
		{
			throw new SaltPatchGuardException(
				$"A real apply needs a successful dry run by this client within the last {_options.DryRunValidityMinutes} minutes, with no failed state, for: {string.Join(", ", withoutDryRun)}.");
		}

		return target;
	}

	private async Task EnsureNoPatchRunningAsync(IReadOnlyList<string> minionIds, CancellationToken cancellationToken)
	{
		var jobs = await GetJobsAsync(cancellationToken).ConfigureAwait(false);
		var patchJobs = jobs
			.Where(j => j.Function == "state.apply" && j.HasArgument(_options.PatchStateName))
			.Take(RunningJobsToInspect);

		foreach (var job in patchJobs)
		{
			var result = await GetJobAsync(job.Jid, cancellationToken).ConfigureAwait(false);
			var busy = result.MissingMinions.Intersect(minionIds, StringComparer.Ordinal).ToList();
			if (busy.Count > 0)
			{
				throw new SaltPatchGuardException(
					$"A patch run of {_options.PatchStateName} (job {job.Jid}) has not returned yet on: {string.Join(", ", busy)}. Two runs on one minion contend for the apt lock.");
			}
		}
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
