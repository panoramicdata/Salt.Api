namespace Salt.Api.IntegrationTest;

/// <summary>
/// The patch dry run (<c>state.apply patch.apply test=True</c>) against ONE test minion. It changes no package, but it
/// refreshes apt and takes the apt lock on that minion for about half a minute.
/// </summary>
/// <remarks>
/// There is deliberately no integration test for a real apply: that is covered only by unit tests with a fake server.
/// </remarks>
public class DryRunIntegrationTests(SaltApiFixture fixture)
{
	[Fact]
	public async Task DryRun_OnOneMinion_ReportsWhatWouldChange()
	{
		var up = await fixture.Client.GetMinionsUpAsync(TestContext.Current.CancellationToken);
		var minionId = up.Order(StringComparer.Ordinal).First();

		var result = await fixture.DryRunClient.PatchDryRunAsync(
			new PatchDryRunRequest { Target = MinionTarget.List(minionId), Timeout = TimeSpan.FromMinutes(10) },
			TestContext.Current.CancellationToken);

		result.IsDryRun.Should().BeTrue();
		result.TimedOut.Should().BeFalse();
		result.Jid.Should().NotBeNullOrEmpty();
		var minion = result.Minions[minionId];
		minion.Succeeded.Should().BeTrue(minion.FailureText);
		minion.Value!.States.Should().NotBeEmpty();
		minion.Value.States.Should().Contain(s => s.Key == PatchStateRun.PackageStateKey);
		minion.Value.States.Where(s => s.Key == PatchStateRun.PackageStateKey)
			.Should().OnlyContain(s => s.Result == null || s.Result == true, "a dry run never reports a package state as done");
	}
}
