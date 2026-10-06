using System.Net;

namespace Salt.Api.Test;

/// <summary>
/// Back-off on 429, and the rule that a state-changing request is never retried after it may have reached Salt.
/// </summary>
public class RetryTests
{
	[Fact]
	public async Task A429OnLogin_BacksOff_ThenSucceeds()
	{
		using var context = new SaltTestContext();
		var refusals = 2;
		context.Server
			.On(r => r.IsLogin && refusals-- > 0 ? FakeSaltServer.Html(HttpStatusCode.TooManyRequests, "Too Many Requests") : null)
			.OnRun("ping-one.json");

		var result = await context.Client.PingAsync(MinionTarget.List("vm-01"), TestContext.Current.CancellationToken);

		result.AllSucceeded.Should().BeTrue();
		context.Server.LoginRequests.Should().HaveCount(3);
		context.Delays.Should().HaveCount(2);
		context.Delays.First().Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(5)).And.BeLessThanOrEqualTo(TimeSpan.FromSeconds(7.5));
		context.Delays.Last().Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(10));
	}

	[Fact]
	public async Task A429_ThatPersists_ThrowsAfterMaxAttempts()
	{
		using var context = new SaltTestContext(o => o.MaxAttemptCount = 3);
		context.Server.On(r => r.IsRun ? FakeSaltServer.Html(HttpStatusCode.TooManyRequests, "Too Many Requests") : null);

		var act = () => context.Client.PingAsync(MinionTarget.List("vm-01"), TestContext.Current.CancellationToken);

		var thrown = await act.Should().ThrowAsync<SaltApiException>();
		thrown.Which.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
		context.Server.NonLoginRequests.Should().HaveCount(3);
		context.Delays.Should().HaveCount(2).And.OnlyContain(d => d <= TimeSpan.FromSeconds(30));
	}

	[Theory]
	[InlineData(0.0)]
	[InlineData(0.5)]
	[InlineData(0.999999)]
	public void TheBackOff_GrowsAndIsCapped(double jitter)
	{
		for (var attempt = 1; attempt <= 10; attempt++)
		{
			var delay = SaltAuthenticatingHandler.CalculateBackOffDelay(attempt, 5, 2.0, 30, jitter);
			var floor = Math.Min(5 * Math.Pow(2, attempt - 1), 30);
			delay.TotalSeconds.Should().BeGreaterThanOrEqualTo(floor).And.BeLessThanOrEqualTo(Math.Min(floor * 1.5, 30));
		}
	}

	[Theory]
	[InlineData(401, false, true, 1, SaltAuthenticatingHandler.RetryAction.Relogin)]
	[InlineData(401, true, true, 1, SaltAuthenticatingHandler.RetryAction.Fail)]
	[InlineData(429, false, false, 1, SaltAuthenticatingHandler.RetryAction.BackOff)]
	[InlineData(429, false, false, 5, SaltAuthenticatingHandler.RetryAction.Fail)]
	[InlineData(503, false, true, 1, SaltAuthenticatingHandler.RetryAction.BackOff)]
	[InlineData(503, false, false, 1, SaltAuthenticatingHandler.RetryAction.Fail)]
	[InlineData(504, false, true, 5, SaltAuthenticatingHandler.RetryAction.Fail)]
	[InlineData(500, false, true, 1, SaltAuthenticatingHandler.RetryAction.Fail)]
	internal void DecideRetry_RetriesOnlyWhatIsSafe(int status, bool reloggedIn, bool isReadOnlySafe, int attempt, SaltAuthenticatingHandler.RetryAction expected)
	{
		var options = new SaltClientOptions { BaseUrl = "https://salt.example.test", Username = "u", Password = "p", MaxAttemptCount = 5 };
		using var handler = new SaltAuthenticatingHandler(
			options,
			Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
			new FakeSaltServer(),
			TimeProvider.System,
			(_, _) => Task.CompletedTask,
			"test");

		handler.DecideRetry(status, reloggedIn, isReadOnlySafe, attempt).Should().Be(expected);
	}

	[Fact]
	public async Task A503_OnAReadOnlyCall_IsRetried()
	{
		using var context = new SaltTestContext();
		var failures = 1;
		context.Server
			.On(r => r.IsRun && failures-- > 0 ? FakeSaltServer.Html(HttpStatusCode.ServiceUnavailable, "maintenance") : null)
			.OnRun("ping-one.json");

		var result = await context.Client.PingAsync(MinionTarget.List("vm-01"), TestContext.Current.CancellationToken);

		result.AllSucceeded.Should().BeTrue();
		context.Server.NonLoginRequests.Should().HaveCount(2);
	}

	[Fact]
	public async Task A504_OnAStateChangingCall_IsNeverRetried()
	{
		using var context = new SaltTestContext(o => o.AllowRawLowstate = true);
		context.Server.On(r => r.IsRun ? FakeSaltServer.Html(HttpStatusCode.GatewayTimeout, "Gateway Timeout") : null);

		var act = () => context.Client.ExecuteAsync<JsonElement>(
			Lowstate.Raw("local", "pkg.install", MinionTarget.List("vm-01"), ["curl"]),
			TestContext.Current.CancellationToken);

		var thrown = await act.Should().ThrowAsync<SaltApiException>();
		thrown.Which.StatusCode.Should().Be(HttpStatusCode.GatewayTimeout);
		context.Server.NonLoginRequests.Should().ContainSingle("the install may already be running");
	}

	[Fact]
	public async Task AnErrorBody_IsHtml_AndIsReportedByStatus()
	{
		using var context = new SaltTestContext();
		context.Server.On(r => r.IsRun ? FakeSaltServer.Html(HttpStatusCode.InternalServerError, "An unexpected error occurred") : null);

		var act = () => context.Client.PingAsync(MinionTarget.List("vm-01"), TestContext.Current.CancellationToken);

		var thrown = await act.Should().ThrowAsync<SaltApiException>();
		thrown.Which.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
		thrown.Which.ResponseText.Should().Contain("An unexpected error occurred");
	}
}
