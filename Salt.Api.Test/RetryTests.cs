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
		using var context = FailThenAnswerPing(r => r.IsLogin, HttpStatusCode.TooManyRequests, failures: 2);

		var result = await PingAsync(context);

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

		await AssertEveryRunFailsWithAsync(context, HttpStatusCode.TooManyRequests);

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
		using var handler = TestHandlers.Create(new FakeSaltServer(), readOnly: false);

		handler.DecideRetry(status, reloggedIn, isReadOnlySafe, attempt).Should().Be(expected);
	}

	[Fact]
	public async Task A503_OnAReadOnlyCall_IsRetried()
	{
		using var context = FailThenAnswerPing(r => r.IsRun, HttpStatusCode.ServiceUnavailable, failures: 1);

		var result = await PingAsync(context);

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

		var thrown = await AssertEveryRunFailsWithAsync(context, HttpStatusCode.InternalServerError);

		thrown.ResponseText.Should().Contain(StatusText(HttpStatusCode.InternalServerError));
	}

	/// <summary>A context whose matching requests fail <paramref name="failures"/> times, then a ping succeeds.</summary>
	private static SaltTestContext FailThenAnswerPing(Func<RecordedRequest, bool> when, HttpStatusCode status, int failures)
	{
		var context = new SaltTestContext();
		var remaining = failures;
		context.Server
			.On(r => when(r) && remaining-- > 0 ? FakeSaltServer.Html(status, StatusText(status)) : null)
			.OnRun("ping-one.json");
		return context;
	}

	/// <summary>Makes every run fail with <paramref name="status"/>, pings, and asserts the typed exception.</summary>
	private static async Task<SaltApiException> AssertEveryRunFailsWithAsync(SaltTestContext context, HttpStatusCode status)
	{
		context.Server.On(r => r.IsRun ? FakeSaltServer.Html(status, StatusText(status)) : null);

		var act = () => PingAsync(context);

		var thrown = await act.Should().ThrowAsync<SaltApiException>();
		thrown.Which.StatusCode.Should().Be(status);
		return thrown.Which;
	}

	private static Task<MinionResultDictionary<bool>> PingAsync(SaltTestContext context)
		=> context.Client.PingAsync(MinionTarget.List("vm-01"), TestContext.Current.CancellationToken);

	private static string StatusText(HttpStatusCode status) => $"Salt returned {(int)status}";
}
