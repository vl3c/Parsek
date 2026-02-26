# CLAUDE.md

## Local KSP.log validation

Use this local workflow while developing logging-sensitive features:

1. Run unit tests:
   - `dotnet test Source/Parsek.Tests/Parsek.Tests.csproj`
2. Run an in-game manual scenario in KSP.
3. Validate the latest Parsek log session:
   - `pwsh -File scripts/validate-ksp-log.ps1`

The validator reads `KSP.log`, selects entries from the last
`[Parsek][INFO][Init] SessionStart runUtc=...` marker, and fails if
structured log contracts are violated.

## Notes

- Verbose logging is expected to be enabled during development.
- The validation script exits non-zero on failure and should be treated as a failed local check.
