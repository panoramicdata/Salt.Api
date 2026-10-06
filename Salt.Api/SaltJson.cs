namespace Salt.Api;

/// <summary>
/// The JSON settings used for every request and response.
/// </summary>
internal static class SaltJson
{
	internal static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.General)
	{
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
	};
}
