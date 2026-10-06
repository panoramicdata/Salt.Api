using System.Reflection;

namespace Salt.Api.Test;

/// <summary>
/// The read-only allow-list, tested on the JSON that would be sent.
/// </summary>
public class ReadOnlyPolicyTests
{
	public static TheoryData<string> PermittedBodies =>
	[
		"""[{"client":"local","tgt":"vm-01","tgt_type":"list","fun":"test.ping","timeout":120}]""",
		"""[{"client":"local","tgt":"*","fun":"patchreport.status"}]""",
		"""[{"client":"local_async","tgt":"vm-01","fun":"patchreport.status"}]""",
		"""[{"client":"local","tgt":"*","fun":"pkg.list_upgrades"}]""",
		"""[{"client":"local","tgt":"*","fun":"pkg.list_upgrades","kwarg":{"refresh":false}}]""",
		"""[{"client":"local","tgt":"*","fun":"grains.get","arg":["patch_class"]}]""",
		"""[{"client":"local","tgt":"vm-01","fun":"grains.items"}]""",
		"""[{"client":"runner","fun":"manage.up"}]""",
		"""[{"client":"runner","fun":"manage.status"}]""",
		"""[{"client":"wheel","fun":"key.list_all"}]""",
		"""[{"client":"local","tgt":"*","fun":"test.ping"},{"client":"runner","fun":"manage.up"}]""",
	];

