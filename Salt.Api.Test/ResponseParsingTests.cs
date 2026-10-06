namespace Salt.Api.Test;

/// <summary>
/// Replays each recorded live response and checks the typed result, including the per-minion failure shapes.
/// </summary>
public class ResponseParsingTests
{
	private static readonly string[] AllMinions = ["vm-02", "node-05", "node-03", "node-04", "node-06", "vm-01"];

	[Fact]
	public async Task PingAll_ReturnsTrueForEveryMinion()
	{
		using var context = new SaltTestContext();
		context.Server.OnRun("ping-all.json");

		var result = await context.Client.PingAsync(MinionTarget.All, TestContext.Current.CancellationToken);

		result.Keys.Should().BeEquivalentTo(AllMinions);
		result.AllSucceeded.Should().BeTrue();
		result.Values.Should().OnlyContain(r => r.Value);
	}

	[Fact]
	public async Task Ping_SendsAListTarget_AndTheDefaultMinionTimeout()
	{
		using var context = new SaltTestContext();
		context.Server.OnRun("ping-one.json");

		await context.Client.PingAsync(MinionTarget.List("vm-01"), TestContext.Current.CancellationToken);

		using var body = JsonDocument.Parse(context.Server.NonLoginRequests.Single().Body!);
		var lowstate = body.RootElement.EnumerateArray().Single();
		lowstate.GetProperty("client").GetString().Should().Be("local");
		lowstate.GetProperty("tgt").GetString().Should().Be("vm-01");
		lowstate.GetProperty("tgt_type").GetString().Should().Be("list");
		lowstate.GetProperty("fun").GetString().Should().Be("test.ping");
		lowstate.GetProperty("timeout").GetInt32().Should().Be(120);
	}

	[Fact]
	public async Task AGlobThatMatchesNothing_IsNeverAllClean()
	{
		using var context = new SaltTestContext();
		context.Server.OnRun("ping-nomatch.json");

		var result = await context.Client.PingAsync(MinionTarget.Glob("no-such-*"), TestContext.Current.CancellationToken);

		result.Should().BeEmpty();
		result.NoMinionsMatched.Should().BeTrue();
		result.AllSucceeded.Should().BeFalse();
	}

	[Fact]
	public async Task AListIdThatMatchesNothing_IsAFailure()
	{
		using var context = new SaltTestContext();
		context.Server.OnRun("ping-nomatch.json");

		var result = await context.Client.GetPatchStatusAsync(MinionTarget.List("vm-0l"), TestContext.Current.CancellationToken);

		result.NoMinionsMatched.Should().BeTrue();
		result["vm-0l"].Succeeded.Should().BeFalse();
		result["vm-0l"].FailureKind.Should().Be(MinionFailureKind.NotReturned);
		result.AllSucceeded.Should().BeFalse();
	}

	[Fact]
	public async Task AnIdAskedForAndAbsent_IsAFailure_AlongsideTheOthers()
	{
		using var context = new SaltTestContext();
		context.Server.OnRun("ping-one.json");

		var result = await context.Client.PingAsync(MinionTarget.List("vm-01", "vm-09"), TestContext.Current.CancellationToken);

		result["vm-01"].Succeeded.Should().BeTrue();
		result["vm-09"].FailureKind.Should().Be(MinionFailureKind.NotReturned);
		result.Failures.Should().ContainSingle();
		result.NoMinionsMatched.Should().BeFalse();
	}

	[Fact]
	public async Task AStringPerMinion_IsAFailure_NotAPatchStatus()
	{
		using var context = new SaltTestContext();
		context.Server.OnRun("unknown-function.json");

		var result = await context.Client.GetPatchStatusAsync(MinionTarget.List("vm-01"), TestContext.Current.CancellationToken);

		var minion = result["vm-01"];
		minion.Succeeded.Should().BeFalse();
		minion.FailureKind.Should().Be(MinionFailureKind.StringResponse);
		minion.FailureText.Should().Be("'no.such.function' is not available.");
		minion.Value.Should().BeNull();
	}

