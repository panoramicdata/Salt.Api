using System.Text.RegularExpressions;

namespace Salt.Api;

/// <summary>
/// Which minions a call runs on: either a list of exact minion ids, or a glob.
/// </summary>
/// <remarks>
/// Prefer <see cref="List(string[])"/>. With a list, ids that were asked for and did not answer are reported as
/// failures. With a glob, a pattern that matches nothing returns an empty result, which
/// <see cref="MinionResultDictionary{T}.NoMinionsMatched"/> reports.
/// </remarks>
public sealed partial class MinionTarget
{
	private MinionTarget(string expression, string targetType, IReadOnlyList<string>? minionIds)
	{
		Expression = expression;
		TargetType = targetType;
		MinionIds = minionIds;
	}

	/// <summary>
	/// The Salt target expression (<c>tgt</c>).
	/// </summary>
	public string Expression { get; }

	/// <summary>
	/// The Salt target type (<c>tgt_type</c>): <c>list</c> or <c>glob</c>.
	/// </summary>
	public string TargetType { get; }

	/// <summary>
	/// The exact minion ids, for a list target; otherwise <see langword="null"/>.
	/// </summary>
	public IReadOnlyList<string>? MinionIds { get; }

	/// <summary>
	/// Whether this is a list of exact minion ids.
	/// </summary>
	public bool IsExactList => MinionIds is not null;

	/// <summary>
	/// Targets exact minion ids (<c>tgt_type: list</c>).
	/// </summary>
	/// <param name="minionIds">One or more minion ids. Each must be an exact id: letters, digits, '.', '_' and '-' only, so no wildcard can slip in.</param>
	/// <exception cref="ArgumentException">The list is empty, or an id is not an exact minion id.</exception>
	public static MinionTarget List(params string[] minionIds) => List((IEnumerable<string>)minionIds);

	/// <summary>
	/// Targets exact minion ids (<c>tgt_type: list</c>).
	/// </summary>
	/// <param name="minionIds">One or more minion ids. Each must be an exact id: letters, digits, '.', '_' and '-' only, so no wildcard can slip in.</param>
	/// <exception cref="ArgumentException">The list is empty, or an id is not an exact minion id.</exception>
	public static MinionTarget List(IEnumerable<string> minionIds)
	{
		ArgumentNullException.ThrowIfNull(minionIds);
		var ids = minionIds.Distinct(StringComparer.Ordinal).ToList();
		if (ids.Count == 0)
		{
			throw new ArgumentException("At least one minion id is required.", nameof(minionIds));
		}

		foreach (var id in ids)
		{
			EnsureExactMinionId(id, nameof(minionIds));
		}

		return new MinionTarget(string.Join(',', ids), "list", ids.AsReadOnly());
	}

	/// <summary>
	/// Targets minions whose ids match a Salt glob (<c>tgt_type: glob</c>), for example <c>web-*</c>.
	/// Not accepted by <see cref="SaltClient.PatchApplyAsync"/>.
	/// </summary>
	/// <exception cref="ArgumentException">The pattern is empty or contains whitespace or a comma.</exception>
	public static MinionTarget Glob(string pattern)
	{
		if (string.IsNullOrWhiteSpace(pattern) || pattern.Any(c => char.IsWhiteSpace(c) || c == ','))
		{
			throw new ArgumentException("A glob must be non-empty and contain no whitespace or commas.", nameof(pattern));
		}

		return new MinionTarget(pattern, "glob", null);
	}

	/// <summary>
	/// Every minion (<c>*</c>).
	/// </summary>
	public static MinionTarget All { get; } = new("*", "glob", null);

	/// <summary>
	/// Whether a string is an exact minion id (letters, digits, '.', '_' and '-', starting with a letter or digit).
	/// </summary>
	public static bool IsExactMinionId(string? minionId)
		=> minionId is not null && ExactMinionIdRegex().IsMatch(minionId);

	internal static void EnsureExactMinionId(string? minionId, string parameterName)
	{
		if (!IsExactMinionId(minionId))
		{
			throw new ArgumentException($"'{minionId}' is not an exact minion id: use letters, digits, '.', '_' and '-' only (no wildcards).", parameterName);
		}
	}

	/// <inheritdoc/>
	public override string ToString() => $"{TargetType}:{Expression}";

	[GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,252}$", RegexOptions.CultureInvariant)]
	private static partial Regex ExactMinionIdRegex();
}