	public static TheoryData<string> RefusedBodies =>
	[
		// The patch state, even as a dry run.
		"""[{"client":"local","tgt":"vm-01","fun":"state.apply","arg":["patch.apply"],"kwarg":{"test":true}}]""",
		"""[{"client":"local_async","tgt":"vm-01","fun":"state.apply","arg":["patch.apply"],"kwarg":{"test":true}}]""",
		"""[{"client":"local","tgt":"vm-01","fun":"state.apply","arg":["patch.apply"]}]""",
		"""[{"client":"local","tgt":"vm-01","fun":"state.highstate"}]""",
		// Root on every host.
		"""[{"client":"local","tgt":"*","fun":"cmd.run","arg":["id"]}]""",
		"""[{"client":"local","tgt":"*","fun":"cmd.script","arg":["salt://x.sh"]}]""",
		"""[{"client":"local","tgt":"*","fun":"cmd.run_all","arg":["id"]}]""",
		// Package and service changes.
		"""[{"client":"local","tgt":"*","fun":"pkg.install","arg":["curl"]}]""",
		"""[{"client":"local","tgt":"*","fun":"pkg.upgrade"}]""",
		"""[{"client":"local","tgt":"*","fun":"pkg.refresh_db"}]""",
		"""[{"client":"local","tgt":"*","fun":"pkg.remove","arg":["curl"]}]""",
		"""[{"client":"local","tgt":"*","fun":"service.restart","arg":["ssh"]}]""",
		"""[{"client":"local","tgt":"*","fun":"system.reboot"}]""",
		// pkg.list_upgrades that would run apt-get update.
		"""[{"client":"local","tgt":"*","fun":"pkg.list_upgrades","kwarg":{"refresh":true}}]""",
		"""[{"client":"local","tgt":"*","fun":"pkg.list_upgrades","kwarg":{"refresh":"True"}}]""",
		"""[{"client":"local","tgt":"*","fun":"pkg.list_upgrades","arg":["refresh=True"]}]""",
		"""[{"client":"local","tgt":"*","fun":"pkg.list_upgrades","arg":["True"]}]""",
		// A keyword argument smuggled in as a positional argument.
		"""[{"client":"local","tgt":"*","fun":"grains.get","arg":["x=y"]}]""",
		"""[{"client":"local","tgt":"*","fun":"grains.get","arg":[{"a":1}]}]""",
		// Keys and wheel.
		"""[{"client":"wheel","fun":"key.accept","match":"vm-01"}]""",
		"""[{"client":"wheel","fun":"key.accept"}]""",
		"""[{"client":"wheel","fun":"key.delete"}]""",
		"""[{"client":"wheel","fun":"key.reject"}]""",
		// Other runners.
		"""[{"client":"runner","fun":"manage.down","kwarg":{"removekeys":true}}]""",
		"""[{"client":"runner","fun":"state.orchestrate","arg":["x"]}]""",
		// Exact, case-sensitive matching only.
		"""[{"client":"local","tgt":"*","fun":"Test.Ping"}]""",
		"""[{"client":"local","tgt":"*","fun":"test.ping "}]""",
		"""[{"client":"local","tgt":"*","fun":"test.pin"}]""",
		"""[{"client":"local","tgt":"*","fun":"test.ping.x"}]""",
		"""[{"client":"local","tgt":"*","fun":"test.*"}]""",
		"""[{"client":"LOCAL","tgt":"*","fun":"test.ping"}]""",
		"""[{"client":"runner","fun":"manage.UP"}]""",
		// Unknown or misplaced function or client.
		"""[{"client":"local","tgt":"*","fun":"no.such.function"}]""",
		"""[{"client":"local","tgt":"*","fun":"manage.up"}]""",
		"""[{"client":"runner","fun":"test.ping"}]""",
		"""[{"client":"wheel","fun":"manage.up"}]""",
		"""[{"client":"ssh","tgt":"*","fun":"test.ping"}]""",
		"""[{"client":"local_batch","tgt":"*","fun":"test.ping"}]""",
		// Missing or malformed fields.
		"""[{"tgt":"*","fun":"test.ping"}]""",
		"""[{"client":"local","tgt":"*"}]""",
		"""[{"client":"local","tgt":"*","fun":""}]""",
		"""[{"client":"local","tgt":"*","fun":["test.ping"]}]""",
		"""[{"client":["local"],"tgt":"*","fun":"test.ping"}]""",
		"""[{"client":"local","fun":"test.ping"}]""",
		"""[{"client":"local","tgt":"  ","fun":"test.ping"}]""",
		"""[{"client":"local","tgt":"*","tgt_type":"compound","fun":"test.ping"}]""",
		"""[{"client":"runner","tgt":"*","fun":"manage.up"}]""",
		"""[{"client":"local","tgt":"*","fun":"test.ping","arg":"x"}]""",
		"""[{"client":"local","tgt":"*","fun":"test.ping","kwarg":[]}]""",
		// Keys that are not part of a read-only lowstate.
		"""[{"client":"local","tgt":"*","fun":"test.ping","ret":"smtp"}]""",
		"""[{"client":"local","tgt":"*","fun":"test.ping","username":"x","password":"y","eauth":"file"}]""",
		"""[{"client":"local","tgt":"*","fun":"test.ping","token":"x"}]""",
		// One bad element refuses the whole request.
		"""[{"client":"local","tgt":"*","fun":"test.ping"},{"client":"local","tgt":"*","fun":"cmd.run","arg":["id"]}]""",
		"""[{"client":"runner","fun":"manage.up"},{"client":"wheel","fun":"key.delete"}]""",
		// Not a JSON array of objects.
		"""{"client":"local","tgt":"*","fun":"test.ping"}""",
		"""[]""",
		"""["test.ping"]""",
		"""client=local&tgt=*&fun=cmd.run&arg=id""",
		"",
	];

	[Theory]
	[MemberData(nameof(PermittedBodies))]
	public void CheckBody_PermitsTheAllowList(string body)
		=> ReadOnlyPolicy.CheckBody(body).Should().BeNull();

	[Theory]
	[MemberData(nameof(RefusedBodies))]
	public void CheckBody_RefusesEverythingElse(string body)
		=> ReadOnlyPolicy.CheckBody(body).Should().NotBeNull().And.StartWith("Read-only mode refused");

	[Theory]
	[InlineData("GET", "/jobs")]
	[InlineData("GET", "/jobs/20261006143338086181")]
	[InlineData("GET", "/keys")]
	[InlineData("GET", "/keys/vm-01")]
	[InlineData("GET", "/minions")]
	[InlineData("GET", "/minions/vm-01")]
	[InlineData("GET", "/events")]
	[InlineData("POST", "/login")]
	[InlineData("POST", "/logout")]
	public void CheckRequest_PermitsReadOnlyEndpoints(string method, string path)
		=> ReadOnlyPolicy.CheckRequest(new HttpMethod(method), path, null).Should().BeNull();