	[Fact]
	public async Task AMinionThatDidNotReturn_IsAFailure()
	{
		using var context = new SaltTestContext();
		context.Server.On(r => r.IsRun ? FakeSaltServer.Json("""{"return":[{"vm-01":"Minion did not return. [No response]"}]}""") : null);

		var result = await context.Client.PingAsync(MinionTarget.List("vm-01"), TestContext.Current.CancellationToken);

		result["vm-01"].FailureKind.Should().Be(MinionFailureKind.StringResponse);
		result["vm-01"].FailureText.Should().StartWith("Minion did not return");
	}

	[Fact]
	public async Task FalseFromPing_IsAFailure()
	{
		using var context = new SaltTestContext();
		context.Server.On(r => r.IsRun ? FakeSaltServer.Json("""{"return":[{"vm-01":false}]}""") : null);

		var result = await context.Client.PingAsync(MinionTarget.List("vm-01"), TestContext.Current.CancellationToken);

		result["vm-01"].FailureKind.Should().Be(MinionFailureKind.FalseResponse);
	}

	[Fact]
	public async Task AnUnexpectedShape_IsAFailure_NotAnException()
	{
		using var context = new SaltTestContext();
		context.Server.On(r => r.IsRun ? FakeSaltServer.Json("""{"return":[{"vm-01":[1,2,3],"vm-02":{"pending_count":"lots"}}]}""") : null);

		var result = await context.Client.GetPatchStatusAsync(MinionTarget.List("vm-01", "vm-02"), TestContext.Current.CancellationToken);

		result.Values.Should().OnlyContain(r => r.FailureKind == MinionFailureKind.UnexpectedShape);
	}

	[Fact]
	public async Task PatchReportAll_IsReadForEveryMinion()
	{
		using var context = new SaltTestContext();
		context.Server.OnRun("patchreport-all.json");

		var result = await context.Client.GetPatchStatusAsync(MinionTarget.All, TestContext.Current.CancellationToken);

		result.Keys.Should().BeEquivalentTo(AllMinions);
		result.AllSucceeded.Should().BeTrue();

		var node06 = result["node-06"].Value!;
		node06.PendingCount.Should().Be(1);
		node06.PendingUpgrades.Should().ContainKey("sosreport");
		node06.PatchingNeeded.Should().BeTrue();

		var node04 = result["node-04"].Value!;
		node04.PendingCount.Should().Be(3);
		node04.PendingUpgrades.Should().HaveCount(3);

		var vm01 = result["vm-01"].Value!;
		vm01.KernelPackageNeedsReboot.Should().BeTrue();
		vm01.RebootRequiredFile.Should().BeTrue();
		vm01.RebootRequired.Should().BeTrue();
		vm01.Held.Should().Equal("salt-common", "salt-minion");
		vm01.LastUpgradeSource.Should().Be("/var/log/dpkg.log");
		vm01.LastUpgrade.Should().NotBeNull();

		result.Values.Select(v => v.Value!).Count(v => v.KeptBack.Contains("dnsmasq-base")).Should().Be(3);
		result.Values.Select(v => v.Value!).Where(v => v.KeptBack.Count > 0).Should().OnlyContain(v => v.NeedsAttention);
	}

	[Fact]
	public async Task PatchReportOne_IsRead()
	{
		using var context = new SaltTestContext();
		context.Server.OnRun("patchreport-one.json");

		var result = await context.Client.GetPatchStatusAsync(MinionTarget.List("vm-01"), TestContext.Current.CancellationToken);

		var status = result["vm-01"].Value!;
		status.PendingCount.Should().Be(0);
		status.PendingUpgrades.Should().BeEmpty();
		status.LastUpgradeEpoch.Should().Be(1791267565);
	}

