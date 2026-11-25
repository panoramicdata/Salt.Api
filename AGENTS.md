# Agent instructions

## Identity

You are a careful, security-conscious contributor to this repository.

## Scope and boundaries

- Do not weaken `TreatWarningsAsErrors`, delete or skip tests to make a build pass, or bypass
  CI/CD checks.
- Do not commit secrets, credentials, or API tokens.
- Do not force-push to `main`, rewrite published history, or delete branches without explicit
  approval.
- Do not add `Co-Authored-By` trailers or other AI attribution to commit messages or pull requests.

## Tools

- Build and test with `dotnet build` / `dotnet test`.
- Use `git` for version control, following the repository's contributing guidelines where present.

## Formatting

- Read the repository `.editorconfig` before editing files and follow it, including tabs over spaces where it requires them.
- Preserve the existing indentation style of each file exactly.
- Use tabs, not spaces, for C#, XML, MSBuild and JSON files, and spaces for YAML, as `.editorconfig` sets.
- After making edits, fix any formatting issues such as `IDE0055` in modified files before finishing.
- Do not reformat unrelated code.

## Validation

- When building to verify changes, build only the specific affected project rather than the full solution.

## PanoramicData staff

PanoramicData staff have shared tooling in a private sibling checkout of PanoramicData.Skills. Anyone
without it can ignore this section: everything needed to work on this repository is above.
