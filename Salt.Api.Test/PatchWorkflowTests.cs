namespace Salt.Api.Test;

/// <summary>
/// Dry run, job polling and the guards on a real apply. A real apply is only ever sent to the fake server.
/// </summary>
public class PatchWorkflowTests
{
	private const string DryRunJid = "20261006143338086181";
	private const string ApplyJid = "20261006150000000001";

	private static readonly string RunningDryRunJob =
		$$$$"""{"info":[{"jid":"{{{{DryRunJid}}}}","Function":"state.apply","Arguments":["patch.apply",{"__kwarg__":true,"test":true}],"Minions":["vm-01"],"Result":{}}],"return":[{}]}""";

	[Fact]
	public async Task DryRun_SubmitsTheTestState_AsAJob_AndPollsUntilComplete()
	{
		using var context = new SaltTestContext();
		var polls = 0;
		context.Server
			.OnRun("dryrun-async-submit.json")
			.On(r => r.Path == $"/jobs/{DryRunJid}"
				? FakeSaltServer.Json(++polls < 3 ? RunningDryRunJob : Fixtures.Load("dryrun-job-result.json"))
				: null);

		var result = await context.Client.PatchDryRunAsync(
			new PatchDryRunRequest { Target = MinionTarget.List("vm-01") },
			TestContext.Current.CancellationToken);

		result.IsDryRun.Should().BeTrue();
		result.Jid.Should().Be(DryRunJid);
		result.TimedOut.Should().BeFalse();
		result.AllSucceeded.Should().BeTrue();
		result.Minions["vm-01"].Value!.PackageChanges.Should().HaveCount(3);
		polls.Should().Be(3);
		context.Delays.Should().Equal(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));