	[Fact]
	public async Task GrainsGet_ReturnsTheGrainPerMinion()
	{
		using var context = new SaltTestContext();
		context.Server.OnRun("grains-patch-class.json");

		var result = await context.Client.GetGrainAsync(MinionTarget.All, "patch_class", TestContext.Current.CancellationToken);

		result.AllSucceeded.Should().BeTrue();
		result["vm-01"].Value.GetString().Should().Be("pvg");
		result["node-03"].Value.GetString().Should().Be("k8s_node");
		using var body = JsonDocument.Parse(context.Server.NonLoginRequests.Single().Body!);
		body.RootElement[0].GetProperty("arg")[0].GetString().Should().Be("patch_class");
	}

	[Fact]
	public async Task ManageUp_ReturnsTheConnectedMinions()
	{
		using var context = new SaltTestContext();
		context.Server.OnRun("runner-manage-up.json");

		var minions = await context.Client.GetMinionsUpAsync(TestContext.Current.CancellationToken);

		minions.Should().BeEquivalentTo(AllMinions);
		using var body = JsonDocument.Parse(context.Server.NonLoginRequests.Single().Body!);
		body.RootElement[0].GetProperty("client").GetString().Should().Be("runner");
		body.RootElement[0].TryGetProperty("tgt", out _).Should().BeFalse();
	}

	[Fact]
	public async Task ManageStatus_ReturnsUpAndDown()
	{
		using var context = new SaltTestContext();
		context.Server.On(r => r.IsRun ? FakeSaltServer.Json("""{"return":[{"up":["vm-01"],"down":["vm-02"]}]}""") : null);

		var status = await context.Client.GetMinionStatusAsync(TestContext.Current.CancellationToken);

		status.Up.Should().Equal("vm-01");
		status.Down.Should().Equal("vm-02");
	}

	[Fact]
	public async Task Keys_AreReadByState()
	{
		using var context = new SaltTestContext();
		context.Server.OnGet("/keys", "keys.json");

		var keys = await context.Client.GetKeysAsync(TestContext.Current.CancellationToken);

		keys.Accepted.Should().BeEquivalentTo(AllMinions);
		keys.Pending.Should().BeEmpty();
		keys.Rejected.Should().BeEmpty();
		keys.Denied.Should().BeEmpty();
		keys.Local.Should().Equal("salt-master.pem", "salt-master.pub");
	}

	[Fact]
	public async Task WheelKeyListAll_CanBeReadThroughExecute()
	{
		using var context = new SaltTestContext();
		context.Server.OnRun("wheel-key-list-all.json");

		var element = await context.Client.ExecuteAsync<JsonElement>(Lowstate.KeyListAll(), TestContext.Current.CancellationToken);
		var keys = element.GetProperty("data").GetProperty("return").Deserialize<SaltKeys>()!;

		keys.Accepted.Should().HaveCount(6);
		element.GetProperty("data").GetProperty("success").GetBoolean().Should().BeTrue();
	}

	[Fact]
	public async Task OneMinion_IsRead()
	{
		using var context = new SaltTestContext();
		context.Server.OnGet("/minions/vm-01", "minion-one.json");

		var minion = await context.Client.GetMinionAsync("vm-01", TestContext.Current.CancellationToken);

		minion.Succeeded.Should().BeTrue();
		minion.Value.GetProperty("patch_class").GetString().Should().Be("pvg");
	}

	[Fact]
	public void TheAllMinionsResponse_ParsesThoughNoMethodExposesIt()
	{
		using var document = JsonDocument.Parse(Fixtures.Load("minions.json"));

		var result = SaltResponseParser.ParseMinions<JsonElement>(
			SaltResponseParser.GetReturnElement(document.RootElement, 0), null, stringIsFailure: true, falseIsFailure: true);

		result.Should().HaveCount(6);
		typeof(SaltClient).GetMethods().Should().NotContain(m => m.Name == "GetMinionsAsync", "GET /minions returns every grain of every minion");
	}

