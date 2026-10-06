namespace Salt.Api;

/// <summary>
/// The Salt rest_cherrypy endpoints this package uses. Login is done by <see cref="SaltAuthenticatingHandler"/>.
/// </summary>
internal interface ISaltApi
{
	[Post("/")]
	Task<JsonElement> RunAsync([Body(BodySerializationMethod.Serialized, buffered: true)] IReadOnlyList<Lowstate> lowstates, CancellationToken cancellationToken);

	[Get("/jobs")]
	Task<JsonElement> GetJobsAsync(CancellationToken cancellationToken);

	[Get("/jobs/{jid}")]
	Task<JsonElement> GetJobAsync(string jid, CancellationToken cancellationToken);

	[Get("/keys")]
	Task<JsonElement> GetKeysAsync(CancellationToken cancellationToken);

	[Get("/minions/{minionId}")]
	Task<JsonElement> GetMinionAsync(string minionId, CancellationToken cancellationToken);

	[Post("/logout")]
	Task<JsonElement> LogoutAsync(CancellationToken cancellationToken);
}
