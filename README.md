# Salt.Api

[![NuGet](https://img.shields.io/nuget/v/Salt.Api.svg)](https://www.nuget.org/packages/Salt.Api)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4.svg)](https://dotnet.microsoft.com/)
[![CI](https://github.com/panoramicdata/Salt.Api/actions/workflows/ci.yml/badge.svg)](https://github.com/panoramicdata/Salt.Api/actions/workflows/ci.yml)
[![Codacy Badge](https://app.codacy.com/project/badge/Grade/8dbdaba778df48ea804b033a564f8e24)](https://app.codacy.com/gh/panoramicdata/Salt.Api/dashboard)
[![Codacy Coverage](https://app.codacy.com/project/badge/Coverage/8dbdaba778df48ea804b033a564f8e24)](https://app.codacy.com/gh/panoramicdata/Salt.Api/dashboard)

A typed, async .NET client for the [Salt Project](https://saltproject.io/) REST API
([`rest_cherrypy`](https://docs.saltproject.io/en/latest/ref/netapi/all/salt.netapi.rest_cherrypy.html)), built for
reading and applying Linux patches safely.

Salt's REST API runs every command through one endpoint, `POST /`, and an account with full rights can run any
function on any minion, including `cmd.run` as root. This package puts the safety in the client:

- an exact **read-only allow-list**, checked twice before anything is sent;
- **per-minion results** that never mistake a failed or missing minion for a clean one;
- a **real patch apply** that cannot be issued by accident.

## Installation

```shell
dotnet add package Salt.Api
```

## Quick start

```csharp
using Salt.Api;
using Salt.Api.Models;

using var client = new SaltClient(new SaltClientOptions
{
	BaseUrl = "https://salt.example.com",
	Username = "api-user",
	Password = passwordFromYourSecretStore, // never from source code
	ReadOnly = true,                        // recommended for anything that only reads
});

// Patch status of two minions
var status = await client.GetPatchStatusAsync(MinionTarget.List("web-01", "web-02"), cancellationToken);
foreach (var minion in status.Values)
{
	Console.WriteLine(minion.Succeeded
		? $"{minion.MinionId}: {minion.Value!.PendingCount} pending, reboot required: {minion.Value.RebootRequired}"
		: $"{minion.MinionId}: FAILED ({minion.FailureKind}): {minion.FailureText}");
}

// Which minions are connected (cheap; prefer this to GET /minions)
IReadOnlyList<string> up = await client.GetMinionsUpAsync(cancellationToken);
```

Create one `SaltClient` per Salt master and reuse it. It logs in once, reuses the token, and logs in again only when
the token is about to expire or Salt answers HTTP 401.

## Capabilities

| Method | Salt call | Read-only mode |
|---|---|---|
| `PingAsync` | `test.ping` | ✅ |
| `GetPatchStatusAsync` | `patchreport.status` (custom module, see below) | ✅ |
| `GetGrainAsync` | `grains.get` | ✅ |
| `GetMinionsUpAsync` | runner `manage.up` | ✅ |
| `GetMinionStatusAsync` | runner `manage.status` | ✅ |
| `GetKeysAsync` | `GET /keys` | ✅ |
| `GetMinionAsync` | `GET /minions/{id}` (one minion's grains) | ✅ |
| `GetJobsAsync`, `GetJobAsync` | `GET /jobs`, `GET /jobs/{jid}` | ✅ |
| `SubmitAsync`, `WaitForJobAsync` | `local_async` + polling `GET /jobs/{jid}` | ✅ for allow-listed functions |
| `PatchDryRunAsync` | `state.apply patch.apply test=True`, as a job | ❌ |
| `PatchApplyAsync` | `state.apply patch.apply`, as a job (**real apply**) | ❌ |
| `ExecuteAsync<T>`, `ExecuteOnMinionsAsync<T>`, `ExecuteAsync(lowstates)` | any `Lowstate` | ✅ if every lowstate is allow-listed |
| `LogoutAsync` | `POST /logout` | ✅ |

There is deliberately no method for `GET /minions`: it returns every grain of every minion (about 100 KB for six
minions) and grows with the estate.

`GetPatchStatusAsync` calls `patchreport.status`, a small custom execution module that is not part of Salt. Its result
maps to `PatchStatus`: `PendingUpgrades`, `PendingCount`, `KeptBack`, `Held`, `KernelPackageNeedsReboot`,
`RebootRequiredFile`, `LastUpgradeEpoch` and `LastUpgradeSource`, plus `RebootRequired`, `PatchingNeeded` and
`NeedsAttention`. A minion without the module returns a string, which is reported as a failure.

## Read-only mode

`ReadOnly` defaults to **`false`**. **Set it to `true` for every consumer that only reads**, such as dashboards and
reports. In read-only mode the client permits only this list, matched exactly and case-sensitively. Anything else
throws `SaltReadOnlyViolationException` **before any request, including the login, is sent**:

| Endpoint or client | Permitted |
|---|---|
| `POST /` with `local` or `local_async` | `test.ping`, `patchreport.status`, `pkg.list_upgrades` (only with `refresh` absent or false), `grains.get`, `grains.items` |
| `POST /` with `runner` | `manage.up`, `manage.status` |
| `POST /` with `wheel` | `key.list_all` |
| `GET` | `/jobs`, `/jobs/{jid}`, `/keys`, `/keys/{id}`, `/minions`, `/minions/{id}`, `/events` |
| `POST` | `/login`, `/logout` |

Refused, however the call is dressed:

- `state.apply`, even with `test=True`: a dry run refreshes apt and takes the apt lock.
- `cmd.*`.
- `pkg.install`, `pkg.upgrade`, `pkg.refresh_db` and the like.
- Any other wheel function, such as `key.accept` or `key.delete`.
- Any other runner.
- `/run` and `/hook`.
- An unknown `client`.
- A lowstate with a missing or empty `client`, `fun` or `tgt`.
- A lowstate key outside `client`, `tgt`, `tgt_type`, `fun`, `arg`, `kwarg` and `timeout`. For example `ret` is refused, because it sends results to a returner.
- A positional argument containing `=`, because Salt would turn it into a keyword argument.

One refused element refuses a whole multi-command request.

The allow-list is enforced in two places:

1. **The request builder.** `Lowstate` has static factories, such as `Lowstate.Ping` and `Lowstate.PatchStatus`, and
   every public factory except `Lowstate.Raw` builds an allow-listed call. A unit test finds the factories by
   reflection, so a new factory that breaks this fails the build. `Lowstate.Raw` builds anything, but needs
   `AllowRawLowstate = true` (default `false`).
2. **The HTTP handler.** It checks the exact bytes about to be sent, so a request built any other way is refused too.

The options are copied when the client is built, so turning off `ReadOnly` afterwards has no effect on that client.

## Per-minion results

Salt answers HTTP 200 even when minions fail. Per-minion calls return a `MinionResultDictionary<T>` of
`MinionResult<T>`, where each result is either `Succeeded` with a `Value`, or a failure with a `FailureKind` and
`FailureText`:

| What Salt returned | Result |
|---|---|
| The expected value | `Succeeded` |
| A string, e.g. `'no.such.function' is not available.` or `Minion did not return. [No response]` | `StringResponse` failure |
| `false` | `FalseResponse` failure |
| A value of the wrong shape | `UnexpectedShape` failure |
| Nothing for an id you listed in `MinionTarget.List` | `NotReturned` failure |
| `{}` because the target matched nothing | `NoMinionsMatched = true`, and `AllSucceeded` is `false` |

The client never throws for one minion's failure. It throws only for HTTP-level problems.

## Patching

```csharp
using var client = new SaltClient(new SaltClientOptions { /* ... */ ReadOnly = false });
var ids = new[] { "web-01", "web-02" };

// 1. Dry run: reports what would change. Runs as an async job and polls until done.
var dryRun = await client.PatchDryRunAsync(new PatchDryRunRequest { Target = MinionTarget.List(ids) }, ct);
foreach (var minion in dryRun.Minions.Values.Where(m => m.Succeeded))
{
	foreach (var (package, change) in minion.Value!.PackageChanges)
	{
		Console.WriteLine($"{minion.MinionId}: {package} {change.Old} -> {change.New}");
	}
}

// 2. Real apply: only after a human has seen the dry run.
var apply = await client.PatchApplyAsync(new PatchApplyRequest
{
	MinionIds = ids,                    // exact ids only, no globs
	ChangeReference = "CHG-12345",      // your approved change
	ConfirmRealApply = true,
}, ct);

// 3. Salt never reboots. Read the patch status and arrange any reboot separately.
var after = await client.GetPatchStatusAsync(MinionTarget.List(ids), ct);
```

`PatchApplyAsync` refuses the request with `SaltPatchGuardException`, **before anything is sent**, unless:

- `ReadOnly` is false;
- `ConfirmRealApply` is true;
- `ChangeReference` is set;
- the minion ids are exact, with no glob, comma or whitespace;
- there are at most 8 ids, unless `AllowMoreThanEightMinions` is set;
- this client ran a successful dry run with no failed state for each id within `DryRunValidityMinutes` (default 60);
- no patch dry run or apply is still running on those minions, because two runs contend for the apt lock.

These guards apply the same way to every estate: the client never infers "test" or "production" from the URL. The
submit is never retried once it may have reached Salt.

Workflow rules the client cannot check for you:

- A production apply needs an approved change.
- Do not apply during a node drain or storage recovery.
- The dry run is not an exact preview: it can list kept-back packages that the real apply will not install.

## Behaviours handled

These were measured against a live Salt 3008 `rest_cherrypy` deployment behind HAProxy.

- **`Accept: application/json` on every request.** Without it, a failed login returns HTTP 200 with the SaltGUI HTML
  page and no token. A login is accepted only if the JSON body has a non-empty `return[0].token` and non-empty
  `perms`, whatever the status code.
- **Error bodies are HTML, not JSON.** The client branches on the status code and includes the start of the text in
  `SaltApiException`.
- **Request bodies are sent with a Content-Length.** `rest_cherrypy` answers a chunked JSON body with HTTP 500.
- **Sessions live in memory** and are lost when salt-api restarts. On HTTP 401 the client logs in again once and
  retries once; a second 401 throws `SaltAuthenticationException`. Logins are serialised, so concurrent first calls
  log in once.
- **Tokens last 8 hours** by default. The client renews the token `TokenRefreshMarginSeconds` (60) before `expire`.
- **HTTP 429** (a proxy rate limit on `/login`): back-off starts at 5 seconds, doubles each time, is capped at 30
  seconds and has jitter.
- **Read-only requests are also retried** on 502, 503 and 504. A request that could change state is retried only on
  401 and 429, which mean it was refused before it ran.
- **Long operations run as jobs.** A synchronous call blocks until the minions answer, and a proxy may close it. The
  patch methods use `local_async` and poll `GET /jobs/{jid}`, every `JobPollIntervalSeconds` (5), until every
  targeted minion has returned or the deadline passes.

## Options

| Option | Default | Notes |
|---|---|---|
| `BaseUrl` | (required) | https only; no default estate |
| `Username`, `Password` | (required) | or `PasswordProvider`, asked at each login |
| `Eauth` | `file` | |
| `ReadOnly` | `false` | set `true` for read-only consumers |
| `AllowRawLowstate` | `false` | needed for `Lowstate.Raw` |
| `HttpClientTimeoutSeconds` | 150 | per request; must exceed `DefaultMinionTimeoutSeconds` |
| `DefaultMinionTimeoutSeconds` | 120 | lowstate `timeout` for `local` calls |
| `JobPollIntervalSeconds` | 5 | |
| `MaxAttemptCount` | 5 | for 429, and 5xx on read-only calls |
| `InitialBackOffDelaySeconds`, `BackOffDelayFactor`, `MaxBackOffDelaySeconds` | 5, 2.0, 30 | |
| `TokenRefreshMarginSeconds` | 60 | |
| `DryRunValidityMinutes` | 60 | |
| `UserAgent` | `Salt.Api/{version}` | |
| `RequestLogLevel` | `Debug` | method, path, status and duration only |

Passwords, tokens and request or response bodies are never logged. Certificates are always validated; there is no
option to turn that off.

## Known limitations and things not yet verified

- **A real apply over HTTP has not been captured** against a live server. Its result is expected to have the same
  shape as the dry run, with `Result` true and the changes filled in. Unit tests cover it with a fake server only.
- **A still-running job lookup** (`GET /jobs/{jid}` before every minion has returned) and the
  `Minion did not return. [No response]` string have not been captured over HTTP. The client handles both shapes as
  documented by Salt.
- **Behaviour during a salt-api restart under load** has not been tested live. The 401 re-login path is unit-tested only.
- `/stats`, `/run`, `/hook` and `/ws` are not used. `GET /events` is permitted in read-only mode but has no method yet.
- Only a service account with `eauth: file` has been used live. Other accounts and eauth backends have not.
- The "no patch run in progress" check reads the 20 most recent `state.apply patch.apply` jobs. A run older than the
  master's job cache is not seen.

## Quality

- `TreatWarningsAsErrors`, nullable reference types, and XML documentation on every public member.
- **Unit tests:** xUnit v3, with skipped tests failing the run, and about 90% line coverage reported to Codacy from CI.
  They replay real responses captured from a live Salt API (with host and account names replaced), and cover:
  - the allow-list, in both directions;
  - "refused before any request";
  - the 401 re-login;
  - the HTML failed login;
  - the per-minion failure shapes;
  - 429 back-off;
  - every patch apply guard.
- **Integration tests** (`Salt.Api.IntegrationTest`) run **read-only calls and one dry run** against a test Salt API.
  They read `SALT_API_BASE_URL`, `SALT_API_USERNAME` and `SALT_API_PASSWORD`. A missing variable fails the test with
  instructions; tests are never skipped. They never perform a real apply.
- Changes that touch the allow-list, the apply guards or retries follow [docs/SAFETY.md](docs/SAFETY.md), alongside
  [CONTRIBUTING.md](CONTRIBUTING.md).

## Links

- NuGet: https://www.nuget.org/packages/Salt.Api
- Source: https://github.com/panoramicdata/Salt.Api
- Issues: https://github.com/panoramicdata/Salt.Api/issues

## License

MIT. See [LICENSE](LICENSE). Salt and the Salt Project are trademarks of their respective owners; this package is not
affiliated with them.
