namespace Salt.Api.Test;

/// <summary>
/// The options that name the estate's Salt content: the patch state, its package state id, and the patch status function.
/// </summary>
public class PatchContentOptionsTests
{
	private const string DryRunJid = "20261006143338086181";
	private const string ApplyJid = "20261006150000000001";

	private static SaltClientOptions Valid() => new() { BaseUrl = "https://salt.example.test", Username = "u", Password = "p" };

	[Fact]
	public void TheDefaults_AreTheOriginalContentNames()
	{
		var options = new SaltClientOptions();

		options.PatchStateName.Should().Be("patch.apply");
		options.PackageStateId.Should().Be("patch-apply");
		options.PatchStatusFunction.Should().Be("patchreport.status");
	}

	public static TheoryData<string, Action<SaltClientOptions>> InvalidContentOptions => new()
	{
		{ "*PatchStateName*", o => o.PatchStateName = "" },
		{ "*PatchStateName*", o => o.PatchStateName = " " },
		{ "*PatchStateName*", o => o.PatchStateName = "patch apply" },
		{ "*PatchStateName*", o => o.PatchStateName = "test=True" },
		{ "*PatchStateName*", o => o.PatchStateName = "patch.apply,patch.reboot" },
		{ "*PatchStateName*", o => o.PatchStateName = ".patch" },
		{ "*PatchStateName*", o => o.PatchStateName = "-patch" },
		{ "*PatchStateName*", o => o.PatchStateName = "patch.apply\n" },
		{ "*PatchStateName*", o => o.PatchStateName = null! },
		{ "*PackageStateId*", o => o.PackageStateId = "" },
		{ "*PackageStateId*", o => o.PackageStateId = " " },
		{ "*PackageStateId*", o => o.PackageStateId = "patch apply" },
		{ "*PackageStateId*", o => o.PackageStateId = "patch-apply\t" },
		{ "*PackageStateId*", o => o.PackageStateId = null! },
		{ "*PatchStatusFunction*", o => o.PatchStatusFunction = "" },
		{ "*PatchStatusFunction*", o => o.PatchStatusFunction = "status" },
		{ "*PatchStatusFunction*", o => o.PatchStatusFunction = "a.b.c" },
		{ "*PatchStatusFunction*", o => o.PatchStatusFunction = "1report.status" },
		{ "*PatchStatusFunction*", o => o.PatchStatusFunction = "report.status " },
		{ "*PatchStatusFunction*", o => o.PatchStatusFunction = "report.st-atus" },
		{ "*PatchStatusFunction*", o => o.PatchStatusFunction = "report.status\n" },
		{ "*PatchStatusFunction*", o => o.PatchStatusFunction = null! },
	};

	[Theory]
	[MemberData(nameof(InvalidContentOptions))]
	public void Validate_RejectsInvalidContentNames(string expectedMessage, Action<SaltClientOptions> change)
	{
		var options = Valid();
		change(options);

		var act = () => new SaltClient(options);

		act.Should().Throw<SaltConfigurationException>().WithMessage(expectedMessage);
	}

	[Fact]
	public void Validate_AcceptsCustomContentNames()
	{
		var options = Valid();
		options.PatchStateName = "site_patching.apply-all";
		options.PackageStateId = "site:upgrade-all";
		options.PatchStatusFunction = "_site_report.status_v2";

		options.Invoking(o => o.Validate()).Should().NotThrow();
	}

	[Fact]
	public async Task DryRun_AppliesTheConfiguredState()
	{
		using var context = CreatePatchContext(o => o.PatchStateName = "site.patch");

		await DryRunAsync(context);

		using var body = JsonDocument.Parse(context.Server.NonLoginRequests.First(r => r.IsRun).Body!);
		var lowstate = body.RootElement.EnumerateArray().Single();
		lowstate.GetProperty("fun").GetString().Should().Be("state.apply");
		lowstate.GetProperty("arg").EnumerateArray().Select(a => a.GetString()).Should().Equal("site.patch");
		lowstate.GetProperty("kwarg").GetProperty("test").GetBoolean().Should().BeTrue();
	}

