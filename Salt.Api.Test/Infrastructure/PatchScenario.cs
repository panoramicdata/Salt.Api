namespace Salt.Api.Test.Infrastructure;

/// <summary>
/// A fake Salt API that answers a patch dry run and a patch apply: the recorded live dry run, and an apply whose
/// result is the same run with every pending state marked as done.
/// </summary>
internal static class PatchScenario
{
	public const string DryRunJid = "20261006143338086181";
	public const string ApplyJid = "20261006150000000001";

	private const string EmptyJobList = """{"return":[{}]}""";

	/// <summary>
	/// Creates a client wired to the scenario.
	/// </summary>
	/// <param name="configure">Changes to the options, if any.</param>
	/// <param name="dryRunResult">The <c>GET /jobs/{jid}</c> body for the dry run; the recorded live one when null.</param>
	/// <param name="jobsList">The <c>GET /jobs</c> body; an empty list when null.</param>
	public static SaltTestContext Create(Action<SaltClientOptions>? configure, string? dryRunResult, string? jobsList)
	{
		var context = new SaltTestContext(configure);
		var dryRun = dryRunResult ?? Fixtures.Load("dryrun-job-result.json");
		var apply = ToApplyResult(dryRun);
		var jobs = jobsList ?? EmptyJobList;
		context.Server
			.On(DryRunSubmit)
			.On(ApplySubmit)
			.On(r => PathRoute(r, $"/jobs/{DryRunJid}", dryRun))
			.On(r => PathRoute(r, $"/jobs/{ApplyJid}", apply))
			.On(r => PathRoute(r, "/jobs", jobs));
		return context;
	}

	private static string ToApplyResult(string dryRun)
		=> dryRun
			.Replace(DryRunJid, ApplyJid, StringComparison.Ordinal)
			.Replace("\"result\": null", "\"result\": true", StringComparison.Ordinal);

	private static HttpResponseMessage? DryRunSubmit(RecordedRequest request)
		=> IsRunContaining(request, "\"test\":true") ? FakeSaltServer.Json(Fixtures.Load("dryrun-async-submit.json")) : null;

	private static HttpResponseMessage? ApplySubmit(RecordedRequest request)
		=> IsRunContaining(request, "state.apply")
			? FakeSaltServer.Json($$$$"""{"return":[{"jid":"{{{{ApplyJid}}}}","minions":["vm-01"]}]}""")
			: null;

	private static bool IsRunContaining(RecordedRequest request, string text)
		=> request.IsRun && request.Body!.Contains(text, StringComparison.Ordinal);

	private static HttpResponseMessage? PathRoute(RecordedRequest request, string path, string body)
		=> request.Path == path ? FakeSaltServer.Json(body) : null;
}
