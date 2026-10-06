# Contributing

Thank you for your interest in contributing to Salt.Api!

## How to Contribute

1. **Fork** the repository
2. **Create a branch** for your feature or fix (`git checkout -b feature/my-feature`)
3. **Make your changes** following the coding standards below
4. **Write or update tests** as appropriate
5. **Ensure the build passes** with zero errors, zero warnings, and zero messages
6. **Submit a Pull Request** against the `main` branch

## Coding Standards

- All public members must have XML documentation comments
- Use `System.Text.Json` — do not introduce `Newtonsoft.Json`
- Use Refit for HTTP client interfaces
- Use file-scoped namespaces and tabs, as `.editorconfig` requires
- Ensure `TreatWarningsAsErrors` remains enabled
- All code must compile with zero diagnostics

## Safety rules for this package

This client can patch servers as root, so some changes need particular care:

- **The read-only allow-list** (`ReadOnlyPolicy`) may only grow by a reviewed change that explains why the new
  function cannot change state on a minion or the master. Add the function to the allow-list tests in both directions.
- **Every public `Lowstate` factory except `Raw` must build an allow-listed call.** A reflection test enforces this.
- **A real apply must stay hard to call by accident.** Do not weaken the `PatchApplyRequest` guards.
- **Never retry a state-changing request** once it may have reached Salt.
- **Never log** passwords, tokens or request and response bodies, and never disable certificate validation.

## Testing

- Use xUnit v3 for all tests, with AwesomeAssertions for fluent assertions
- Unit tests (`Salt.Api.Test`) run without network access; skipped tests fail the run (`failSkips: true`)
- Integration tests (`Salt.Api.IntegrationTest`) need a **test** Salt API and the environment variables
  `SALT_API_BASE_URL`, `SALT_API_USERNAME` and `SALT_API_PASSWORD`. They make read-only calls and one patch dry run.
  Never add an integration test that performs a real apply, changes a key, or calls a function outside the allow-list.
- Ensure all existing tests pass before submitting a PR

## License

By contributing, you agree that your contributions will be licensed under the MIT License.
