namespace Salt.Api;

internal static partial class SaltResponseParser
{
	/// <summary>The start of the key of every <c>pkg</c> state: <c>pkg_|-&lt;id&gt;_|-&lt;name&gt;_|-&lt;function&gt;</c>.</summary>
	private const string PackageStateKeyPrefix = "pkg_|-";

	/// <summary>
	/// Maps one minion's state-run return to a <see cref="PatchStateRun"/>.
	/// </summary>
	/// <param name="minionReturn">The minion's return.</param>
	/// <param name="packageStateId">The <c>__id__</c> of the <c>pkg</c> state whose changes are the package upgrades.</param>
	/// <exception cref="FormatException">The return is not an object of state results (for example a render error, which Salt returns as a list of strings).</exception>
	internal static PatchStateRun ParseStateRun(JobMinionReturn minionReturn, string packageStateId)
	{
		ArgumentException.ThrowIfNullOrEmpty(packageStateId);
		var states = minionReturn.Return;
		if (states.ValueKind != JsonValueKind.Object)
		{
			throw new FormatException(DescribeNonStateReturn(states));
		}

		var results = new List<StateResult>();
		foreach (var property in states.EnumerateObject())
		{
			if (property.Value.ValueKind != JsonValueKind.Object)
			{
				throw new FormatException(DescribeNonStateReturn(states));
			}

			var state = property.Value.Deserialize<StateResult>(SaltJson.Options)!;
			state.Key = property.Name;
			results.Add(state);
		}

		var packageState = results.FirstOrDefault(s =>
			s.Key.StartsWith(PackageStateKeyPrefix, StringComparison.Ordinal)
			&& string.Equals(s.Id, packageStateId, StringComparison.Ordinal));
		var packageChanges = new Dictionary<string, PackageChange>(StringComparer.Ordinal);
		if (packageState is not null && packageState.Changes.ValueKind == JsonValueKind.Object)
		{
			foreach (var change in packageState.Changes.EnumerateObject())
			{
				if (change.Value.ValueKind == JsonValueKind.Object)
				{
					packageChanges[change.Name] = new PackageChange(GetString(change.Value, "old"), GetString(change.Value, "new"));
				}
			}
		}

		return new PatchStateRun
		{
			States = [.. results.OrderBy(s => s.RunNumber)],
			RetCode = minionReturn.RetCode,
			Success = minionReturn.Success,
			PackageState = packageState,
			PackageChanges = packageChanges,
		};
	}

	private static string DescribeNonStateReturn(JsonElement value)
		=> value.ValueKind switch
		{
			JsonValueKind.String => value.GetString()!,
			JsonValueKind.Array => string.Join(Environment.NewLine, value.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText())),
			_ => $"The minion returned {value.ValueKind} instead of state results.",
		};
}
