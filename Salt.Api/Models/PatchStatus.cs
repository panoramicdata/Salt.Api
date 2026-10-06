namespace Salt.Api.Models;

/// <summary>
/// The return value of the patch status function, <see cref="SaltClientOptions.PatchStatusFunction"/> (by default the
/// custom execution module function <c>patchreport.status</c>). It is read-only and changes nothing.
/// </summary>
public sealed class PatchStatus
{
	/// <summary>
	/// Package name to candidate version, at plain <c>apt-get upgrade</c> scope, from the apt lists as they stand
	/// (only as fresh as the last <c>apt-get update</c> on the host).
	/// </summary>
	[JsonPropertyName("pending_upgrades")]
	public IReadOnlyDictionary<string, string> PendingUpgrades { get; init; } = new Dictionary<string, string>();

	/// <summary>The number of entries in <see cref="PendingUpgrades"/>.</summary>
	[JsonPropertyName("pending_count")]
	public int PendingCount { get; init; }

	/// <summary>Packages a plain <c>apt-get upgrade</c> holds back, typically new kernel packages. A patch apply never installs these.</summary>
	[JsonPropertyName("kept_back")]
	public IReadOnlyList<string> KeptBack { get; init; } = [];

	/// <summary>From <c>kernelpkg.needs_reboot</c>; <see langword="null"/> when that module cannot answer.</summary>
	[JsonPropertyName("kernelpkg_needs_reboot")]
	public bool? KernelPackageNeedsReboot { get; init; }

	/// <summary>Whether <c>/var/run/reboot-required</c> exists.</summary>
	[JsonPropertyName("reboot_required_file")]
	public bool RebootRequiredFile { get; init; }

	/// <summary>Held packages (<c>apt-mark showhold</c>).</summary>
	[JsonPropertyName("held")]
	public IReadOnlyList<string> Held { get; init; } = [];

	/// <summary>Epoch seconds of the newest change to the apt history or dpkg log; <see langword="null"/> if neither exists.</summary>
	[JsonPropertyName("last_upgrade_epoch")]
	public long? LastUpgradeEpoch { get; init; }

	/// <summary>Which file supplied <see cref="LastUpgradeEpoch"/>.</summary>
	[JsonPropertyName("last_upgrade_source")]
	public string? LastUpgradeSource { get; init; }

	/// <summary><see cref="LastUpgradeEpoch"/> as a time, when present.</summary>
	[JsonIgnore]
	public DateTimeOffset? LastUpgrade => LastUpgradeEpoch is { } epoch ? DateTimeOffset.FromUnixTimeSeconds(epoch) : null;

	/// <summary>
	/// Whether a reboot is needed: the reboot-required file exists, or the kernel package says so. Salt never reboots;
	/// a reboot is a separate, approved operation.
	/// </summary>
	[JsonIgnore]
	public bool RebootRequired => RebootRequiredFile || KernelPackageNeedsReboot == true;

	/// <summary>Whether there are pending upgrades.</summary>
	[JsonIgnore]
	public bool PatchingNeeded => PendingCount > 0;

	/// <summary>Whether packages are kept back and need attention outside a Salt apply.</summary>
	[JsonIgnore]
	public bool NeedsAttention => KeptBack.Count > 0;
}
