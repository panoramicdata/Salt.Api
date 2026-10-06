using System.Text;
using Microsoft.Extensions.Logging.Abstractions;

namespace Salt.Api.Test;

/// <summary>
/// Read-only mode on the client: a refused call must throw before any request, including the login, is made.
/// </summary>
public class ReadOnlyClientTests
{
	public static TheoryData<string> ForbiddenRawCalls =>
	[
		"cmd.run",
		"state.apply",
		"pkg.install",
		"pkg.upgrade",
		"system.reboot",
		"no.such.function",
	];

	[Theory]
	[MemberData(nameof(ForbiddenRawCalls))]
	public async Task RawForbiddenCall_IsRefusedBeforeAnyRequest(string function)
	{
		using var context = new SaltTestContext(o =>
		{
			o.ReadOnly = true;
			o.AllowRawLowstate = true;
		});

		var act = () => context.Client.ExecuteAsync<JsonElement>(
			Lowstate.Raw("local", function, MinionTarget.All, ["arg"]),
			TestContext.Current.CancellationToken);

		await act.Should().ThrowAsync<SaltReadOnlyViolationException>();
		context.Server.Requests.Should().BeEmpty("nothing, not even the login, may be sent");
	}

	[Theory]
	[InlineData("key.accept")]
	[InlineData("key.delete")]
	public async Task RawWheelKeyChange_IsRefusedBeforeAnyRequest(string function)
	{
		using var context = new SaltTestContext(o =>
		{
			o.ReadOnly = true;
			o.AllowRawLowstate = true;
		});

		var act = () => context.Client.ExecuteAsync<JsonElement>(Lowstate.Raw("wheel", function), TestContext.Current.CancellationToken);

		await act.Should().ThrowAsync<SaltReadOnlyViolationException>();
		context.Server.Requests.Should().BeEmpty();
	}

	[Fact]
	public async Task MultiCommandRequest_WithOneForbiddenElement_IsRefusedWhole()
	{
		using var context = new SaltTestContext(o =>
		{
			o.ReadOnly = true;
			o.AllowRawLowstate = true;
		});

		var act = () => context.Client.ExecuteAsync(
			[Lowstate.Ping(MinionTarget.All), Lowstate.Raw("local", "cmd.run", MinionTarget.All, ["id"])],
			TestContext.Current.CancellationToken);

		await act.Should().ThrowAsync<SaltReadOnlyViolationException>().WithMessage("*lowstate 2 of 2*");
		context.Server.Requests.Should().BeEmpty();
	}

	[Fact]
	public async Task PatchDryRun_IsRefusedInReadOnlyMode_BeforeAnyRequest()
	{
		using var context = new SaltTestContext(o => o.ReadOnly = true);

		var act = () => context.Client.PatchDryRunAsync(new PatchDryRunRequest { Target = MinionTarget.List("vm-01") }, TestContext.Current.CancellationToken);

		await act.Should().ThrowAsync<SaltReadOnlyViolationException>().WithMessage("*even with test=True*");
		context.Server.Requests.Should().BeEmpty();
	}

	[Fact]
	public async Task PatchApply_IsRefusedInReadOnlyMode_BeforeAnyRequest()
	{
		using var context = new SaltTestContext(o => o.ReadOnly = true);

		var act = () => context.Client.PatchApplyAsync(
			new PatchApplyRequest { MinionIds = ["vm-01"], ChangeReference = "CHG-1", ConfirmRealApply = true },
			TestContext.Current.CancellationToken);

		await act.Should().ThrowAsync<SaltReadOnlyViolationException>();
		context.Server.Requests.Should().BeEmpty();
	}

	[Fact]
	public async Task RawLowstate_NeedsAllowRawLowstate_EvenWhenNotReadOnly()
	{
		using var context = new SaltTestContext();

		var act = () => context.Client.ExecuteAsync<JsonElement>(Lowstate.Raw("local", "test.ping", MinionTarget.All), TestContext.Current.CancellationToken);

		await act.Should().ThrowAsync<SaltConfigurationException>().WithMessage("*AllowRawLowstate*");
		context.Server.Requests.Should().BeEmpty();
	}

	[Fact]
	public async Task RawPermittedCall_IsAllowedInReadOnlyMode()
	{
		using var context = new SaltTestContext(o =>
		{
			o.ReadOnly = true;
			o.AllowRawLowstate = true;
		});
		context.Server.OnRun("ping-one.json");

		var result = await context.Client.ExecuteOnMinionsAsync<bool>(
			Lowstate.Raw("local", "test.ping", MinionTarget.List("vm-01")),
			TestContext.Current.CancellationToken);

		result["vm-01"].Value.Should().BeTrue();
	}

