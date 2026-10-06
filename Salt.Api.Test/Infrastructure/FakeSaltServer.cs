using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace Salt.Api.Test.Infrastructure;

/// <summary>
/// A request the fake server received.
/// </summary>
internal sealed record RecordedRequest(
	HttpMethod Method,
	string Path,
	string? Body,
	string? Accept,
	string? Token,
	string? UserAgent,
	long? ContentLength)
{
	public bool IsLogin => Method == HttpMethod.Post && Path == "/login";

	public bool IsRun => Method == HttpMethod.Post && Path == "/";
}

/// <summary>
/// An in-memory Salt API. Routes are matched in order; login answers with the recorded live login response unless overridden.
/// </summary>
internal sealed class FakeSaltServer : HttpMessageHandler
{
	private readonly List<Func<RecordedRequest, HttpResponseMessage?>> _routes = [];
	private int _tokenNumber;

	public ConcurrentQueue<RecordedRequest> Requests { get; } = new();

	public IReadOnlyList<RecordedRequest> NonLoginRequests => [.. Requests.Where(r => !r.IsLogin)];

	public IReadOnlyList<RecordedRequest> LoginRequests => [.. Requests.Where(r => r.IsLogin)];

	/// <summary>The token the most recent login issued.</summary>
	public string? CurrentToken { get; private set; }

	/// <summary>Adds a route. The first route that returns a response wins.</summary>
	public FakeSaltServer On(Func<RecordedRequest, HttpResponseMessage?> route)
	{
		_routes.Add(route);
		return this;
	}

	/// <summary>Answers matching requests with a fixture.</summary>
	public FakeSaltServer OnRun(string fixture, Func<RecordedRequest, bool>? when = null)
		=> On(r => r.IsRun && (when?.Invoke(r) ?? true) ? Json(Fixtures.Load(fixture)) : null);

	/// <summary>Answers a GET path with a fixture.</summary>
	public FakeSaltServer OnGet(string path, string fixture)
		=> On(r => r.Method == HttpMethod.Get && r.Path == path ? Json(Fixtures.Load(fixture)) : null);

	protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
	{
		// Read before the body: a streamed (chunked) content has no length until it is buffered.
		var contentLength = request.Content?.Headers.ContentLength;
		var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
		var recorded = new RecordedRequest(
			request.Method,
			request.RequestUri!.AbsolutePath,
			body,
			request.Headers.Accept.ToString(),
			request.Headers.TryGetValues("X-Auth-Token", out var tokens) ? tokens.Single() : null,
			request.Headers.TryGetValues("User-Agent", out var agents) ? string.Join(' ', agents) : null,
			contentLength);
		Requests.Enqueue(recorded);

		foreach (var route in _routes)
		{
			var response = route(recorded);
			if (response is not null)
			{
				return response;
			}
		}

		if (recorded.IsLogin)
		{
			CurrentToken = $"token-{Interlocked.Increment(ref _tokenNumber)}";
			return Json(Fixtures.Load("login-ok.json").Replace("<token>", CurrentToken, StringComparison.Ordinal));
		}

		if (recorded.Token is null || recorded.Token != CurrentToken)
		{
			return Html(HttpStatusCode.Unauthorized, "No permission -- see authorization schemes");
		}

		return Html(HttpStatusCode.NotFound, $"No fake route for {recorded.Method} {recorded.Path}");
	}

	public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK)
		=> new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

	public static HttpResponseMessage Html(HttpStatusCode status, string text)
		=> new(status) { Content = new StringContent($"<html><body>{text}</body></html>", Encoding.UTF8, "text/html") };
}
