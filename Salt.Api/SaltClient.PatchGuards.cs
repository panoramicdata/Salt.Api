namespace Salt.Api;

public sealed partial class SaltClient
{
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
		RequireForApply(request.ConfirmRealApply, $"A real apply needs {nameof(PatchApplyRequest.ConfirmRealApply)} = true.");
		RequireForApply(!string.IsNullOrWhiteSpace(request.ChangeReference), $"A real apply needs a {nameof(PatchApplyRequest.ChangeReference)}.");
		RequireForApply(request.MinionIds is { Count: > 0 }, "A real apply needs at least one exact minion id.");

		var invalid = request.MinionIds.Where(id => !MinionTarget.IsExactMinionId(id)).ToList();
		RequireForApply(invalid.Count == 0, $"A real apply takes exact minion ids only, not: {string.Join(", ", invalid.Select(i => $"'{i}'"))}.");

		var target = MinionTarget.List(request.MinionIds);
		var count = target.MinionIds!.Count;
		RequireForApply(
			count <= MaxMinionsPerApplyByDefault || request.AllowMoreThanEightMinions,
			$"A real apply on {count} minions needs {nameof(PatchApplyRequest.AllowMoreThanEightMinions)} = true.");

		var withoutDryRun = GetMinionsWithoutRecentDryRun(target.MinionIds);
		RequireForApply(
			withoutDryRun.Count == 0,
			$"A real apply needs a successful dry run by this client within the last {_options.DryRunValidityMinutes} minutes, with no failed state, for: {string.Join(", ", withoutDryRun)}.");

		return target;
	}

	private List<string> GetMinionsWithoutRecentDryRun(IReadOnlyList<string> minionIds)
	{
		var validFrom = _timeProvider.GetUtcNow() - TimeSpan.FromMinutes(_options.DryRunValidityMinutes);
		lock (_dryRunLock)
		{
			return [.. minionIds.Where(id => !_successfulDryRuns.TryGetValue(id, out var at) || at < validFrom)];
		}
	}

	private static void RequireForApply(bool isSatisfied, string message)
	{
		if (!isSatisfied)
		{
			throw new SaltPatchGuardException(message);
		}
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
}
