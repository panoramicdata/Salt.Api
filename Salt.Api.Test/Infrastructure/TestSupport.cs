using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Salt.Api.Test.Infrastructure;

/// <summary>
/// Loads the recorded responses in the Fixtures folder.
/// </summary>
internal static class Fixtures
{
	public static string Load(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
}

/// <summary>
/// A clock that moves only when told to.
/// </summary>
internal sealed class ManualTimeProvider : TimeProvider
{
	private DateTimeOffset _now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

	public override DateTimeOffset GetUtcNow() => _now;

	public void Advance(TimeSpan by) => _now += by;

	public void SetUtcNow(DateTimeOffset now) => _now = now;
}

/// <summary>
/// Collects log messages.
/// </summary>
internal sealed class ListLogger : ILogger
{
	public ConcurrentQueue<string> Messages { get; } = new();

	public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

	public bool IsEnabled(LogLevel logLevel) => true;

	public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
		=> Messages.Enqueue($"{logLevel}: {formatter(state, exception)} {exception}");
}

/// <summary>
/// Builds a client wired to a fake server, a manual clock and a recorded, instant delay.
/// </summary>
internal sealed class SaltTestContext : IDisposable
{
	public const string Password = "not-a-real-password-7f3a";

	public SaltTestContext(Action<SaltClientOptions>? configure = null, FakeSaltServer? server = null)
	{
		Server = server ?? new FakeSaltServer();
		Options = new SaltClientOptions
		{
			BaseUrl = "https://salt.example.test",
			Username = "api-user",
			Password = Password,
			JobPollIntervalSeconds = 5,
		};
		configure?.Invoke(Options);
		Client = new SaltClient(Options, Logger, Server, Clock, (delay, _) =>
		{
			Delays.Enqueue(delay);
			Clock.Advance(delay);
			return Task.CompletedTask;
		});
	}

	public FakeSaltServer Server { get; }

	public SaltClientOptions Options { get; }

	public ManualTimeProvider Clock { get; } = new();

	public ListLogger Logger { get; } = new();

	public ConcurrentQueue<TimeSpan> Delays { get; } = new();

	public SaltClient Client { get; }

	public void Dispose() => Client.Dispose();
}
