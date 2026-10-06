namespace Salt.Api.Models;

/// <summary>
/// A request for a patch dry run: <c>state.apply patch.apply</c> with <c>test=True</c>, which reports what a real apply
/// would change. It still refreshes apt and takes the apt lock on each minion.
/// </summary>
public sealed class PatchDryRunRequest
{
	/// <summary>The minions. Exact ids or a glob.</summary>
	public required MinionTarget Target { get; init; }

	/// <summary>How long to wait for every minion to return. Defaults to 30 minutes (a dry run can wait for the apt lock).</summary>
	public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(30);
}

/// <summary>
/// A request for a REAL patch apply: <c>state.apply patch.apply</c> without <c>test</c>. This installs packages as root.
/// </summary>
/// <remarks>
/// <para>The client refuses the request, before anything is sent, unless all of these hold:</para>
/// <list type="bullet">
/// <item><see cref="SaltClientOptions.ReadOnly"/> is <see langword="false"/>.</item>
/// <item><see cref="ConfirmRealApply"/> is <see langword="true"/>.</item>
/// <item><see cref="ChangeReference"/> is set (for example an approved change request key).</item>
/// <item><see cref="MinionIds"/> are exact ids (no globs), at most 8 unless <see cref="AllowMoreThanEightMinions"/> is set.</item>
/// <item>This client ran a successful dry run (<see cref="SaltClient.PatchDryRunAsync"/>) for each of those minions within
/// <see cref="SaltClientOptions.DryRunValidityMinutes"/>, with no failed state.</item>
/// <item>No patch dry run or apply is still running on any of those minions (two runs contend for the apt lock).</item>
/// </list>
/// <para>
/// Policy that the client cannot check: a production apply needs an approved change; Salt never reboots, so read
/// <see cref="SaltClient.GetPatchStatusAsync"/> afterwards and arrange any reboot separately; do not apply during a
/// node drain or storage recovery. The client applies these guards whatever the estate, and never infers "test" or
/// "production" from the URL.
/// </para>
/// </remarks>
public sealed class PatchApplyRequest
{
	/// <summary>The exact minion ids to patch. No globs.</summary>
	public required IReadOnlyList<string> MinionIds { get; init; }

	/// <summary>The approved change reference this apply is made under. Required. Logged with the apply.</summary>
	public required string ChangeReference { get; init; }

	/// <summary>Must be <see langword="true"/>: the explicit confirmation that a real apply is intended.</summary>
	public bool ConfirmRealApply { get; init; }

	/// <summary>Permits more than 8 minions in one apply.</summary>
	public bool AllowMoreThanEightMinions { get; init; }

	/// <summary>How long to wait for every minion to return. Defaults to 30 minutes.</summary>
	public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(30);
}

/// <summary>
/// One state's result within a state run.
/// </summary>
public sealed class StateResult
{
	/// <summary>The state key, <c>&lt;module&gt;_|-&lt;id&gt;_|-&lt;name&gt;_|-&lt;function&gt;</c>.</summary>
	[JsonIgnore]
	public string Key { get; internal set; } = string.Empty;

	/// <summary>The state id.</summary>
	[JsonPropertyName("__id__")]
	public string? Id { get; init; }

	/// <summary>The state name.</summary>
	[JsonPropertyName("name")]
	public string? Name { get; init; }

	/// <summary>
	/// <see langword="true"/>: satisfied or changed; <see langword="false"/>: failed; <see langword="null"/>: in a dry run, would change.
	/// </summary>
	[JsonPropertyName("result")]
	public bool? Result { get; init; }

	/// <summary>The state's comment.</summary>
	[JsonPropertyName("comment")]
	public string? Comment { get; init; }

	/// <summary>The changes made, or in a dry run the changes that would be made.</summary>
	[JsonPropertyName("changes")]
	public JsonElement Changes { get; init; }

	/// <summary>The SLS file the state came from.</summary>
	[JsonPropertyName("__sls__")]
	public string? Sls { get; init; }

	/// <summary>The order the state ran in.</summary>
	[JsonPropertyName("__run_num__")]
	public int RunNumber { get; init; }

	/// <summary>How long the state took, in milliseconds.</summary>
	[JsonPropertyName("duration")]
	public double? DurationMilliseconds { get; init; }

	/// <summary>Whether there are changes.</summary>
	[JsonIgnore]
	public bool HasChanges => Changes.ValueKind == JsonValueKind.Object && Changes.EnumerateObject().Any();
}

/// <summary>
/// A package version change.
/// </summary>
/// <param name="Old">The installed version.</param>
/// <param name="New">The new version.</param>
public sealed record PackageChange(string? Old, string? New);

/// <summary>
/// One minion's run of the patch state.
/// </summary>
public sealed class PatchStateRun
{
	/// <summary>The state key whose changes are the package upgrades.</summary>
	public const string PackageStateKey = "pkg_|-patch-apply_|-patch-apply_|-uptodate";

	/// <summary>The states, in run order.</summary>
	public IReadOnlyList<StateResult> States { get; init; } = [];

	/// <summary>The minion's return code.</summary>
	public int? RetCode { get; init; }

	/// <summary>Whether Salt reported success. This does not mean nothing would change.</summary>
	public bool? Success { get; init; }

	/// <summary>Package upgrades: in a dry run, what would be installed; in a real apply, what was installed.</summary>
	public IReadOnlyDictionary<string, PackageChange> PackageChanges { get; init; } = new Dictionary<string, PackageChange>();

	/// <summary>Whether any state failed. A class-based refusal to patch appears here, not as an HTTP error.</summary>
	public bool HasFailures => States.Any(s => s.Result == false);

	/// <summary>The states that failed.</summary>
	public IEnumerable<StateResult> FailedStates => States.Where(s => s.Result == false);

	/// <summary>The states with changes (or, in a dry run, that would change).</summary>
	public IEnumerable<StateResult> ChangedStates => States.Where(s => s.Result is null || s.HasChanges);
}

/// <summary>
/// The outcome of a patch dry run or apply.
/// </summary>
public sealed class PatchRunResult
{
	/// <summary>The job id, or <see langword="null"/> when the target matched no minion and nothing ran.</summary>
	public string? Jid { get; init; }

	/// <summary>Whether this was a dry run.</summary>
	public bool IsDryRun { get; init; }

	/// <summary>Whether the deadline passed before every minion returned. Those minions are failures; the job may still be running on them.</summary>
	public bool TimedOut { get; init; }

	/// <summary>Per-minion results. A minion that did not return, or returned an error instead of state results, is a failure.</summary>
	public required MinionResultDictionary<PatchStateRun> Minions { get; init; }

	/// <summary>Whether every minion returned state results with no failed state.</summary>
	public bool AllSucceeded => !TimedOut && Minions.AllSucceeded && Minions.Values.All(m => !m.Value!.HasFailures);
}
