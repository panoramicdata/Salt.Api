namespace Salt.Api;

/// <summary>
/// One Salt lowstate command: the JSON object sent to <c>POST /</c>.
/// </summary>
/// <remarks>
/// <para>
/// Lowstates are built only through the static factories. Every public factory except <see cref="Raw"/> produces a
/// call on the read-only allow-list, so code that uses only those factories cannot build a forbidden call.
/// <see cref="Raw"/> can build anything, and needs <see cref="SaltClientOptions.AllowRawLowstate"/>; in read-only
/// mode the result is still checked against the allow-list before anything is sent.
/// </para>
/// <para>
/// The patch state (<c>state.apply patch.apply</c>) is never built here; use <see cref="SaltClient.PatchDryRunAsync"/>
/// and <see cref="SaltClient.PatchApplyAsync"/>.
/// </para>
/// </remarks>
public sealed class Lowstate
{
	private Lowstate(
		string client,
		string function,
		MinionTarget? target,
		string? targetExpression,
		string? targetType,
		IReadOnlyList<object?>? arguments,
		IReadOnlyDictionary<string, object?>? keywordArguments,
		int? timeout,
		bool isRaw)
	{
		Client = client;
		Function = function;
		MinionTarget = target;
		Target = target?.Expression ?? targetExpression;
		TargetType = target?.TargetType ?? targetType;
		Arguments = arguments;
		KeywordArguments = keywordArguments;
		Timeout = timeout;
		IsRaw = isRaw;
	}

	/// <summary>
	/// The Salt client: <c>local</c>, <c>local_async</c>, <c>runner</c> or <c>wheel</c>.
	/// </summary>
	[JsonPropertyName("client")]
	public string Client { get; }

	/// <summary>
	/// The target expression, for <c>local</c> and <c>local_async</c>.
	/// </summary>
	[JsonPropertyName("tgt")]
	public string? Target { get; }

	/// <summary>
	/// The target type, for <c>local</c> and <c>local_async</c>.
	/// </summary>
	[JsonPropertyName("tgt_type")]
	public string? TargetType { get; }

	/// <summary>
	/// The Salt function, for example <c>test.ping</c>.
	/// </summary>
	[JsonPropertyName("fun")]
	public string Function { get; }

	/// <summary>
	/// Positional arguments.
	/// </summary>
	[JsonPropertyName("arg")]
	public IReadOnlyList<object?>? Arguments { get; }

	/// <summary>
	/// Keyword arguments.
	/// </summary>
	[JsonPropertyName("kwarg")]
	public IReadOnlyDictionary<string, object?>? KeywordArguments { get; }

	/// <summary>
	/// For <c>local</c>: how long the master waits for minions, in seconds. When <see langword="null"/>,
	/// <see cref="SaltClientOptions.DefaultMinionTimeoutSeconds"/> is sent.
	/// </summary>
	[JsonPropertyName("timeout")]
	public int? Timeout { get; }

	/// <summary>
	/// Whether this lowstate was built with <see cref="Raw"/>.
	/// </summary>
	[JsonIgnore]
	public bool IsRaw { get; }

	/// <summary>
	/// The structured target, when the lowstate was built with one.
	/// </summary>
	[JsonIgnore]
	public MinionTarget? MinionTarget { get; }

	/// <summary>
	/// <c>test.ping</c>: whether each minion answers.
	/// </summary>
	public static Lowstate Ping(MinionTarget target, int? timeoutSeconds = null)
		=> Local(target, "test.ping", null, null, timeoutSeconds);

	/// <summary>
	/// <c>patchreport.status</c>: the read-only patch report for each minion.
	/// </summary>
	public static Lowstate PatchStatus(MinionTarget target, int? timeoutSeconds = null)
		=> Local(target, "patchreport.status", null, null, timeoutSeconds);

	/// <summary>
	/// <c>pkg.list_upgrades</c> with <c>refresh=False</c>: upgrades from the apt lists as they stand. It never runs <c>apt-get update</c>.
	/// </summary>
	public static Lowstate ListUpgrades(MinionTarget target, int? timeoutSeconds = null)
		=> Local(target, "pkg.list_upgrades", null, new Dictionary<string, object?> { ["refresh"] = false }, timeoutSeconds);

