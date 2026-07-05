## Release 1.0

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|--------------------
SRDB0001 | Security | Error | SurrealQL query constructed via string interpolation or concatenation

## Release 0.1.0

### Notes

- Analyzer relocated from `analyzers/SurrealQlSafetyAnalyzer/` into the
  companion package `SurrealForge.Analyzer`. Namespace changed from
  `SurrealQlSafetyAnalyzer` to `SurrealForge.Analyzer`.
- SRDB0001 extended with **one-hop variable resolution**: when the banned-API
  argument is an `IdentifierNameSyntax` resolving to a local variable whose
  initializer is an unsafe string-construction pattern, the diagnostic now
  fires at the call site with an `AdditionalLocations` entry pointing at the
  declaration. Closes the largest H3 bypass identified in code review.
- Allowlist expanded to include the new `SurrealForge.Client.Query`
  namespace alongside the legacy `Core.SurrealDb.Query` segment.

### Changed Rules

Rule ID | Old Category | New Category | Old Severity | New Severity | Notes
--------|--------------|--------------|--------------|--------------|------
SRDB0001 | Security | Security | Error | Error | One-hop variable resolution added; allowlist namespace expanded