		using var body = JsonDocument.Parse(context.Server.NonLoginRequests.First(r => r.IsRun).Body!);
		var lowstate = body.RootElement.EnumerateArray().Single();
		lowstate.GetProperty("client").GetString().Should().Be("local_async");
		lowstate.GetProperty("fun").GetString().Should().Be("state.apply");
		lowstate.GetProperty("arg").EnumerateArray().Select(a => a.GetString()).Should().Equal("patch.apply");
		lowstate.GetProperty("kwarg").GetProperty("test").GetBoolean().Should().BeTrue();
		lowstate.GetProperty("tgt_type").GetString().Should().Be("list");
	}

	[Fact]
	public async Task DryRun_ThatPassesTheDeadline_ReportsTheMinionAsNotReturned()
	{
		using var context = new SaltTestContext();
		context.Server
			.OnRun("dryrun-async-submit.json")
			.On(r => r.Path == $"/jobs/{DryRunJid}" ? FakeSaltServer.Json(RunningDryRunJob) : null);

		var result = await context.Client.PatchDryRunAsync(
			new PatchDryRunRequest { Target = MinionTarget.List("vm-01"), Timeout = TimeSpan.FromSeconds(12) },
			TestContext.Current.CancellationToken);

		result.TimedOut.Should().BeTrue();
		result.AllSucceeded.Should().BeFalse();
		result.Minions["vm-01"].FailureKind.Should().Be(MinionFailureKind.NotReturned);
		result.Minions["vm-01"].FailureText.Should().Contain(DryRunJid);
		context.Delays.Should().Equal(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2));
	}

	[Fact]
	public async Task DryRun_OnATargetThatMatchesNothing_ReportsFailures()
	{
		using var context = new SaltTestContext();
		context.Server.On(r => r.IsRun ? FakeSaltServer.Json("""{"return":[{}]}""") : null);

		var result = await context.Client.PatchDryRunAsync(
			new PatchDryRunRequest { Target = MinionTarget.List("vm-0l") },
			TestContext.Current.CancellationToken);

		result.Jid.Should().BeNull();
		result.Minions.NoMinionsMatched.Should().BeTrue();
		result.Minions["vm-0l"].Succeeded.Should().BeFalse();
		result.AllSucceeded.Should().BeFalse();
	}

	[Fact]
	public async Task DryRun_WithARenderError_IsAFailure()
	{
		using var context = new SaltTestContext();
		context.Server
			.OnRun("dryrun-async-submit.json")
			.On(r => r.Path == $"/jobs/{DryRunJid}"
				? FakeSaltServer.Json($$$$"""{"info":[{"jid":"{{{{DryRunJid}}}}","Minions":["vm-01"],"Result":{"vm-01":{"return":["Rendering SLS 'base:patch.apply' failed: patch-refuse"],"retcode":1,"success":false}}}]}""")
				: null);

		var result = await context.Client.PatchDryRunAsync(
			new PatchDryRunRequest { Target = MinionTarget.List("vm-01") },
			TestContext.Current.CancellationToken);

		result.Minions["vm-01"].Succeeded.Should().BeFalse();
		result.Minions["vm-01"].FailureText.Should().Contain("patch-refuse");
	}

	[Fact]
	public async Task WaitForJob_ThrowsWithThePartialResult_AtTheDeadline()
	{
		using var context = new SaltTestContext();
		context.Server.On(r => r.Path == $"/jobs/{DryRunJid}" ? FakeSaltServer.Json(RunningDryRunJob) : null);

		var act = () => context.Client.WaitForJobAsync(new SaltJob(DryRunJid, ["vm-01"]), TimeSpan.FromSeconds(7), TestContext.Current.CancellationToken);

		var thrown = await act.Should().ThrowAsync<SaltJobTimeoutException>();
		thrown.Which.LastResult!.MissingMinions.Should().Equal("vm-01");
	}

	[Fact]
	public async Task WaitForJob_StopsWhenCancelled()
	{
		using var context = new SaltTestContext();
		context.Server.On(r => r.Path == $"/jobs/{DryRunJid}" ? FakeSaltServer.Json(RunningDryRunJob) : null);
		using var cancellation = new CancellationTokenSource();
		cancellation.Cancel();

		var act = () => context.Client.WaitForJobAsync(new SaltJob(DryRunJid, ["vm-01"]), TimeSpan.FromMinutes(5), cancellation.Token);

		await act.Should().ThrowAsync<OperationCanceledException>();
	}

	[Fact]
	public async Task Submit_TurnsALocalCallIntoAJob()
	{
		using var context = new SaltTestContext(o => o.ReadOnly = true);
		context.Server.On(r => r.IsRun ? FakeSaltServer.Json("""{"return":[{"jid":"1","minions":["vm-01"]}]}""") : null);

		var job = await context.Client.SubmitAsync(Lowstate.PatchStatus(MinionTarget.List("vm-01")), TestContext.Current.CancellationToken);

		job.Jid.Should().Be("1");
		using var body = JsonDocument.Parse(context.Server.NonLoginRequests.Single().Body!);
		body.RootElement[0].GetProperty("client").GetString().Should().Be("local_async");
		body.RootElement[0].TryGetProperty("timeout", out _).Should().BeFalse();
	}

	[Fact]
	public async Task Apply_AfterADryRun_SendsTheRealState_AndReadsTheResult()
	{
		using var context = CreateApplyContext();
		await DryRunAsync(context);

		var result = await context.Client.PatchApplyAsync(ValidApply(), TestContext.Current.CancellationToken);

		result.IsDryRun.Should().BeFalse();
		result.Jid.Should().Be(ApplyJid);
		result.AllSucceeded.Should().BeTrue();
		result.Minions["vm-01"].Value!.PackageChanges.Should().ContainKey("libfreetype6");

		var applyRequest = context.Server.NonLoginRequests.Where(r => r.IsRun).Last();
		using var body = JsonDocument.Parse(applyRequest.Body!);
		var lowstate = body.RootElement.EnumerateArray().Single();
		lowstate.GetProperty("client").GetString().Should().Be("local_async");
		lowstate.GetProperty("fun").GetString().Should().Be("state.apply");
		lowstate.GetProperty("tgt").GetString().Should().Be("vm-01");
		lowstate.GetProperty("tgt_type").GetString().Should().Be("list");
		lowstate.TryGetProperty("kwarg", out _).Should().BeFalse("a real apply sends no test flag");
		context.Logger.Messages.Should().Contain(m => m.Contains("REAL patch apply", StringComparison.Ordinal) && m.Contains("CHG-1", StringComparison.Ordinal));
	}

	[Fact]
	public async Task Apply_NeedsAFreshDryRun_ForTheNextApply()
	{
		using var context = CreateApplyContext();
		await DryRunAsync(context);
		await context.Client.PatchApplyAsync(ValidApply(), TestContext.Current.CancellationToken);

		var act = () => context.Client.PatchApplyAsync(ValidApply(), TestContext.Current.CancellationToken);

		await act.Should().ThrowAsync<SaltPatchGuardException>().WithMessage("*dry run*");
	}

	public static TheoryData<string, PatchApplyRequest> GuardCases => new()
	{
		{ "*ConfirmRealApply*", new PatchApplyRequest { MinionIds = ["vm-01"], ChangeReference = "CHG-1", ConfirmRealApply = false } },
		{ "*ChangeReference*", new PatchApplyRequest { MinionIds = ["vm-01"], ChangeReference = " ", ConfirmRealApply = true } },
		{ "*at least one*", new PatchApplyRequest { MinionIds = [], ChangeReference = "CHG-1", ConfirmRealApply = true } },
		{ "*exact minion ids*", new PatchApplyRequest { MinionIds = ["vm-*"], ChangeReference = "CHG-1", ConfirmRealApply = true } },
		{ "*exact minion ids*", new PatchApplyRequest { MinionIds = ["*"], ChangeReference = "CHG-1", ConfirmRealApply = true } },
		{ "*exact minion ids*", new PatchApplyRequest { MinionIds = ["vm-01,vm-02"], ChangeReference = "CHG-1", ConfirmRealApply = true } },
		{ "*exact minion ids*", new PatchApplyRequest { MinionIds = ["vm-0[1-2]"], ChangeReference = "CHG-1", ConfirmRealApply = true } },
		{ "*exact minion ids*", new PatchApplyRequest { MinionIds = ["vm-01 "], ChangeReference = "CHG-1", ConfirmRealApply = true } },
		{ "*AllowMoreThanEightMinions*", new PatchApplyRequest { MinionIds = [.. Enumerable.Range(1, 9).Select(i => $"vm-{i:00}")], ChangeReference = "CHG-1", ConfirmRealApply = true } },
		{ "*dry run*", new PatchApplyRequest { MinionIds = ["vm-02"], ChangeReference = "CHG-1", ConfirmRealApply = true } },
	};

	[Theory]
	[MemberData(nameof(GuardCases))]
	public async Task Apply_GuardsRefuse_BeforeAnyApplyIsSent(string expectedMessage, PatchApplyRequest request)
	{
		using var context = CreateApplyContext();
		await DryRunAsync(context);
		var requestsBefore = context.Server.Requests.Count;

		var act = () => context.Client.PatchApplyAsync(request, TestContext.Current.CancellationToken);

		await act.Should().ThrowAsync<SaltPatchGuardException>().WithMessage(expectedMessage);
		context.Server.Requests.Should().HaveCount(requestsBefore, "a guard must refuse before any request");
	}

	[Fact]
	public async Task Apply_OnMoreThanEightMinions_IsPermittedWithTheFlag()
	{
		using var context = CreateApplyContext();
		var ids = Enumerable.Range(1, 9).Select(i => $"vm-{i:00}").ToList();

		var act = () => context.Client.PatchApplyAsync(
			new PatchApplyRequest { MinionIds = ids, ChangeReference = "CHG-1", ConfirmRealApply = true, AllowMoreThanEightMinions = true },
			TestContext.Current.CancellationToken);

		// It passes the size guard and stops at the next one.
		await act.Should().ThrowAsync<SaltPatchGuardException>().WithMessage("*dry run*");
	}

	[Fact]
	public async Task Apply_RefusesAnExpiredDryRun()
	{
		using var context = CreateApplyContext();
		await DryRunAsync(context);
		context.Clock.Advance(TimeSpan.FromMinutes(61));

		var act = () => context.Client.PatchApplyAsync(ValidApply(), TestContext.Current.CancellationToken);

		await act.Should().ThrowAsync<SaltPatchGuardException>().WithMessage("*within the last 60 minutes*");
	}

	[Fact]
	public async Task Apply_RefusesWhenTheDryRunHadAFailedState()
	{
		using var context = CreateApplyContext(dryRunResult: Fixtures.Load("dryrun-job-result.json")
			.Replace("\"result\": true,\n              \"comment\": \"Success!\"", "\"result\": false,\n              \"comment\": \"patch-refuse\"", StringComparison.Ordinal)
			.Replace("\"result\": true,\r\n              \"comment\": \"Success!\"", "\"result\": false,\r\n              \"comment\": \"patch-refuse\"", StringComparison.Ordinal));
		var dryRun = await DryRunAsync(context);
		dryRun.Minions["vm-01"].Value!.HasFailures.Should().BeTrue();

		var act = () => context.Client.PatchApplyAsync(ValidApply(), TestContext.Current.CancellationToken);

		await act.Should().ThrowAsync<SaltPatchGuardException>().WithMessage("*dry run*");
	}

	[Fact]
	public async Task Apply_RefusesWhileAnotherPatchRunHasNotReturned_OnTheSameMinion()
	{
		const string busyJid = "20261006145900000000";
		using var context = CreateApplyContext(jobsList:
			$$$$"""{"return":[{"{{{{busyJid}}}}":{"Function":"state.apply","Arguments":["patch.apply"],"Target":"vm-01","Target-type":"list","User":"someone","StartTime":"2026, Oct 06 14:59:00.000000"}}]}""");
		context.Server.On(r => r.Path == $"/jobs/{busyJid}"
			? FakeSaltServer.Json($$$$"""{"info":[{"jid":"{{{{busyJid}}}}","Function":"state.apply","Minions":["vm-01"],"Result":{}}]}""")
			: null);
		await DryRunAsync(context);

		var act = () => context.Client.PatchApplyAsync(ValidApply(), TestContext.Current.CancellationToken);

		await act.Should().ThrowAsync<SaltPatchGuardException>().WithMessage($"*{busyJid}*apt lock*");
		context.Server.NonLoginRequests.Where(r => r.IsRun).Should().ContainSingle("only the dry run was submitted");
	}

	[Fact]
	public async Task Apply_IgnoresFinishedPatchRuns_AndOtherFunctions()
	{
		using var context = CreateApplyContext(jobsList: Fixtures.Load("jobs-list.json"));
		await DryRunAsync(context);

		var result = await context.Client.PatchApplyAsync(ValidApply(), TestContext.Current.CancellationToken);

		result.AllSucceeded.Should().BeTrue();
	}

	private static PatchApplyRequest ValidApply()
		=> new() { MinionIds = ["vm-01"], ChangeReference = "CHG-1", ConfirmRealApply = true };

	private static async Task<PatchRunResult> DryRunAsync(SaltTestContext context)
		=> await context.Client.PatchDryRunAsync(new PatchDryRunRequest { Target = MinionTarget.List("vm-01") }, TestContext.Current.CancellationToken);

	private static SaltTestContext CreateApplyContext(string? jobsList = null, string? dryRunResult = null)
		=> PatchScenario.Create(null, dryRunResult, jobsList);
}
