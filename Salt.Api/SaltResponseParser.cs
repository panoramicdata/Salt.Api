namespace Salt.Api;

/// <summary>
/// Maps Salt response JSON to the typed models.
/// </summary>
internal static class SaltResponseParser
{
	/// <summary>
	/// Returns element <paramref name="index"/> of the <c>return</c> array.
	/// </summary>
	internal static JsonElement GetReturnElement(JsonElement root, int index)
	{
		if (root.ValueKind != JsonValueKind.Object
			|| !root.TryGetProperty("return", out var returnArray)
			|| returnArray.ValueKind != JsonValueKind.Array
			|| returnArray.GetArrayLength() <= index)
		{
			throw new SaltApiException($"The response has no 'return' element {index}.");
		}

		return returnArray[index];
	}

	/// <summary>
	/// Returns the <c>return</c> value when it is not an array (as for <c>GET /keys</c>).
	/// </summary>
	internal static JsonElement GetReturnValue(JsonElement root)
		=> root.ValueKind == JsonValueKind.Object && root.TryGetProperty("return", out var value)
			? value
			: throw new SaltApiException("The response has no 'return' value.");

	/// <summary>
	/// Maps an object keyed by minion id to per-minion results.
	/// </summary>
	/// <param name="element">The object keyed by minion id.</param>
	/// <param name="expectedMinionIds">The exact ids asked for, if known: any of them absent from the response is a failure.</param>
	/// <param name="stringIsFailure">Whether a string value is a failure (Salt reports per-minion errors as strings).</param>
	/// <param name="falseIsFailure">Whether <c>false</c> is a failure.</param>
	/// <param name="convert">Converts a minion's value; throwing <see cref="JsonException"/> or <see cref="FormatException"/> marks it as an unexpected shape.</param>
	internal static MinionResultDictionary<T> ParseMinions<T>(
		JsonElement element,
		IReadOnlyList<string>? expectedMinionIds,
		bool stringIsFailure,
		bool falseIsFailure,
		Func<JsonElement, T>? convert = null)
	{
		if (element.ValueKind != JsonValueKind.Object)
		{
			throw new SaltApiException($"Expected an object keyed by minion id, but the response held {element.ValueKind}.");
		}

		convert ??= value => value.Deserialize<T>(SaltJson.Options)!;
		var results = new List<MinionResult<T>>();
		foreach (var property in element.EnumerateObject())
		{
			results.Add(ParseMinion(property.Name, property.Value, stringIsFailure, falseIsFailure, convert));
		}

		var noMinionsMatched = results.Count == 0;
		if (expectedMinionIds is not null)
		{
			var returned = results.Select(r => r.MinionId).ToHashSet(StringComparer.Ordinal);
			results.AddRange(expectedMinionIds
				.Where(id => !returned.Contains(id))
				.Select(id => MinionResult.Failure<T>(
					id,
					MinionFailureKind.NotReturned,
					"The minion is absent from the result: it did not return in time, is down, or has no accepted key.")));
		}

		return new MinionResultDictionary<T>(results, noMinionsMatched);
	}

	/// <summary>
	/// Whether a string is a failure for this result type: a string is a valid value only for <see cref="string"/> and <see cref="JsonElement"/>.
	/// </summary>
	internal static bool StringIsFailureFor<T>() => typeof(T) != typeof(string) && typeof(T) != typeof(JsonElement);

	/// <summary>
	/// Whether <c>false</c> is a failure for this result type: it is a valid value only for <see cref="bool"/> and <see cref="JsonElement"/>.
	/// </summary>
	internal static bool FalseIsFailureFor<T>() => typeof(T) != typeof(bool) && typeof(T) != typeof(bool?) && typeof(T) != typeof(JsonElement);

	internal static IReadOnlyList<string>? GetListMinionIds(Lowstate lowstate)
	{
		if (lowstate.MinionTarget is { } target)
		{
			return target.MinionIds;
		}

		return lowstate.TargetType == "list" && lowstate.Target is { } expression
			? [.. expression.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]
			: null;
	}

	internal static SaltJob? ParseSubmittedJob(JsonElement element)
	{
		if (element.ValueKind != JsonValueKind.Object
			|| !element.TryGetProperty("jid", out var jid)
			|| jid.ValueKind != JsonValueKind.String
			|| string.IsNullOrEmpty(jid.GetString()))
		{
			return null;
		}

		var minions = element.TryGetProperty("minions", out var minionsElement) && minionsElement.ValueKind == JsonValueKind.Array
			? minionsElement.EnumerateArray().Where(m => m.ValueKind == JsonValueKind.String).Select(m => m.GetString()!).ToList()
			: [];

		return minions.Count == 0 ? null : new SaltJob(jid.GetString()!, minions);
	}

