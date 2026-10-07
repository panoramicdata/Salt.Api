namespace Salt.Api;

/// <summary>
/// Maps Salt response JSON to the typed models.
/// </summary>
internal static partial class SaltResponseParser
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
	/// Maps an object keyed by minion id to per-minion results, deserialising each value as <typeparamref name="T"/>.
	/// </summary>
	internal static MinionResultDictionary<T> ParseMinions<T>(
		JsonElement element,
		IReadOnlyList<string>? expectedMinionIds,
		bool stringIsFailure,
		bool falseIsFailure)
		=> ParseMinions<T>(element, expectedMinionIds, stringIsFailure, falseIsFailure, null);

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
		Func<JsonElement, T>? convert)
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

	private static string? GetString(JsonElement element, string name)
		=> element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
			? value.GetString()
			: null;
}