	[Fact]
	public async Task Apply_AppliesTheConfiguredState()
	{
		using var context = CreatePatchContext(o => o.PatchStateName = "site.patch");
		await DryRunAsync(context);

		await context.Client.PatchApplyAsync(ValidApply(), TestContext.Current.CancellationToken);

		using var body = JsonDocument.Parse(context.Server.NonLoginRequests.Where(r => r.IsRun).Last().Body!);
		var lowstate = body.RootElement.EnumerateArray().Single();
		lowstate.GetProperty("arg").EnumerateArray().Select(a => a.GetString()).Should().Equal("site.patch");
		lowstate.TryGetProperty("kwarg", out _).Should().BeFalse();
	}

	[Fact]
	public async Task DryRun_ReadsThePackageState_WithTheConfiguredId()
	{
		using var context = CreatePatchContext(o => o.PackageStateId = "site-upgrade", RenamedPackageStateResult());

		var result = await DryRunAsync(context);

		var run = result.Minions["vm-01"].Value!;
		run.PackageState.Should().NotBeNull();
		run.PackageState!.Id.Should().Be("site-upgrade");
		run.PackageChanges.Should().HaveCount(3).And.ContainKey("libfreetype6");
	}

	[Fact]
	public async Task DryRun_WithTheDefaultId_FindsNoPackageState_WhenTheStateIsNamedDifferently()
	{
		using var context = CreatePatchContext(dryRunResult: RenamedPackageStateResult());

		var result = await DryRunAsync(context);

		var run = result.Minions["vm-01"].Value!;
		run.PackageState.Should().BeNull();
		run.PackageChanges.Should().BeEmpty();
	}

	[Theory]
	[InlineData("patch-apply", "pkg_|-patch-apply_|-patch-apply_|-uptodate", 3)]
	[InlineData("hold-salt-minion", "pkg_|-hold-salt-minion_|-salt-minion_|-held", 0)]
	[InlineData("patch-holds", null, 0)] // a test state, not a pkg state
	[InlineData("patch-needrestart-config", null, 0)] // a file state with changes, not a pkg state
	[InlineData("no-such-state", null, 0)]
	public void ParseStateRun_SelectsOnlyThePkgStateWithTheId(string packageStateId, string? expectedKey, int expectedChanges)
	{
		using var document = JsonDocument.Parse(Fixtures.Load("dryrun-job-result.json"));
		var minionReturn = SaltResponseParser.ParseJobResult(document.RootElement, DryRunJid).Returns["vm-01"];

		var run = SaltResponseParser.ParseStateRun(minionReturn, packageStateId);

		run.PackageState?.Key.Should().Be(expectedKey);
		(run.PackageState is null).Should().Be(expectedKey is null);
		run.PackageChanges.Should().HaveCount(expectedChanges);
		run.States.Should().HaveCount(5);
	}

	[Fact]
	public async Task Apply_RefusesWhileARunOfTheConfiguredStateHasNotReturned()
	{
		const string busyJid = "20261006145900000000";
		using var context = CreatePatchContext(
			o => o.PatchStateName = "site.patch",
			jobsList: RunningJobList(busyJid, "site.patch"));
		context.Server.On(r => r.Path == $"/jobs/{busyJid}" ? RunningJob(busyJid) : null);
		await DryRunAsync(context);

		var act = () => context.Client.PatchApplyAsync(ValidApply(), TestContext.Current.CancellationToken);

		await act.Should().ThrowAsync<SaltPatchGuardException>().WithMessage($"*site.patch*{busyJid}*apt lock*");
		context.Server.NonLoginRequests.Where(r => r.IsRun).Should().ContainSingle("only the dry run was submitted");
	}

	[Fact]
	public async Task Apply_IgnoresARunningJobOfAnotherState()
	{
		const string busyJid = "20261006145900000000";
		using var context = CreatePatchContext(
			o => o.PatchStateName = "site.patch",
			jobsList: RunningJobList(busyJid, "patch.apply"));
		context.Server.On(r => r.Path == $"/jobs/{busyJid}" ? RunningJob(busyJid) : null);
		await DryRunAsync(context);

		var result = await context.Client.PatchApplyAsync(ValidApply(), TestContext.Current.CancellationToken);

		result.AllSucceeded.Should().BeTrue();
		context.Server.NonLoginRequests.Should().NotContain(r => r.Path == $"/jobs/{busyJid}", "a job of another state is not inspected");
	}

