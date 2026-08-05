# SurrealForge.Analyzer

Roslyn analyzer that enforces injection-safe SurrealQL construction in code
that calls into the `SurrealForge.Client` query layer.

## Diagnostic

| ID | Category | Severity | Title |
|----|----------|----------|-------|
| SRDB0001 | Security | Error | SurrealQL query constructed via string interpolation or concatenation |

The analyzer flags banned patterns at the call site of:

- `ISurrealDbClient.Query` / `RawQuery` / `QueryAsync`
- `SurrealQuery.Of(...)` (the parameterized builder entry-point)

Banned argument expressions:

- Interpolated strings: `$"SELECT * FROM {table}"`
- String concatenation: `"SELECT * FROM " + table`
- `string.Format(...)` / `string.Concat(...)`
- `StringBuilder.ToString()` (semantic + heuristic detection)

### One-hop variable resolution

As of v0.1.0 the analyzer follows local-variable initializers one hop:

```csharp
var sql = "SELECT * FROM " + userInput;
var q   = SurrealQuery.Of(sql);   // SRDB0001 fires here
```

The diagnostic reports at the call site and attaches the local declaration
location via `Diagnostic.AdditionalLocations` so the bypass is easy to find.
Multi-hop data flow is intentionally out of scope — one hop closes ~80% of
the H3 bypass surface identified in code review.

### Allowlist

Code whose enclosing namespace contains any of the following is exempt —
these are the safe construction layers themselves, so a blanket
"no interpolation" rule is not a value-correct test inside them:

| Namespace fragment | Why it is trusted |
|---|---|
| `Core.SurrealDb.Query` | Legacy in-repo builder layer. |
| `SurrealForge.Client.Query` | The parameterized `SurrealQuery` builder. |
| `SurrealForge.Vector` | Vector query builder. The KNN operator and brute-force shapes concatenate **validated** identifiers and invariant-formatted `K`/`EF` ints (SurrealDB cannot parameterize them); the embedding itself stays `$q`-bound. |
| `SurrealForge.Schema.Migration` | Migration runner. Emits compile-time literals keyed by a sanitised record id, with a `JsonEscape` helper for the one operator-supplied field (`applied_by`). |

Matching is namespace-substring based; making the allowlist
consumer-configurable is a tracked follow-up.

## Consuming the analyzer

Add a `PackageReference` with `PrivateAssets="all"` so the analyzer applies
to your project without flowing transitively to downstream consumers of your
library:

```xml
<ItemGroup>
  <PackageReference Include="SurrealForge.Analyzer" Version="0.5.0" PrivateAssets="all" />
</ItemGroup>
```

In a multi-project repo using project references, wire the analyzer as an
analyzer-only reference:

```xml
<ItemGroup>
  <ProjectReference Include="packages/SurrealForge.Analyzer/SurrealForge.Analyzer.csproj"
                    OutputItemType="Analyzer"
                    ReferenceOutputAssembly="false" />
</ItemGroup>
```

## Status

Internal-only. Public NuGet publish is deferred until the broader
`SurrealForge.*` package suite is proven internally for 3–6 months.
