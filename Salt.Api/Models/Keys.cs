namespace Salt.Api.Models;

/// <summary>
/// Minion keys by state, from <c>GET /keys</c> or the wheel <c>key.list_all</c>.
/// A minion with an accepted key is not necessarily connected; compare with <see cref="SaltClient.GetMinionsUpAsync"/>.
/// </summary>
public sealed class SaltKeys
{
	/// <summary>Accepted minion keys.</summary>
	[JsonPropertyName("minions")]
	public IReadOnlyList<string> Accepted { get; init; } = [];

	/// <summary>Keys waiting to be accepted.</summary>
	[JsonPropertyName("minions_pre")]
	public IReadOnlyList<string> Pending { get; init; } = [];

	/// <summary>Rejected keys.</summary>
	[JsonPropertyName("minions_rejected")]
	public IReadOnlyList<string> Rejected { get; init; } = [];

	/// <summary>Denied keys.</summary>
	[JsonPropertyName("minions_denied")]
	public IReadOnlyList<string> Denied { get; init; } = [];

	/// <summary>The master's own key files.</summary>
	[JsonPropertyName("local")]
	public IReadOnlyList<string> Local { get; init; } = [];
}

/// <summary>
/// The result of the runner <c>manage.status</c>.
/// </summary>
public sealed class MinionStatus
{
	/// <summary>Minions that answered.</summary>
	[JsonPropertyName("up")]
	public IReadOnlyList<string> Up { get; init; } = [];

	/// <summary>Minions with accepted keys that did not answer.</summary>
	[JsonPropertyName("down")]
	public IReadOnlyList<string> Down { get; init; } = [];
}
