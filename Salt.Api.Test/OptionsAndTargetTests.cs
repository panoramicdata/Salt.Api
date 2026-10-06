namespace Salt.Api.Test;

/// <summary>
/// Options validation and minion targets.
/// </summary>
public class OptionsAndTargetTests
{
	private static SaltClientOptions Valid() => new() { BaseUrl = "https://salt.example.test", Username = "u", Password = "p" };

	[Fact]
	public void TheDefaults_AreAsDocumented()
	{
		var options = new SaltClientOptions();

		options.ReadOnly.Should().BeFalse();
		options.AllowRawLowstate.Should().BeFalse();
		options.Eauth.Should().Be("file");
		options.HttpClientTimeoutSeconds.Should().Be(150);
		options.DefaultMinionTimeoutSeconds.Should().Be(120);
		options.BaseUrl.Should().BeEmpty("there is no default estate");
	}

	public static TheoryData<string, Action<SaltClientOptions>> InvalidOptions => new()
	{
		{ "*BaseUrl*", o => o.BaseUrl = "" },
		{ "*https*", o => o.BaseUrl = "http://salt.example.test" },
		{ "*https*", o => o.BaseUrl = "salt.example.test" },
		{ "*query*", o => o.BaseUrl = "https://salt.example.test/?x=1" },
		{ "*query*", o => o.BaseUrl = "https://user:pass@salt.example.test" },
		{ "*Username*", o => o.Username = " " },
		{ "*Password*", o => o.Password = "" },
		{ "*Eauth*", o => o.Eauth = "" },
		{ "*HttpClientTimeoutSeconds*", o => o.HttpClientTimeoutSeconds = 120 },
		{ "*DefaultMinionTimeoutSeconds*", o => o.DefaultMinionTimeoutSeconds = 0 },
		{ "*MaxAttemptCount*", o => o.MaxAttemptCount = 0 },
		{ "*JobPollIntervalSeconds*", o => o.JobPollIntervalSeconds = 0 },
		{ "*factor*", o => o.BackOffDelayFactor = 0.5 },
		{ "*DryRunValidityMinutes*", o => o.DryRunValidityMinutes = 0 },
	};

	[Theory]
	[MemberData(nameof(InvalidOptions))]
	public void Validate_RejectsInvalidOptions(string expectedMessage, Action<SaltClientOptions> change)
	{
		var options = Valid();
		change(options);

		var act = () => new SaltClient(options);

		act.Should().Throw<SaltConfigurationException>().WithMessage(expectedMessage);
	}

	[Fact]
	public void Validate_AcceptsAPasswordProviderInsteadOfAPassword()
	{
		var options = Valid();
		options.Password = string.Empty;
		options.PasswordProvider = _ => ValueTask.FromResult("p");

		options.Invoking(o => o.Validate()).Should().NotThrow();
	}

	[Fact]
	public void ToString_DoesNotContainThePassword()
		=> new SaltClientOptions { BaseUrl = "https://x.test", Username = "u", Password = "secret-value" }.ToString().Should().NotContain("secret-value");

	[Fact]
	public async Task ABaseUrlWithAPath_IsHonoured()
	{
		using var context = new SaltTestContext(o =>
		{
			o.BaseUrl = "https://salt.example.test/salt-api/";
			o.ReadOnly = true;
		});
		context.Server.OnRun("ping-one.json", r => r.Path == "/salt-api/").OnGet("/salt-api/keys", "keys.json");
		context.Server.On(r => r.Path == "/salt-api/login" ? FakeSaltServer.Json(Fixtures.Load("login-ok.json").Replace("<token>", "path-token", StringComparison.Ordinal)) : null);

		await context.Client.GetKeysAsync(TestContext.Current.CancellationToken);

		context.Server.Requests.Select(r => r.Path).Should().Equal("/salt-api/login", "/salt-api/keys");
	}

	[Theory]
	[InlineData("vm-01")]
	[InlineData("node.example.com")]
	[InlineData("a_b-c.d")]
	public void List_AcceptsExactIds(string id)
		=> MinionTarget.List(id).MinionIds.Should().Equal(id);

	[Theory]
	[InlineData("*")]
	[InlineData("vm-*")]
	[InlineData("vm-0?")]
	[InlineData("vm-0[12]")]
	[InlineData("vm-01,vm-02")]
	[InlineData("vm 01")]
	[InlineData("")]
	[InlineData("-vm")]
	[InlineData("G@os:Ubuntu")]
	[InlineData("E@.*")]
	public void List_RejectsAnythingButAnExactId(string id)
	{
		var act = () => MinionTarget.List(id);

		act.Should().Throw<ArgumentException>();
	}

	[Fact]
	public void List_RemovesDuplicates_AndJoinsWithCommas()
	{
		var target = MinionTarget.List("vm-01", "vm-02", "vm-01");

		target.Expression.Should().Be("vm-01,vm-02");
		target.TargetType.Should().Be("list");
		target.IsExactList.Should().BeTrue();
	}

	[Fact]
	public void List_RejectsAnEmptyList()
	{
		var act = () => MinionTarget.List();

		act.Should().Throw<ArgumentException>();
	}

	[Fact]
	public void Glob_IsNotAnExactList()
	{
		var target = MinionTarget.Glob("vm-*");

		target.TargetType.Should().Be("glob");
		target.IsExactList.Should().BeFalse();
		target.MinionIds.Should().BeNull();
		MinionTarget.All.Expression.Should().Be("*");
	}

	[Theory]
	[InlineData("")]
	[InlineData("a b")]
	[InlineData("a,b")]
	public void Glob_RejectsWhitespaceAndCommas(string pattern)
	{
		var act = () => MinionTarget.Glob(pattern);

		act.Should().Throw<ArgumentException>();
	}

	[Fact]
	public void GrainsGet_RejectsAKeywordArgumentAsTheGrainName()
	{
		var act = () => Lowstate.GrainsGet(MinionTarget.All, "refresh=True");

		act.Should().Throw<ArgumentException>();
	}

	[Fact]
	public void AsAsync_IsOnlyForLocalCalls()
	{
		var act = () => Lowstate.ManageUp().AsAsync();

		act.Should().Throw<InvalidOperationException>();
	}
}