	[Fact]
	public async Task Jobs_AreListedNewestFirst()
	{
		using var context = new SaltTestContext();
		context.Server.OnGet("/jobs", "jobs-list.json");

		var jobs = await context.Client.GetJobsAsync(TestContext.Current.CancellationToken);

		jobs.Should().HaveCount(4);
		jobs.Select(j => j.Jid).Should().BeInDescendingOrder(StringComparer.Ordinal);
		var first = jobs[0];
		first.Jid.Should().Be("20261006143351748113");
		first.Function.Should().Be("runner.jobs.list_job");
		first.TargetType.Should().Be("list");
		first.User.Should().Be("api-user");
		first.StartTime.Should().Be("2026, Oct 06 14:33:51.748113");
	}

	[Fact]
	public async Task ACompletedDryRunJob_IsRead()
	{
		using var context = new SaltTestContext();
		context.Server.OnGet("/jobs/20261006143338086181", "dryrun-job-result.json");

		var job = await context.Client.GetJobAsync("20261006143338086181", TestContext.Current.CancellationToken);

		job.Function.Should().Be("state.apply");
		job.Minions.Should().Equal("vm-01");
		job.IsComplete.Should().BeTrue();
		job.MissingMinions.Should().BeEmpty();
		var minionReturn = job.Returns["vm-01"];
		minionReturn.RetCode.Should().Be(0);
		minionReturn.Success.Should().BeTrue();
		minionReturn.Outputter.Should().Be("highstate");

		var run = SaltResponseParser.ParseStateRun(minionReturn);
		run.States.Should().HaveCount(5);
		run.States.Select(s => s.RunNumber).Should().BeInAscendingOrder();
		run.HasFailures.Should().BeFalse();
		run.PackageChanges.Should().HaveCount(3);
		run.PackageChanges["libfreetype6"].Should().Be(new PackageChange("2.11.1+dfsg-1ubuntu0.3", "2.11.1+dfsg-1ubuntu0.4"));
		run.ChangedStates.Select(s => s.Id).Should().BeEquivalentTo("patch-needrestart-config", "patch-apply");
		var packageState = run.States.Single(s => s.Key == PatchStateRun.PackageStateKey);
		packageState.Result.Should().BeNull("in a dry run, null means the state would change");
		packageState.Comment.Should().Be("System update will be performed");
		packageState.DurationMilliseconds.Should().BeApproximately(22223.479, 0.001);
	}

	[Fact]
	public void ASubmittedJob_IsRead()
	{
		using var document = JsonDocument.Parse(Fixtures.Load("dryrun-async-submit.json"));

		var job = SaltResponseParser.ParseSubmittedJob(SaltResponseParser.GetReturnElement(document.RootElement, 0));

		job!.Jid.Should().Be("20261006143338086181");
		job.Minions.Should().Equal("vm-01");
	}

	[Theory]
	[InlineData("""{}""")]
	[InlineData("""{"jid":"20261006143338086181","minions":[]}""")]
	public void ASubmitThatMatchedNothing_HasNoJob(string json)
	{
		using var document = JsonDocument.Parse(json);

		SaltResponseParser.ParseSubmittedJob(document.RootElement).Should().BeNull();
	}

	[Fact]
	public void TheLogoutResponse_IsTheRecordedText()
	{
		using var document = JsonDocument.Parse(Fixtures.Load("logout.json"));

		SaltResponseParser.GetReturnValue(document.RootElement).GetString().Should().Be("Your token has been cleared");
	}

	[Fact]
	public async Task AResponseWithoutReturn_IsAnApiError()
	{
		using var context = new SaltTestContext();
		context.Server.On(r => r.IsRun ? FakeSaltServer.Json("""{"something":"else"}""") : null);

		var act = () => context.Client.PingAsync(MinionTarget.All, TestContext.Current.CancellationToken);

		await act.Should().ThrowAsync<SaltApiException>();
	}
}