	[Theory]
	[InlineData("POST", "/run")]
	[InlineData("POST", "/hook")]
	[InlineData("POST", "/hook/anything")]
	[InlineData("POST", "/minions")]
	[InlineData("POST", "/keys")]
	[InlineData("POST", "/jobs")]
	[InlineData("GET", "/stats")]
	[InlineData("GET", "/ws")]
	[InlineData("GET", "/run")]
	[InlineData("GET", "/jobs/1/2")]
	[InlineData("PUT", "/")]
	[InlineData("DELETE", "/keys/vm-01")]
	[InlineData("PATCH", "/")]
	public void CheckRequest_RefusesOtherEndpoints(string method, string path)
		=> ReadOnlyPolicy.CheckRequest(new HttpMethod(method), path, """[{"client":"local","tgt":"*","fun":"test.ping"}]""").Should().NotBeNull();

	[Fact]
	public void CheckRequest_ChecksTheBodyOfPostRoot()
	{
		ReadOnlyPolicy.CheckRequest(HttpMethod.Post, "/", """[{"client":"local","tgt":"*","fun":"test.ping"}]""").Should().BeNull();
		ReadOnlyPolicy.CheckRequest(HttpMethod.Post, "/", """[{"client":"local","tgt":"*","fun":"cmd.run"}]""").Should().NotBeNull();
	}

	/// <summary>
	/// Every public factory except Raw must produce a call on the allow-list, so that code using only the factories
	/// cannot build a forbidden call. This test finds the factories by reflection, so a new one is covered automatically.
	/// </summary>
	[Fact]
	public void EveryPublicFactoryExceptRaw_BuildsAPermittedCall()
	{
		var factories = typeof(Lowstate)
			.GetMethods(BindingFlags.Public | BindingFlags.Static)
			.Where(m => m.ReturnType == typeof(Lowstate) && m.Name != nameof(Lowstate.Raw))
			.ToList();

		factories.Should().NotBeEmpty();
		foreach (var factory in factories)
		{
			var arguments = factory.GetParameters().Select(p => p.ParameterType switch
			{
				var t when t == typeof(MinionTarget) => (object?)MinionTarget.List("vm-01"),
				var t when t == typeof(string) => "patch_class",
				_ => null,
			}).ToArray();

			var lowstate = (Lowstate)factory.Invoke(null, arguments)!;
			var asJob = lowstate.Client == "local" ? lowstate.AsAsync() : lowstate;

			ReadOnlyPolicy.CheckBody(JsonSerializer.Serialize(new[] { lowstate }, SaltJson.Options))
				.Should().BeNull($"{factory.Name} must build a read-only call");
			ReadOnlyPolicy.CheckBody(JsonSerializer.Serialize(new[] { asJob }, SaltJson.Options))
				.Should().BeNull($"{factory.Name} submitted as a job must stay read-only");
		}
	}

	[Fact]
	public void ThePatchState_IsNeverPermitted()
	{
		foreach (var test in new[] { true, false })
		{
			var lowstate = Lowstate.PatchStateApply(MinionTarget.List("vm-01"), test);
			ReadOnlyPolicy.CheckBody(JsonSerializer.Serialize(new[] { lowstate }, SaltJson.Options)).Should().NotBeNull();
		}
	}

	[Fact]
	public void TheAllowList_IsExactlyTheDocumentedSet()
	{
		ReadOnlyPolicy.LocalFunctions.Should().BeEquivalentTo("test.ping", "patchreport.status", "pkg.list_upgrades", "grains.get", "grains.items");
		ReadOnlyPolicy.RunnerFunctions.Should().BeEquivalentTo("manage.up", "manage.status");
		ReadOnlyPolicy.WheelFunctions.Should().BeEquivalentTo("key.list_all");
	}
}
