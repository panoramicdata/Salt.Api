# Safety rules for contributors

This client can patch servers as root, so some changes need particular care:

- **The read-only allow-list** (`ReadOnlyPolicy`) may only grow by a reviewed change that explains why the new
  function cannot change state on a minion or the master. Add the function to the allow-list tests in both directions.
- **Every public `Lowstate` factory except `Raw` must build an allow-listed call.** A reflection test enforces this.
- **A real apply must stay hard to call by accident.** Do not weaken the `PatchApplyRequest` guards.
- **Never retry a state-changing request** once it may have reached Salt.
- **Never log** passwords, tokens or request and response bodies, and never disable certificate validation.

## Integration tests

Integration tests (`Salt.Api.IntegrationTest`) need a **test** Salt API and the environment variables
`SALT_API_BASE_URL`, `SALT_API_USERNAME` and `SALT_API_PASSWORD`. They make read-only calls and one patch dry run.
Never add an integration test that performs a real apply, changes a key, or calls a function outside the allow-list.
