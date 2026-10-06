namespace Salt.Api.Models;

/// <summary>
/// A job submitted with <c>local_async</c>.
/// </summary>
/// <param name="Jid">The job id.</param>
/// <param name="Minions">The minions the master sent the job to.</param>
public sealed record SaltJob(string Jid, IReadOnlyList<string> Minions);

/// <summary>
/// One entry of <c>GET /jobs</c>.
/// </summary>
public sealed class JobSummary
{
	/// <summary>The job id. Salt job ids are timestamps, so they sort by start time.</summary>
	[JsonIgnore]
	public string Jid { get; internal set; } = string.Empty;

	/// <summary>The function, for example <c>state.apply</c>.</summary>
	[JsonPropertyName("Function")]
	public string Function { get; init; } = string.Empty;

	/// <summary>The arguments, as Salt reports them.</summary>
	[JsonPropertyName("Arguments")]
	public JsonElement Arguments { get; init; }

	/// <summary>The target expression.</summary>
	[JsonPropertyName("Target")]
	public JsonElement Target { get; init; }

	/// <summary>The target type.</summary>
	[JsonPropertyName("Target-type")]
	public string? TargetType { get; init; }

	/// <summary>The user that started the job.</summary>
	[JsonPropertyName("User")]
	public string? User { get; init; }

	/// <summary>The start time as the master reports it, for example <c>2026, Oct 06 14:33:38.086181</c> (master local time).</summary>
	[JsonPropertyName("StartTime")]
	public string? StartTime { get; init; }

	/// <summary>Whether the arguments contain the string <paramref name="value"/>.</summary>
	public bool HasArgument(string value)
		=> Arguments.ValueKind == JsonValueKind.Array
			&& Arguments.EnumerateArray().Any(a => a.ValueKind == JsonValueKind.String && a.GetString() == value);
}

/// <summary>
/// One minion's return within a job.
/// </summary>
public sealed class JobMinionReturn
{
	/// <summary>The value the function returned.</summary>
	[JsonPropertyName("return")]
	public JsonElement Return { get; init; }

	/// <summary>The return code; 0 means success.</summary>
	[JsonPropertyName("retcode")]
	public int? RetCode { get; init; }

	/// <summary>Whether Salt considered the call a success.</summary>
	[JsonPropertyName("success")]
	public bool? Success { get; init; }

	/// <summary>The outputter, for example <c>highstate</c> for a state run.</summary>
	[JsonPropertyName("out")]
	public string? Outputter { get; init; }
}

/// <summary>
/// The result of <c>GET /jobs/{jid}</c>.
/// </summary>
public sealed class SaltJobResult
{
	/// <summary>The job id.</summary>
	public string Jid { get; init; } = string.Empty;

	/// <summary>The function.</summary>
	public string? Function { get; init; }

	/// <summary>The arguments, as Salt reports them.</summary>
	public JsonElement Arguments { get; init; }

	/// <summary>The target expression.</summary>
	public JsonElement Target { get; init; }

	/// <summary>The target type.</summary>
	public string? TargetType { get; init; }

	/// <summary>The user that started the job.</summary>
	public string? User { get; init; }

	/// <summary>The start time as the master reports it (master local time).</summary>
	public string? StartTime { get; init; }

	/// <summary>The minions the job went to.</summary>
	public IReadOnlyList<string> Minions { get; init; } = [];

	/// <summary>The returns so far, keyed by minion id. A minion missing here has not returned yet.</summary>
	public IReadOnlyDictionary<string, JobMinionReturn> Returns { get; init; } = new Dictionary<string, JobMinionReturn>();

	/// <summary>The minions in <see cref="Minions"/> that have not returned.</summary>
	public IReadOnlyList<string> MissingMinions => [.. Minions.Where(m => !Returns.ContainsKey(m))];

	/// <summary>Whether every minion in <see cref="Minions"/> has returned.</summary>
	public bool IsComplete => Minions.Count > 0 && Minions.All(Returns.ContainsKey);
}
