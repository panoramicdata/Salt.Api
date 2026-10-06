[assembly: AssemblyFixture(typeof(Salt.Api.IntegrationTest.SaltApiFixture))]

namespace Salt.Api.IntegrationTest;

/// <summary>
/// One read-only client for the whole test run, so that it logs in once. A second client, which is not read-only,
/// exists only for the patch dry run and is created on first use.
/// </summary>
/// <remarks>
/// The credentials come from environment variables. A missing variable FAILS the test with instructions: these
/// tests are never skipped.
/// </remarks>
public sealed class SaltApiFixture : IAsyncDisposable
{
	private const string BaseUrlVariable = "SALT_API_BASE_URL";
	private const string UsernameVariable = "SALT_API_USERNAME";
	private const string PasswordVariable = "SALT_API_PASSWORD";

	private readonly Lazy<SaltClient> _readOnlyClient;
	private readonly Lazy<SaltClient> _dryRunClient;

	public SaltApiFixture()
	{
		_readOnlyClient = new(() => new SaltClient(CreateOptions(readOnly: true)));
		_dryRunClient = new(() => new SaltClient(CreateOptions(readOnly: false)));
	}

	/// <summary>The read-only client. Every test except the dry run uses this one.</summary>
	public SaltClient Client => _readOnlyClient.Value;

	/// <summary>
	/// A client that is NOT read-only, used only by the dry-run test. Tests must never call
	/// <see cref="SaltClient.PatchApplyAsync"/>, <see cref="SaltClient.ExecuteAsync{T}"/> or anything outside the allow-list with it.
	/// </summary>
	public SaltClient DryRunClient => _dryRunClient.Value;

	public async ValueTask DisposeAsync()
	{
		foreach (var client in new[] { _readOnlyClient, _dryRunClient }.Where(c => c.IsValueCreated).Select(c => c.Value))
		{
			await client.LogoutAsync(CancellationToken.None);
			client.Dispose();
		}
	}

	private static SaltClientOptions CreateOptions(bool readOnly) => new()
	{
		BaseUrl = Require(BaseUrlVariable, "the base URL of the TEST Salt API, for example https://salt-test.example.com"),
		Username = Require(UsernameVariable, "the Salt API username"),
		Password = Require(PasswordVariable, "the Salt API password, from your secret store; never commit it"),
		ReadOnly = readOnly,
	};

	private static string Require(string name, string meaning)
	{
		var value = Environment.GetEnvironmentVariable(name);
		if (string.IsNullOrWhiteSpace(value))
		{
			Assert.Fail(
				$"The environment variable {name} is not set. It must hold {meaning}. " +
				$"Set it before running the integration tests, for example in PowerShell: $env:{name} = '...' " +
				$"(or in bash: export {name}=...). These tests run only against a TEST Salt API, and are never skipped.");
		}

		return value!;
	}
}
