using System.Net;

namespace Salt.Api.Test;

/// <summary>
/// Login, token reuse, the 401 re-login rule, and the measured login traps.
/// </summary>
public class AuthenticationTests
{
	[Fact]
	public async Task EveryRequest_SendsAcceptApplicationJson_AndAUserAgent()
	{
		using var context = new SaltTestContext();
		context.Server.OnRun("ping-one.json").OnGet("/keys", "keys.json");

		await context.Client.PingAsync(MinionTarget.List("vm-01"), TestContext.Current.CancellationToken);
		await context.Client.GetKeysAsync(TestContext.Current.CancellationToken);

		context.Server.Requests.Should().HaveCount(3);
		context.Server.Requests.Should().OnlyContain(r => r.Accept == "application/json");
		context.Server.Requests.Should().OnlyContain(r => r.UserAgent != null && r.UserAgent.StartsWith("Salt.Api/", StringComparison.Ordinal));
	}

	/// <summary>
	/// Measured live: rest_cherrypy answers a chunked JSON body (no Content-Length) with HTTP 500.
	/// </summary>
	[Fact]
	public async Task EveryPost_HasAContentLength_MatchingItsBody()
	{
		using var context = new SaltTestContext();
		context.Server.OnRun("ping-one.json");

		await context.Client.PingAsync(MinionTarget.List("vm-01"), TestContext.Current.CancellationToken);

		var posts = context.Server.Requests.Where(r => r.Method == HttpMethod.Post).ToList();
		posts.Should().HaveCount(2);
		posts.Should().OnlyContain(r => r.ContentLength == System.Text.Encoding.UTF8.GetByteCount(r.Body!));
	}

	[Fact]
	public async Task Login_SendsTheCredentialsAndEauth_AsJson()
	{
		using var context = new SaltTestContext();
		context.Server.OnRun("ping-one.json");

		await context.Client.PingAsync(MinionTarget.List("vm-01"), TestContext.Current.CancellationToken);

		var login = context.Server.LoginRequests.Should().ContainSingle().Subject;
		using var body = JsonDocument.Parse(login.Body!);
		body.RootElement.GetProperty("username").GetString().Should().Be("api-user");
		body.RootElement.GetProperty("password").GetString().Should().Be(SaltTestContext.Password);
		body.RootElement.GetProperty("eauth").GetString().Should().Be("file");
		login.Token.Should().BeNull();
	}

	[Fact]
	public async Task TheToken_IsReused_AcrossCalls()
	{
		using var context = new SaltTestContext();
		context.Server.OnRun("ping-one.json");

		for (var i = 0; i < 5; i++)
		{
			await context.Client.PingAsync(MinionTarget.List("vm-01"), TestContext.Current.CancellationToken);
		}

		context.Client.LoginCount.Should().Be(1);
		context.Server.NonLoginRequests.Should().HaveCount(5).And.OnlyContain(r => r.Token == "token-1");
	}

