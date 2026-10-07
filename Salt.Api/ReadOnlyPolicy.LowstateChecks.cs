namespace Salt.Api;

internal static partial class ReadOnlyPolicy
{
	/// <summary>
	/// The checks applied to each lowstate, in order. Each returns the reason for refusal, or <see langword="null"/>.
	/// The client and function check comes before the checks that rely on them being valid.
	/// </summary>
	private static readonly Func<JsonElement, string?>[] LowstateChecks =
	[
		CheckKeys,
		CheckClientAndFunction,
		CheckTarget,
		CheckArguments,
		CheckKeywordArguments,
		CheckListUpgrades,
	];

	private static string? CheckLowstate(JsonElement lowstate)
	{
		if (lowstate.ValueKind != JsonValueKind.Object)
		{
			return "it is not a JSON object.";
		}

		foreach (var check in LowstateChecks)
		{
			var reason = check(lowstate);
			if (reason is not null)
			{
				return reason;
			}
		}

		return null;
	}

	private static string? CheckKeys(JsonElement lowstate)
	{
		foreach (var property in lowstate.EnumerateObject())
		{
			if (!PermittedKeys.Contains(property.Name))
			{
				return $"the key '{property.Name}' is not permitted.";
			}
		}

		return null;
	}

	private static string? CheckClientAndFunction(JsonElement lowstate)
	{
		var client = GetString(lowstate, "client");
		var function = GetString(lowstate, "fun");
		if (string.IsNullOrEmpty(client))
		{
			return "'client' is missing, empty or not a string.";
		}

		if (string.IsNullOrEmpty(function))
		{
			return "'fun' is missing, empty or not a string.";
		}

		if (!PermittedFunctionsByClient.TryGetValue(client, out var permitted))
		{
			return $"the client '{client}' is not permitted.";
		}

		return permitted.Contains(function) ? null : $"'{client}' '{function}' is not on the read-only allow-list.";
	}

	private static string? CheckTarget(JsonElement lowstate)
	{
		var client = GetString(lowstate, "client")!;
		if (client is "local" or "local_async")
		{
			return CheckLocalTarget(lowstate);
		}

		var hasTarget = lowstate.TryGetProperty("tgt", out _) || lowstate.TryGetProperty("tgt_type", out _);
		return hasTarget ? $"'{client}' does not take a target." : null;
	}

	private static string? CheckLocalTarget(JsonElement lowstate)
	{
		if (string.IsNullOrWhiteSpace(GetString(lowstate, "tgt")))
		{
			return $"'{GetString(lowstate, "fun")}' needs a non-empty 'tgt'.";
		}

		if (!lowstate.TryGetProperty("tgt_type", out var targetType))
		{
			return null;
		}

		var isPermitted = targetType.ValueKind == JsonValueKind.String && PermittedTargetTypes.Contains(targetType.GetString()!);
		return isPermitted ? null : "'tgt_type' must be 'glob' or 'list'.";
	}

	private static string? CheckArguments(JsonElement lowstate)
	{
		if (!lowstate.TryGetProperty("arg", out var arguments))
		{
			return null;
		}

		if (arguments.ValueKind != JsonValueKind.Array)
		{
			return "'arg' must be an array.";
		}

		// Salt turns a "name=value" positional argument into a keyword argument, which would get past the kwarg checks.
		return arguments.EnumerateArray().All(IsPlainStringArgument) ? null : "every 'arg' entry must be a string without '='.";
	}

	private static bool IsPlainStringArgument(JsonElement argument)
		=> argument.ValueKind == JsonValueKind.String && !argument.GetString()!.Contains('=', StringComparison.Ordinal);

	private static string? CheckKeywordArguments(JsonElement lowstate)
		=> lowstate.TryGetProperty("kwarg", out var keywordArguments) && keywordArguments.ValueKind != JsonValueKind.Object
			? "'kwarg' must be an object."
			: null;

	private static string? CheckListUpgrades(JsonElement lowstate)
	{
		if (GetString(lowstate, "fun") != "pkg.list_upgrades")
		{
			return null;
		}

		if (lowstate.TryGetProperty("arg", out var arguments) && arguments.GetArrayLength() > 0)
		{
			return "'pkg.list_upgrades' takes no positional arguments in read-only mode.";
		}

		return lowstate.TryGetProperty("kwarg", out var keywordArguments)
			&& keywordArguments.TryGetProperty("refresh", out var refresh)
			&& refresh.ValueKind != JsonValueKind.False
				? "'pkg.list_upgrades' with refresh other than false runs apt-get update."
				: null;
	}
}