	internal static SaltJobResult ParseJobResult(JsonElement root, string jid)
	{
		if (root.ValueKind != JsonValueKind.Object
			|| !root.TryGetProperty("info", out var infoArray)
			|| infoArray.ValueKind != JsonValueKind.Array
			|| infoArray.GetArrayLength() == 0
			|| infoArray[0].ValueKind != JsonValueKind.Object)
		{
			return new SaltJobResult { Jid = jid };
		}

		var info = infoArray[0];
		var returns = new Dictionary<string, JobMinionReturn>(StringComparer.Ordinal);
		if (info.TryGetProperty("Result", out var result) && result.ValueKind == JsonValueKind.Object)
		{
			foreach (var property in result.EnumerateObject())
			{
				returns[property.Name] = property.Value.ValueKind == JsonValueKind.Object && property.Value.TryGetProperty("return", out _)
					? property.Value.Deserialize<JobMinionReturn>(SaltJson.Options)!
					: new JobMinionReturn { Return = property.Value.Clone() };
			}
		}

		return new SaltJobResult
		{
			Jid = GetString(info, "jid") ?? jid,
			Function = GetString(info, "Function"),
			Arguments = info.TryGetProperty("Arguments", out var arguments) ? arguments.Clone() : default,
			Target = info.TryGetProperty("Target", out var target) ? target.Clone() : default,
			TargetType = GetString(info, "Target-type"),
			User = GetString(info, "User"),
			StartTime = GetString(info, "StartTime"),
			Minions = info.TryGetProperty("Minions", out var minions) && minions.ValueKind == JsonValueKind.Array
				? [.. minions.EnumerateArray().Where(m => m.ValueKind == JsonValueKind.String).Select(m => m.GetString()!)]
				: [],
			Returns = returns,
		};
	}

	internal static IReadOnlyList<JobSummary> ParseJobList(JsonElement root)
	{
		var element = GetReturnElement(root, 0);
		if (element.ValueKind != JsonValueKind.Object)
		{
			throw new SaltApiException("Expected an object keyed by job id.");
		}

		var jobs = new List<JobSummary>();
		foreach (var property in element.EnumerateObject())
		{
			var job = property.Value.Deserialize<JobSummary>(SaltJson.Options)!;
			job.Jid = property.Name;
			jobs.Add(job);
		}

		return [.. jobs.OrderByDescending(j => j.Jid, StringComparer.Ordinal)];
	}

	/// <summary>
	/// Maps one minion's state-run return to a <see cref="PatchStateRun"/>.
	/// </summary>
	/// <exception cref="FormatException">The return is not an object of state results (for example a render error, which Salt returns as a list of strings).</exception>
	internal static PatchStateRun ParseStateRun(JobMinionReturn minionReturn)
	{
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

		var packageChanges = new Dictionary<string, PackageChange>(StringComparer.Ordinal);
		if (states.TryGetProperty(PatchStateRun.PackageStateKey, out var packageState)
			&& packageState.TryGetProperty("changes", out var changes)
			&& changes.ValueKind == JsonValueKind.Object)
		{
			foreach (var change in changes.EnumerateObject())
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
			PackageChanges = packageChanges,
		};
	}

	private static MinionResult<T> ParseMinion<T>(
		string minionId,
		JsonElement value,
		bool stringIsFailure,
		bool falseIsFailure,
		Func<JsonElement, T> convert)
	{
		if (stringIsFailure && value.ValueKind == JsonValueKind.String)
		{
			return MinionResult.Failure<T>(minionId, MinionFailureKind.StringResponse, value.GetString()!);
		}

		if (falseIsFailure && value.ValueKind == JsonValueKind.False)
		{
			return MinionResult.Failure<T>(minionId, MinionFailureKind.FalseResponse, "The minion returned false.");
		}

		try
		{
			return MinionResult.Success(minionId, convert(value.Clone()));
		}
		catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
		{
			return MinionResult.Failure<T>(minionId, MinionFailureKind.UnexpectedShape, $"The minion's value could not be read as {typeof(T).Name}: {ex.Message}");
		}
	}

	private static string DescribeNonStateReturn(JsonElement value)
		=> value.ValueKind switch
		{
			JsonValueKind.String => value.GetString()!,
			JsonValueKind.Array => string.Join(Environment.NewLine, value.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText())),
			_ => $"The minion returned {value.ValueKind} instead of state results.",
		};

	private static string? GetString(JsonElement element, string name)
		=> element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
			? value.GetString()
			: null;
}
