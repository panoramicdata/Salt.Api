namespace Salt.Api;

public sealed partial class SaltClient
{
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
	public async Task<PatchRunResult> PatchDryRunAsync(PatchDryRunRequest request, CancellationToken cancellationToken)
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
	public async Task<PatchRunResult> PatchApplyAsync(PatchApplyRequest request, CancellationToken cancellationToken)
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
					(target.MinionIds ?? []).Select(id => MinionResult.Failure<PatchStateRun>(id, MinionFailureKind.NotReturned, NoAcceptedKeyText)),
					noMinionsMatched: true),
			};
		}

		var (result, complete) = await PollJobAsync(job, timeout, cancellationToken).ConfigureAwait(false);
		var results = job.Minions
			.Union(target.MinionIds ?? [], StringComparer.Ordinal)
			.Select(minionId => ToPatchMinionResult(minionId, job, result))
			.ToList();

		return new PatchRunResult
		{
			Jid = job.Jid,
			IsDryRun = test,
			TimedOut = !complete,
			Minions = new MinionResultDictionary<PatchStateRun>(results, noMinionsMatched: false),
		};
	}

	private MinionResult<PatchStateRun> ToPatchMinionResult(string minionId, SaltJob job, SaltJobResult result)
	{
		if (!result.Returns.TryGetValue(minionId, out var minionReturn))
		{
			var text = job.Minions.Contains(minionId, StringComparer.Ordinal)
				? $"The minion did not return before the deadline; job {job.Jid} may still be running on it."
				: NoAcceptedKeyText;
			return MinionResult.Failure<PatchStateRun>(minionId, MinionFailureKind.NotReturned, text);
		}

		try
		{
			return MinionResult.Success(minionId, SaltResponseParser.ParseStateRun(minionReturn, _options.PackageStateId));
		}
		catch (FormatException ex)
		{
			return MinionResult.Failure<PatchStateRun>(minionId, MinionFailureKind.StringResponse, ex.Message);
		}
	}
}
