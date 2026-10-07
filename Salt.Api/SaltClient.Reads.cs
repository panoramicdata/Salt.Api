namespace Salt.Api;

public sealed partial class SaltClient
{
	/// <summary>
	/// Runs <c>test.ping</c>. A minion that does not answer <c>true</c> is a failure.
	/// </summary>
	public async Task<MinionResultDictionary<bool>> PingAsync(MinionTarget target, CancellationToken cancellationToken)
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
	public async Task<MinionResultDictionary<PatchStatus>> GetPatchStatusAsync(MinionTarget target, CancellationToken cancellationToken)
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
	public async Task<MinionResultDictionary<JsonElement>> GetGrainAsync(MinionTarget target, string grain, CancellationToken cancellationToken)
	{
		var element = await RunSingleAsync(Lowstate.GrainsGet(target, grain), cancellationToken).ConfigureAwait(false);
		return SaltResponseParser.ParseMinions<JsonElement>(element, target.MinionIds, stringIsFailure: false, falseIsFailure: false);
	}

	/// <summary>
	/// Runs the runner <c>manage.up</c>: the ids of the connected minions. This is the cheap way to list minions.
	/// </summary>
	public async Task<IReadOnlyList<string>> GetMinionsUpAsync(CancellationToken cancellationToken)
	{
		var element = await RunSingleAsync(Lowstate.ManageUp(), cancellationToken).ConfigureAwait(false);
		return ReadStringArray(element, "manage.up");
	}

	/// <summary>
	/// Runs the runner <c>manage.status</c>: the minions that are up and down.
	/// </summary>
	public async Task<MinionStatus> GetMinionStatusAsync(CancellationToken cancellationToken)
	{
		var element = await RunSingleAsync(Lowstate.ManageStatus(), cancellationToken).ConfigureAwait(false);
		return DeserializeOrThrow<MinionStatus>(element, "manage.status");
	}

	/// <summary>
	/// Reads every minion key by state (<c>GET /keys</c>).
	/// </summary>
	public async Task<SaltKeys> GetKeysAsync(CancellationToken cancellationToken)
	{
		var root = await CallAsync(() => _api.GetKeysAsync(cancellationToken)).ConfigureAwait(false);
		return DeserializeOrThrow<SaltKeys>(SaltResponseParser.GetReturnValue(root), "GET /keys");
	}

	/// <summary>
	/// Reads every grain of one minion (<c>GET /minions/{id}</c>, several kilobytes). Prefer <see cref="GetGrainAsync"/>.
	/// There is deliberately no call for every minion's grains, which grows with the estate.
	/// </summary>
	/// <exception cref="ArgumentException">The id is not an exact minion id.</exception>
	public async Task<MinionResult<JsonElement>> GetMinionAsync(string minionId, CancellationToken cancellationToken)
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
	public async Task<IReadOnlyList<JobSummary>> GetJobsAsync(CancellationToken cancellationToken)
	{
		var root = await CallAsync(() => _api.GetJobsAsync(cancellationToken)).ConfigureAwait(false);
		return SaltResponseParser.ParseJobList(root);
	}

	/// <summary>
	/// Reads a job and the returns so far (<c>GET /jobs/{jid}</c>).
	/// </summary>
	public async Task<SaltJobResult> GetJobAsync(string jid, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(jid);
		var root = await CallAsync(() => _api.GetJobAsync(jid, cancellationToken)).ConfigureAwait(false);
		return SaltResponseParser.ParseJobResult(root, jid);
	}

	/// <summary>
	/// Logs out (<c>POST /logout</c>), if logged in. The token is then refused by Salt; the next call logs in again.
	/// </summary>
	public async Task LogoutAsync(CancellationToken cancellationToken)
	{
		if (!_handler.HasToken)
		{
			return;
		}

		await CallAsync(() => _api.LogoutAsync(cancellationToken)).ConfigureAwait(false);
		_handler.ClearToken();
	}
}
