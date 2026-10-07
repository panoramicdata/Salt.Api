namespace Salt.Api;

internal static partial class SaltResponseParser
{
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
		if (!TryGetJobInfo(root, out var info))
		{
			return new SaltJobResult { Jid = jid };
		}

		return new SaltJobResult
		{
			Jid = GetString(info, "jid") ?? jid,
			Function = GetString(info, "Function"),
			Arguments = CloneProperty(info, "Arguments"),
			Target = CloneProperty(info, "Target"),
			TargetType = GetString(info, "Target-type"),
			User = GetString(info, "User"),
			StartTime = GetString(info, "StartTime"),
			Minions = ReadStrings(info, "Minions"),
			Returns = ParseJobReturns(info),
		};
	}

	private static bool TryGetJobInfo(JsonElement root, out JsonElement info)
	{
		info = default;
		if (root.ValueKind != JsonValueKind.Object
			|| !root.TryGetProperty("info", out var infoArray)
			|| infoArray.ValueKind != JsonValueKind.Array
			|| infoArray.GetArrayLength() == 0)
		{
			return false;
		}

		info = infoArray[0];
		return info.ValueKind == JsonValueKind.Object;
	}

	private static Dictionary<string, JobMinionReturn> ParseJobReturns(JsonElement info)
	{
		var returns = new Dictionary<string, JobMinionReturn>(StringComparer.Ordinal);
		if (info.TryGetProperty("Result", out var result) && result.ValueKind == JsonValueKind.Object)
		{
			foreach (var property in result.EnumerateObject())
			{
				returns[property.Name] = ParseJobMinionReturn(property.Value);
			}
		}

		return returns;
	}

	private static JobMinionReturn ParseJobMinionReturn(JsonElement value)
		=> value.ValueKind == JsonValueKind.Object && value.TryGetProperty("return", out _)
			? value.Deserialize<JobMinionReturn>(SaltJson.Options)!
			: new JobMinionReturn { Return = value.Clone() };

	private static JsonElement CloneProperty(JsonElement element, string name)
		=> element.TryGetProperty(name, out var value) ? value.Clone() : default;

	private static List<string> ReadStrings(JsonElement element, string name)
		=> element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
			? [.. value.EnumerateArray().Where(m => m.ValueKind == JsonValueKind.String).Select(m => m.GetString()!)]
			: [];

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
}
