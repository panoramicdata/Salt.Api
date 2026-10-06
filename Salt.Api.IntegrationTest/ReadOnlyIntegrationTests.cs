namespace Salt.Api.IntegrationTest;

/// <summary>
/// Read-only calls against the TEST Salt API, through the read-only client. A handful of requests per run, far below
/// the login rate limit; the client logs in once.
/// </summary>
public class ReadOnlyIntegrationTests(SaltApiFixture fixture)
{
	private readonly SaltClient _client = fixture.Client;

	[Fact]
	public void TheClient_IsReadOnly()
		=> _client.IsReadOnly.Should().BeTrue();

	[Fact]
	public async Task Ping_AllMinions_Answer()
	{
		var result = await _client.PingAsync(MinionTarget.All, TestContext.Current.CancellationToken);

		result.Should().NotBeEmpty();
		result.Failures.Should().BeEmpty();
		result.AllSucceeded.Should().BeTrue();
		_client.LoginCount.Should().Be(1);
	}

	[Fact]
	public async Task MinionsUp_AreAllAccepted()
	{
		var up = await _client.GetMinionsUpAsync(TestContext.Current.CancellationToken);
		var keys = await _client.GetKeysAsync(TestContext.Current.CancellationToken);

		up.Should().NotBeEmpty();
		keys.Accepted.Should().Contain(up);
		keys.Local.Should().NotBeEmpty();
	}

	[Fact]
	public async Task MinionStatus_ListsUpAndDown()
	{
		var up = await _client.GetMinionsUpAsync(TestContext.Current.CancellationToken);
		var status = await _client.GetMinionStatusAsync(TestContext.Current.CancellationToken);

		status.Up.Should().BeEquivalentTo(up);
	}

	[Fact]
	public async Task PatchStatus_IsReadForEveryMinionThatIsUp()
	{
		var up = await _client.GetMinionsUpAsync(TestContext.Current.CancellationToken);

		var result = await _client.GetPatchStatusAsync(MinionTarget.List(up), TestContext.Current.CancellationToken);

		result.Keys.Should().BeEquivalentTo(up);
		result.Failures.Should().BeEmpty();
		foreach (var status in result.Values.Select(r => r.Value!))
		{
			status.PendingCount.Should().Be(status.PendingUpgrades.Count);
			status.KeptBack.Should().NotBeNull();
			status.Held.Should().NotBeNull();
		}
	}

	[Fact]
	public async Task Grain_IsReadForEveryMinion()
	{
		var result = await _client.GetGrainAsync(MinionTarget.All, "patch_class", TestContext.Current.CancellationToken);

		result.Should().NotBeEmpty();
		result.Values.Should().OnlyContain(r => r.Succeeded && r.Value.ValueKind == JsonValueKind.String);
	}

	[Fact]
	public async Task AnIdThatDoesNotExist_IsReportedAsNotReturned()
	{
		var result = await _client.PingAsync(MinionTarget.List("salt-api-integration-test-no-such-minion"), TestContext.Current.CancellationToken);

		result.NoMinionsMatched.Should().BeTrue();
		result.AllSucceeded.Should().BeFalse();
		result.Single().Value.FailureKind.Should().Be(MinionFailureKind.NotReturned);
	}

	[Fact]
	public async Task OneMinion_IsRead()
	{
		var up = await _client.GetMinionsUpAsync(TestContext.Current.CancellationToken);

		var minion = await _client.GetMinionAsync(up.Order(StringComparer.Ordinal).First(), TestContext.Current.CancellationToken);

		minion.Succeeded.Should().BeTrue();
		minion.Value.GetProperty("id").GetString().Should().Be(minion.MinionId);
	}

	[Fact]
	public async Task Jobs_AreListed()
	{
		var jobs = await _client.GetJobsAsync(TestContext.Current.CancellationToken);

		jobs.Should().NotBeEmpty("the calls above are recorded as jobs");
		jobs.Should().OnlyContain(j => j.Jid.Length > 0 && j.Function.Length > 0);
	}

	[Fact]
	public async Task AnAsyncPatchReport_CompletesThroughJobPolling()
	{
		var up = await _client.GetMinionsUpAsync(TestContext.Current.CancellationToken);
		var target = MinionTarget.List(up.Order(StringComparer.Ordinal).First());

		var job = await _client.SubmitAsync(Lowstate.PatchStatus(target), TestContext.Current.CancellationToken);
		var result = await _client.WaitForJobAsync(job, TimeSpan.FromMinutes(3), TestContext.Current.CancellationToken);

		result.IsComplete.Should().BeTrue();
		result.Returns.Keys.Should().BeEquivalentTo(target.MinionIds);
	}

	[Fact]
	public async Task AForbiddenCall_IsRefusedLocally()
	{
		var act = () => _client.PatchDryRunAsync(new PatchDryRunRequest { Target = MinionTarget.List("x") }, TestContext.Current.CancellationToken);

		await act.Should().ThrowAsync<SaltReadOnlyViolationException>();
	}
}
