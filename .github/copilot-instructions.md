# Copilot Repository Instructions

## Formatting
- Always read and follow the repository `.editorconfig` before editing files, including using tabs over spaces where required.
- Preserve the existing indentation style of each file exactly.
- Use tabs, not spaces, for C#, XML, MSBuild, JSON, and most repository files unless `.editorconfig` says otherwise.
- Use spaces only where `.editorconfig` explicitly requires them, such as YAML.
- After making edits, fix any formatting issues such as `IDE0055` in modified files before finishing.
- Do not reformat unrelated code.

## Validation
- When building to verify changes, build only the specific affected project rather than the full solution.
