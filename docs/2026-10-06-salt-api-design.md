# Salt.Api design (2026-10-06)

The design agreed before the first implementation. The README describes the behaviour; this records the decisions and why.

## Goal

A typed .NET client for Salt's `rest_cherrypy` API that a dashboard can use to read patch state, and that an operator
tool can use to dry-run and apply the patch state, without a forbidden or accidental write being possible.

## Decisions

| Decision | Choice | Why |
|---|---|---|
| Package and repository | `Salt.Api`, public GitHub repository | Matches the `Meraki.Api` and `LogicMonitor.Api` naming. |
| HTTP stack | Refit over `HttpClient`, with System.Text.Json | Refit with System.Text.Json is the standard for these packages. The single generic `POST /` is one Refit method; the union-typed per-minion values are mapped by hand from `JsonElement`. |
| `ReadOnly` default | `false` | The maintainer's decision. Other guards keep the default client from being dangerous; see below. |
| Read-only enforcement | Two layers: the request builder, and the HTTP handler on the outgoing bytes | The server does not enforce read-only for a full-rights account, so the client must. A builder that cannot produce a forbidden call is better than a check that can be forgotten; the handler is the backstop. |
| Raw lowstate | Needs `AllowRawLowstate` (default `false`), and is still allow-list-checked in read-only mode | Arbitrary lowstate bypasses the apply guards, so it is opt-in. |
| Real apply | A separate request type with exact ids, a change reference, a confirmation flag, at most 8 ids by default, a recent successful dry run by the same client, and no patch run in progress on those minions | Makes an accidental apply impossible through the typed API, whatever the `ReadOnly` setting. |
| Production versus test | Never inferred from the URL; the apply guards always apply | The package must not assume which estate it talks to. |
| Dry run in read-only mode | Refused | A dry run refreshes apt and takes the apt lock, and a mistaken omission of `test` would be a real apply. |
| Retries | 401 once (after a fresh login) and 429 for everything; 502, 503 and 504 for allow-listed calls only | A state-changing request must never be sent twice. |
| Long operations | `local_async` plus polling `GET /jobs/{jid}` | Synchronous calls block, and a proxy may close them. |
| `GET /minions` | No method | It returns every grain of every minion. |
| Request bodies | Always sent with a Content-Length | Measured live: `rest_cherrypy` answers a chunked JSON body with HTTP 500. |
| Options | Copied when the client is built | Turning off `ReadOnly` on a shared options object must not change a live client. |

## Units

| Unit | Purpose |
|---|---|
| `SaltClientOptions` | Options and validation. |
| `MinionTarget` | Exact-id lists (validated) or globs. |
| `Lowstate` | The request builder: allow-listed factories, `Raw`, and the internal patch state. |
| `ReadOnlyPolicy` | The allow-list, applied to JSON. |
| `SaltAuthenticatingHandler` | Accept header, login and token reuse, 401 re-login, back-off, the second read-only check, and the Content-Length rule. |
| `ISaltApi` | The Refit interface. |
| `SaltResponseParser` | Maps the envelope, per-minion values, jobs and state runs to the models. |
| `SaltClient` | The public API and the patch workflow guards. |

## Testing

- Unit tests replay sanitised live captures and use a fake server, a manual clock and an instant, recorded delay.
- Integration tests run read-only calls and one dry run on one minion against a test API, and log in once per client.
