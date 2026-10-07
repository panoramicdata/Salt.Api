namespace Salt.Api;

public sealed partial class SaltClient
{
	/// <summary>
	/// Submits a <c>local</c> lowstate as an async job (<c>local_async</c>) and returns at once with its jid.
	/// Use this, with <see cref="WaitForJobAsync"/>, for anything that may take more than a couple of minutes.
	/// </summary>
	/// <exception cref="SaltException">The target matched no minion, so nothing was started.</exception>
	public async Task<SaltJob> SubmitAsync(Lowstate lowstate, CancellationToken cancellationToken)
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
	public async Task<SaltJobResult> WaitForJobAsync(SaltJob job, TimeSpan timeout, CancellationToken cancellationToken)
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
	/// Runs one lowstate and returns its <c>return</c> element as <typeparamref name="T"/>. Subject to read-only mode.
	/// </summary>
	public async Task<T> ExecuteAsync<T>(Lowstate lowstate, CancellationToken cancellationToken)
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
	public async Task<MinionResultDictionary<T>> ExecuteOnMinionsAsync<T>(Lowstate lowstate, CancellationToken cancellationToken)
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
	public async Task<IReadOnlyList<JsonElement>> ExecuteAsync(IReadOnlyList<Lowstate> lowstates, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(lowstates);
		var root = await RunAsync(lowstates, cancellationToken).ConfigureAwait(false);
		return [.. Enumerable.Range(0, lowstates.Count).Select(i => SaltResponseParser.GetReturnElement(root, i))];
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
}
