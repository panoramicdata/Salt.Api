using System.Collections.Frozen;

namespace Salt.Api;

/// <summary>
/// The read-only allow-list. It works on the JSON that will be sent, so the same check guards the typed client
/// (before a request is built) and the HTTP handler (on the bytes about to leave).
/// </summary>
/// <remarks>
/// Matching is exact and case sensitive on the whole <c>client</c> and <c>fun</c> strings, applies to every element
/// of a multi-command request (one bad element refuses the whole request), and uses no prefixes or wildcards.
/// </remarks>
internal static class ReadOnlyPolicy
{
	/// <summary>Execution functions permitted with <c>local</c> and <c>local_async</c>.</summary>
	internal static readonly FrozenSet<string> LocalFunctions = new[]
	{
		"test.ping",
		"patchreport.status",
		"pkg.list_upgrades",
		"grains.get",
		"grains.items",
	}.ToFrozenSet(StringComparer.Ordinal);

	/// <summary>Runner functions permitted.</summary>
	internal static readonly FrozenSet<string> RunnerFunctions = new[]
	{
		"manage.up",
		"manage.status",
	}.ToFrozenSet(StringComparer.Ordinal);

	/// <summary>Wheel functions permitted.</summary>
	internal static readonly FrozenSet<string> WheelFunctions = new[]
	{
		"key.list_all",
	}.ToFrozenSet(StringComparer.Ordinal);

	/// <summary>The only lowstate keys accepted. Anything else (for example <c>ret</c>, which sends results to a returner) is refused.</summary>
	private static readonly FrozenSet<string> PermittedKeys = new[]
	{
		"client",
		"tgt",
		"tgt_type",
		"fun",
		"arg",
		"kwarg",
		"timeout",
	}.ToFrozenSet(StringComparer.Ordinal);

	private static readonly FrozenSet<string> PermittedTargetTypes = new[]
	{
		"glob",
		"list",
	}.ToFrozenSet(StringComparer.Ordinal);

	/// <summary>The permitted functions for each permitted client. A client that is absent here is refused.</summary>
	private static readonly FrozenDictionary<string, FrozenSet<string>> PermittedFunctionsByClient =
		new Dictionary<string, FrozenSet<string>>(StringComparer.Ordinal)
		{
			["local"] = LocalFunctions,
			["local_async"] = LocalFunctions,
			["runner"] = RunnerFunctions,
			["wheel"] = WheelFunctions,
		}.ToFrozenDictionary(StringComparer.Ordinal);

	/// <summary>
	/// Throws <see cref="SaltReadOnlyViolationException"/> unless every lowstate is permitted.
	/// </summary>
	internal static void EnsureAllowed(IReadOnlyList<Lowstate> lowstates)
	{
		var reason = CheckBody(JsonSerializer.Serialize(lowstates, SaltJson.Options));
		if (reason is not null)
		{
			throw new SaltReadOnlyViolationException(reason);
		}
	}

	/// <summary>
	/// Checks a request: its method, its path relative to the base URL, and for <c>POST /</c> its body.
	/// </summary>
	/// <returns><see langword="null"/> when permitted, otherwise the reason it is refused.</returns>
	internal static string? CheckRequest(HttpMethod method, string relativePath, string? body)
	{
		if (method == HttpMethod.Post)
		{
			return relativePath switch
			{
				"/" => CheckBody(body),
				"/login" or "/logout" => null,
				_ => $"Read-only mode refused POST {relativePath}: only POST /, /login and /logout are permitted.",
			};
		}

		if (method == HttpMethod.Get && IsPermittedGetPath(relativePath))
		{
			return null;
		}

		return $"Read-only mode refused {method} {relativePath}: it is not a permitted read-only endpoint.";
	}

	/// <summary>
	/// Checks a <c>POST /</c> body.
	/// </summary>
	/// <returns><see langword="null"/> when permitted, otherwise the reason it is refused.</returns>
	internal static string? CheckBody(string? body)
	{
		if (string.IsNullOrWhiteSpace(body))
		{
			return "Read-only mode refused POST / with no body.";
		}

		JsonDocument document;
		try
		{
			document = JsonDocument.Parse(body);
		}
		catch (JsonException)
		{
			return "Read-only mode refused POST / whose body is not JSON.";
		}

		using (document)
		{
			var root = document.RootElement;
			if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
			{
				return "Read-only mode refused POST / whose body is not a non-empty JSON array of lowstates.";
			}

			var count = root.GetArrayLength();
			var index = 0;
			foreach (var element in root.EnumerateArray())
			{
				index++;
				var reason = CheckLowstate(element);
				if (reason is not null)
				{
					return $"Read-only mode refused lowstate {index} of {count}: {reason}";
				}
			}
		}

		return null;
	}

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

	private static bool IsPermittedGetPath(string relativePath)
	{
		var segments = relativePath.Trim('/').Split('/');
		return segments switch
		{
			["jobs" or "keys" or "minions" or "events"] => true,
			["jobs" or "keys" or "minions", var id] => id.Length > 0,
			_ => false,
		};
	}

	private static string? GetString(JsonElement element, string name)
		=> element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
			? value.GetString()
			: null;
}