	[Fact]
	public async Task ConcurrentFirstCalls_LogInOnce()
	{
		using var context = new SaltTestContext();
		context.Server.OnRun("ping-one.json");

		await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => context.Client.PingAsync(MinionTarget.List("vm-01"), TestContext.Current.CancellationToken)));

		context.Client.LoginCount.Should().Be(1);
	}

	[Fact]
	public async Task A401_LogsInAgainOnce_AndRetriesOnce()
	{
		using var context = new SaltTestContext();
		var restarted = false;
		context.Server
			.On(r => restarted && r.Token == "token-1" && !r.IsLogin
				? FakeSaltServer.Html(HttpStatusCode.Unauthorized, "Authentication error occurred.")
				: null)
			.OnRun("ping-one.json");
		await context.Client.PingAsync(MinionTarget.List("vm-01"), TestContext.Current.CancellationToken);

		// salt-api restarts and forgets every session: the old token is now refused.
		restarted = true;
		var result = await context.Client.PingAsync(MinionTarget.List("vm-01"), TestContext.Current.CancellationToken);

		result["vm-01"].Succeeded.Should().BeTrue();
		context.Client.LoginCount.Should().Be(2);
		context.Server.NonLoginRequests.Select(r => r.Token).Should().Equal("token-1", "token-1", "token-2");
	}

	[Fact]
	public async Task A401AfterTheRelogin_Throws_AndDoesNotLoop()
	{
		using var context = new SaltTestContext();
		context.Server.On(r => r.IsRun ? FakeSaltServer.Html(HttpStatusCode.Unauthorized, "Authorization error occurred.") : null);

		var act = () => context.Client.PingAsync(MinionTarget.List("vm-01"), TestContext.Current.CancellationToken);

		await act.Should().ThrowAsync<SaltAuthenticationException>().WithMessage("*Authorization error occurred*");
		context.Client.LoginCount.Should().Be(2);
		context.Server.NonLoginRequests.Should().HaveCount(2);
	}

	[Fact]
	public async Task A200LoginWithTheHtmlPageAndNoToken_IsAFailedLogin()
	{
		using var context = new SaltTestContext();
		context.Server.On(r => r.IsLogin
			? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<!DOCTYPE html><html><head><title>SaltGUI</title></head></html>") }
			: null);

		var act = () => context.Client.PingAsync(MinionTarget.List("vm-01"), TestContext.Current.CancellationToken);

		await act.Should().ThrowAsync<SaltAuthenticationException>().WithMessage("*without a token*");
		context.Server.NonLoginRequests.Should().BeEmpty("no call may be made without a token");
	}

	[Fact]
	public async Task A200LoginWithJsonButNoToken_IsAFailedLogin()
	{
		using var context = new SaltTestContext();
		context.Server.On(r => r.IsLogin ? FakeSaltServer.Json("""{"return":[{"user":"api-user","perms":[".*"]}]}""") : null);

		var act = () => context.Client.PingAsync(MinionTarget.List("vm-01"), TestContext.Current.CancellationToken);

		await act.Should().ThrowAsync<SaltAuthenticationException>();
		context.Server.NonLoginRequests.Should().BeEmpty();
	}

	[Fact]
	public async Task A401Login_IsAnAuthenticationFailure_WithoutThePassword()
	{
		using var context = new SaltTestContext();
		context.Server.On(r => r.IsLogin ? FakeSaltServer.Html(HttpStatusCode.Unauthorized, "Could not authenticate using provided credentials") : null);

		var act = () => context.Client.PingAsync(MinionTarget.List("vm-01"), TestContext.Current.CancellationToken);

		var thrown = await act.Should().ThrowAsync<SaltAuthenticationException>();
		thrown.Which.ToString().Should().NotContain(SaltTestContext.Password);
		context.Client.LoginCount.Should().Be(1, "a refused login is not retried");
	}

	[Fact]
	public async Task ALoginWithEmptyPerms_IsAConfigurationError()
	{
		using var context = new SaltTestContext();
		context.Server.On(r => r.IsLogin
			? FakeSaltServer.Json("""{"return":[{"token":"t","expire":1791325999.3,"start":1791297199.3,"user":"x","eauth":"file","perms":[]}]}""")
			: null);

		var act = () => context.Client.PingAsync(MinionTarget.List("vm-01"), TestContext.Current.CancellationToken);

		await act.Should().ThrowAsync<SaltAuthenticationException>().WithMessage("*no permissions*");
	}

	[Fact]
	public async Task TheToken_IsRenewed_ShortlyBeforeItExpires()
	{
		using var context = new SaltTestContext();
		context.Server.OnRun("ping-one.json");
		await context.Client.PingAsync(MinionTarget.List("vm-01"), TestContext.Current.CancellationToken);

		// The recorded login expires at 1791325999 (2026-10-06 16:33:19 UTC); the clock starts at 12:00.
		context.Clock.SetUtcNow(DateTimeOffset.FromUnixTimeSeconds(1791325999 - 30));
		await context.Client.PingAsync(MinionTarget.List("vm-01"), TestContext.Current.CancellationToken);

		context.Client.LoginCount.Should().Be(2);
	}

	[Fact]
	public async Task APasswordProvider_IsAskedAtEachLogin()
	{
		var calls = 0;
		using var context = new SaltTestContext(o =>
		{
			o.Password = string.Empty;
			o.PasswordProvider = _ =>
			{
				calls++;
				return ValueTask.FromResult("rotated");
			};
		});
		context.Server.OnRun("ping-one.json");

		await context.Client.PingAsync(MinionTarget.List("vm-01"), TestContext.Current.CancellationToken);

		calls.Should().Be(1);
		context.Server.LoginRequests.Single().Body.Should().Contain("rotated");
	}

	[Fact]
	public async Task NeitherThePasswordNorTheToken_IsLogged()
	{
		using var context = new SaltTestContext(o => o.RequestLogLevel = Microsoft.Extensions.Logging.LogLevel.Information);
		context.Server.OnRun("ping-one.json");

		await context.Client.PingAsync(MinionTarget.List("vm-01"), TestContext.Current.CancellationToken);

		context.Logger.Messages.Should().NotBeEmpty();
		context.Logger.Messages.Should().NotContain(m => m.Contains(SaltTestContext.Password, StringComparison.Ordinal));
		context.Logger.Messages.Should().NotContain(m => m.Contains("token-1", StringComparison.Ordinal));
	}

	[Fact]
	public async Task Logout_ClearsTheToken_AndTheNextCallLogsInAgain()
	{
		using var context = new SaltTestContext();
		context.Server
			.On(r => r.Method == HttpMethod.Post && r.Path == "/logout" ? FakeSaltServer.Json(Fixtures.Load("logout.json")) : null)
			.OnRun("ping-one.json");

		await context.Client.LogoutAsync(TestContext.Current.CancellationToken);
		context.Server.Requests.Should().BeEmpty("there was no session to end");

		await context.Client.PingAsync(MinionTarget.List("vm-01"), TestContext.Current.CancellationToken);
		await context.Client.LogoutAsync(TestContext.Current.CancellationToken);
		await context.Client.PingAsync(MinionTarget.List("vm-01"), TestContext.Current.CancellationToken);

		context.Client.LoginCount.Should().Be(2);
	}

	[Fact]
	public void ParseLogin_ReadsTheRecordedLiveLogin()
	{
		var (token, expire, hasPermissions) = SaltAuthenticatingHandler.ParseLogin(Fixtures.Load("login-ok.json"));

		token.Should().Be("<token>");
		expire.ToUnixTimeSeconds().Should().Be(1791325999);
		hasPermissions.Should().BeTrue();
	}
}