	/// <summary>
	/// <c>grains.get</c>: one grain from each minion.
	/// </summary>
	/// <exception cref="ArgumentException">The grain name is empty or contains '='.</exception>
	public static Lowstate GrainsGet(MinionTarget target, string grain, int? timeoutSeconds = null)
	{
		if (string.IsNullOrWhiteSpace(grain) || grain.Contains('=', StringComparison.Ordinal))
		{
			throw new ArgumentException("A grain name must be non-empty and must not contain '='.", nameof(grain));
		}

		return Local(target, "grains.get", [grain], null, timeoutSeconds);
	}

	/// <summary>
	/// <c>grains.items</c>: every grain of each minion (several kilobytes per minion; prefer <see cref="GrainsGet"/>).
	/// </summary>
	public static Lowstate GrainsItems(MinionTarget target, int? timeoutSeconds = null)
		=> Local(target, "grains.items", null, null, timeoutSeconds);

	/// <summary>
	/// The runner <c>manage.up</c>: the ids of the minions that are connected.
	/// </summary>
	public static Lowstate ManageUp()
		=> new("runner", "manage.up", null, null, null, null, null, null, false);

	/// <summary>
	/// The runner <c>manage.status</c>: the ids of the minions that are up and down.
	/// </summary>
	public static Lowstate ManageStatus()
		=> new("runner", "manage.status", null, null, null, null, null, null, false);

	/// <summary>
	/// The wheel <c>key.list_all</c>: every minion key by state.
	/// </summary>
	public static Lowstate KeyListAll()
		=> new("wheel", "key.list_all", null, null, null, null, null, null, false);

	/// <summary>
	/// Any lowstate. Needs <see cref="SaltClientOptions.AllowRawLowstate"/>. This bypasses the patch apply guards
	/// of <see cref="SaltClient.PatchApplyAsync"/>, so use it only when no typed method fits.
	/// </summary>
	/// <param name="client">The Salt client: <c>local</c>, <c>local_async</c>, <c>runner</c> or <c>wheel</c>.</param>
	/// <param name="function">The function, for example <c>test.ping</c>.</param>
	/// <param name="target">The target, for <c>local</c> and <c>local_async</c>.</param>
	/// <param name="arguments">Positional arguments.</param>
	/// <param name="keywordArguments">Keyword arguments.</param>
	/// <param name="timeoutSeconds">For <c>local</c>: how long the master waits for minions.</param>
	public static Lowstate Raw(
		string client,
		string function,
		MinionTarget? target = null,
		IReadOnlyList<object?>? arguments = null,
		IReadOnlyDictionary<string, object?>? keywordArguments = null,
		int? timeoutSeconds = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(client);
		ArgumentException.ThrowIfNullOrWhiteSpace(function);
		return new(client, function, target, null, null, arguments, keywordArguments, timeoutSeconds, true);
	}

	/// <summary>
	/// The patch state, <c>state.apply patch.apply</c>, as an async job. Internal: only the guarded patch methods use it.
	/// </summary>
	internal static Lowstate PatchStateApply(MinionTarget target, bool test)
		=> new(
			"local_async",
			"state.apply",
			target,
			null,
			null,
			["patch.apply"],
			test ? new Dictionary<string, object?> { ["test"] = true } : null,
			null,
			false);

	/// <summary>
	/// The same call as an async job (<c>local_async</c>), which returns a jid at once.
	/// </summary>
	internal Lowstate AsAsync()
		=> Client switch
		{
			"local_async" => this,
			"local" => new("local_async", Function, MinionTarget, Target, TargetType, Arguments, KeywordArguments, null, IsRaw),
			_ => throw new InvalidOperationException($"Only a 'local' lowstate can be submitted as a job; this one uses '{Client}'."),
		};

	internal Lowstate WithDefaultTimeout(int timeoutSeconds)
		=> Client == "local" && Timeout is null
			? new(Client, Function, MinionTarget, Target, TargetType, Arguments, KeywordArguments, timeoutSeconds, IsRaw)
			: this;

	private static Lowstate Local(
		MinionTarget target,
		string function,
		IReadOnlyList<object?>? arguments,
		IReadOnlyDictionary<string, object?>? keywordArguments,
		int? timeoutSeconds)
	{
		ArgumentNullException.ThrowIfNull(target);
		if (timeoutSeconds is <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(timeoutSeconds), "The timeout must be greater than zero.");
		}

		return new("local", function, target, null, null, arguments, keywordArguments, timeoutSeconds, false);
	}

	/// <inheritdoc/>
	public override string ToString() => $"{Client} {Function}" + (Target is null ? string.Empty : $" on {TargetType}:{Target}");
}