	[Fact]
	public async Task ChangingTheOptionsAfterConstruction_DoesNotTurnOffReadOnly()
	{
		using var context = new SaltTestContext(o =>
		{
			o.ReadOnly = true;
			o.AllowRawLowstate = true;
		});
		context.Options.ReadOnly = false;

		var act = () => context.Client.ExecuteAsync<JsonElement>(Lowstate.Raw("local", "cmd.run", MinionTarget.All, ["id"]), TestContext.Current.CancellationToken);

		await act.Should().ThrowAsync<SaltReadOnlyViolationException>();
		context.Client.IsReadOnly.Should().BeTrue();
		context.Server.Requests.Should().BeEmpty();
	}

	[Fact]
	public async Task EveryConvenienceRead_IsPermittedInReadOnlyMode()
	{
		using var context = new SaltTestContext(o => o.ReadOnly = true);
		context.Server
			.OnRun("ping-all.json", r => r.Body!.Contains("test.ping", StringComparison.Ordinal))
			.OnRun("patchreport-all.json", r => r.Body!.Contains("patchreport.status", StringComparison.Ordinal))
			.OnRun("grains-patch-class.json", r => r.Body!.Contains("grains.get", StringComparison.Ordinal))
			.OnRun("runner-manage-up.json", r => r.Body!.Contains("manage.up", StringComparison.Ordinal))
			.OnGet("/keys", "keys.json")
			.OnGet("/jobs", "jobs-list.json")
			.OnGet("/minions/vm-01", "minion-one.json")
			.OnGet("/jobs/20261006143338086181", "dryrun-job-result.json");
		var token = TestContext.Current.CancellationToken;

		await context.Client.PingAsync(MinionTarget.All, token);
		await context.Client.GetPatchStatusAsync(MinionTarget.All, token);
		await context.Client.GetGrainAsync(MinionTarget.All, "patch_class", token);
		await context.Client.GetMinionsUpAsync(token);
		await context.Client.GetKeysAsync(token);
		await context.Client.GetJobsAsync(token);
		await context.Client.GetMinionAsync("vm-01", token);
		await context.Client.GetJobAsync("20261006143338086181", token);

		context.Server.NonLoginRequests.Should().HaveCount(8);
	}

	/// <summary>
	/// The handler checks the bytes about to be sent, so a request built without the typed client is refused too.
	/// </summary>
	[Fact]
	public async Task Handler_RefusesAForbiddenBody_ThatBypassedTheClient()
	{
		var server = new FakeSaltServer();
		var options = new SaltClientOptions { BaseUrl = "https://salt.example.test", Username = "u", Password = "p", ReadOnly = true };
		using var handler = new SaltAuthenticatingHandler(options, NullLogger.Instance, server, TimeProvider.System, (_, _) => Task.CompletedTask, "test");
		using var invoker = new HttpMessageInvoker(handler);
		using var request = new HttpRequestMessage(HttpMethod.Post, "https://salt.example.test/")
		{
			Content = new StringContent("""[{"client":"local","tgt":"*","fun":"cmd.run","arg":["id"]}]""", Encoding.UTF8, "application/json"),
		};

		var act = () => invoker.SendAsync(request, TestContext.Current.CancellationToken);

		await act.Should().ThrowAsync<SaltReadOnlyViolationException>();
		server.Requests.Should().BeEmpty();
	}

	[Theory]
	[InlineData("POST", "https://salt.example.test/run")]
	[InlineData("POST", "https://salt.example.test/hook")]
	[InlineData("GET", "https://salt.example.test/stats")]
	[InlineData("DELETE", "https://salt.example.test/keys/vm-01")]
	public async Task Handler_RefusesOtherEndpoints(string method, string url)
	{
		var server = new FakeSaltServer();
		var options = new SaltClientOptions { BaseUrl = "https://salt.example.test", Username = "u", Password = "p", ReadOnly = true };
		using var handler = new SaltAuthenticatingHandler(options, NullLogger.Instance, server, TimeProvider.System, (_, _) => Task.CompletedTask, "test");
		using var invoker = new HttpMessageInvoker(handler);
		using var request = new HttpRequestMessage(new HttpMethod(method), url);

		var act = () => invoker.SendAsync(request, TestContext.Current.CancellationToken);

		await act.Should().ThrowAsync<SaltReadOnlyViolationException>();
		server.Requests.Should().BeEmpty();
	}
}