	[Fact]
	public async Task PatchStatus_CallsTheConfiguredFunction_WhenNotReadOnly()
	{
		using var context = new SaltTestContext(o => o.PatchStatusFunction = "site_report.status");
		context.Server.OnRun("patchreport-one.json");

		var result = await context.Client.GetPatchStatusAsync(MinionTarget.List("vm-01"), TestContext.Current.CancellationToken);

		result["vm-01"].Succeeded.Should().BeTrue();
		result["vm-01"].Value!.LastUpgradeEpoch.Should().Be(1791267565);
		using var body = JsonDocument.Parse(context.Server.NonLoginRequests.Single().Body!);
		var lowstate = body.RootElement.EnumerateArray().Single();
		lowstate.GetProperty("client").GetString().Should().Be("local");
		lowstate.GetProperty("fun").GetString().Should().Be("site_report.status");
		lowstate.GetProperty("tgt").GetString().Should().Be("vm-01");
		lowstate.TryGetProperty("arg", out _).Should().BeFalse();
	}

	[Fact]
	public async Task PatchStatus_WithACustomFunction_IsRefusedInReadOnlyMode_BeforeAnyRequest()
	{
		using var context = new SaltTestContext(o =>
		{
			o.ReadOnly = true;
			o.PatchStatusFunction = "site_report.status";
		});
		context.Server.OnRun("patchreport-one.json");

		var act = () => context.Client.GetPatchStatusAsync(MinionTarget.List("vm-01"), TestContext.Current.CancellationToken);

		await act.Should().ThrowAsync<SaltReadOnlyViolationException>().WithMessage("*site_report.status*allow-list*");
		context.Server.Requests.Should().BeEmpty("nothing, not even the login, may be sent");
	}

	[Fact]
	public async Task PatchStatus_WithTheDefaultFunction_IsPermittedInReadOnlyMode()
	{
		using var context = new SaltTestContext(o => o.ReadOnly = true);
		context.Server.OnRun("patchreport-one.json");

		var result = await context.Client.GetPatchStatusAsync(MinionTarget.List("vm-01"), TestContext.Current.CancellationToken);

		result.AllSucceeded.Should().BeTrue();
		using var body = JsonDocument.Parse(context.Server.NonLoginRequests.Single().Body!);
		body.RootElement[0].GetProperty("fun").GetString().Should().Be("patchreport.status");
	}

	[Fact]
	public void TheAllowList_IsNotExtendedByTheConfiguredFunction()
	{
		using var context = new SaltTestContext(o => o.PatchStatusFunction = "site_report.status");

		ReadOnlyPolicy.LocalFunctions.Should().NotContain("site_report.status");
	}

	[Fact]
	public async Task TheContentNames_AreCopied_WhenTheClientIsBuilt()
	{
		using var context = CreatePatchContext();
		context.Options.PatchStateName = "site.patch";

		await DryRunAsync(context);

		using var body = JsonDocument.Parse(context.Server.NonLoginRequests.First(r => r.IsRun).Body!);
		body.RootElement[0].GetProperty("arg").EnumerateArray().Select(a => a.GetString()).Should().Equal("patch.apply");
	}

	private static PatchApplyRequest ValidApply()
		=> new() { MinionIds = ["vm-01"], ChangeReference = "CHG-1", ConfirmRealApply = true };

	private static async Task<PatchRunResult> DryRunAsync(SaltTestContext context)
		=> await context.Client.PatchDryRunAsync(new PatchDryRunRequest { Target = MinionTarget.List("vm-01") }, TestContext.Current.CancellationToken);

	/// <summary>The recorded dry run, with the package state's id (and so its key and name) renamed to <c>site-upgrade</c>.</summary>
	private static string RenamedPackageStateResult()
		=> Fixtures.Load("dryrun-job-result.json").Replace("patch-apply", "site-upgrade", StringComparison.Ordinal);

	private static string RunningJobList(string jid, string stateName)
		=> $$$$"""{"return":[{"{{{{jid}}}}":{"Function":"state.apply","Arguments":["{{{{stateName}}}}"],"Target":"vm-01","Target-type":"list","User":"someone","StartTime":"2026, Oct 06 14:59:00.000000"}}]}""";

	private static HttpResponseMessage RunningJob(string jid)
		=> FakeSaltServer.Json($$$$"""{"info":[{"jid":"{{{{jid}}}}","Function":"state.apply","Minions":["vm-01"],"Result":{}}]}""");

	private static SaltTestContext CreatePatchContext(
		Action<SaltClientOptions>? configure = null,
		string? dryRunResult = null,
		string? jobsList = null)
		=> PatchScenario.Create(configure, dryRunResult, jobsList);
}
