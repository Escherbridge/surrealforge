# Changelog

Notable changes to the SurrealForge packages (`SurrealForge.Client`,
`SurrealForge.Schema`, `SurrealForge.Analyzer`, `SurrealForge.Vector` —
published in lockstep).
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/);
versioning is SemVer with the usual 0.x caveat that minor bumps may carry
breaking changes (called out explicitly below).

## [0.5.0] — 2026-08-04

### Highlights

New **`SurrealForge.Vector`** package: vector search and an embedding pipeline
for SurrealDB, with no ONNX or model dependency. The other three packages are
unchanged apart from the coordinated version bump.

### Added

- **`SurrealForge.Vector`** (`netstandard2.0;net10.0`) — the no-ONNX core of
  the vector package family.
  - **Search surface** — `ISurrealExecutor.VectorSearchAsync<T>(field, query,
    …)` over both paths: indexed HNSW KNN (`SELECT *,
    vector::distance::knn() AS dist … WHERE field <|K,EF|> $q ORDER BY dist`)
    and brute-force (`vector::similarity::cosine` / `vector::distance::{
    euclidean,manhattan}`, ordered by metric direction with `LIMIT k`). The
    embedding is always a bound `$q` param, never interpolated; K/EF are
    range-checked ints; table names resolve through `SurrealSchemaRegistry`
    and field paths through a package-local allowlist. `VectorSearchOptions
    .Filter` merges an extra parameterized predicate into the same `WHERE`.
    Results come back as `VectorSearchResult<T>` — the `dist` projection is
    split off the row, so consumer POCOs need no `dist` property.
  - **Encoder abstraction** — `IVectorEncoder` with a mandatory batch
    overload, a named-profile `VectorEncoderRegistry`, and
    `CachedVectorEncoder` wrapping any encoder in an `IEmbeddingCache`.
  - **Caching** — `IEmbeddingCache` keyed by content hash so re-embedding
    unchanged text is a no-op across write-time, incremental, and ad-hoc
    paths; `InMemoryEmbeddingCache` ships as the default (`TryAdd`, so
    registering your own first wins).
  - **Chunking** — `TextChunker`, a token-budgeted overlapping-window splitter
    using char/token heuristics; no model dependency.
  - **Schema-declared embedding jobs** — `[Embedded(targetColumn, Profile,
    Mode)]` on the source text column (`EmbeddingMode.WriteTime` /
    `Batched`); `EmbeddingSchemaScanner` turns declarations into job
    definitions, and `EmbeddingBackfillService` (an `IHostedService`) drains
    them through a bounded `Channel` for backpressure. Three job shapes:
    incremental (checkpointed, resumable), ad-hoc (run-once over an explicit
    range), and dynamic (change-feed driven). Wire up with
    `services.AddSurrealVectorSearch(o => { o.AddEncoder(…); o.AddJobsFrom<T>(); })`.
  - **Analyzer** — the `SurrealForge.Vector` namespace is on the SRDB0001
    allowlist: this package is itself a safe-construction layer, same trust
    status as `SurrealQuery`.
  - **Scope for this release:** `EmbeddingMode.Batched` is wired end-to-end.
    `WriteTime` is a declaration only — no interceptor hooks `SurrealContext`'s
    save pipeline yet, and `AddJobsFrom<T>` registers jobs for `Batched` fields
    only, so a `WriteTime` field encodes nothing on its own. Dynamic jobs need
    a caller-supplied `DynamicBackfillConfig.LiveSource`. `[Embedded]` is inert
    to `AttributeSchemaScanner`, so the vector and hash columns are declared by
    hand. All three are tracked follow-ups.
- **`[Embedded]` attribute** (`SurrealForge.Client`) — declares a string column
  as the source text for a vector column, naming the target column, encoder
  profile, and `EmbeddingMode`. Emits no DDL; documented in
  `src/SurrealForge.Client/Schema/ANNOTATIONS.md` §Embedded columns, which also
  gains the previously undocumented `[ExtraSurrealField]` entry.
- **Live vector-search verification** —
  `tests/SurrealForge.Client.IntegrationTests/LiveVectorSearchTests.cs` runs
  every generated search shape against a real SurrealDB (indexed KNN, KNN with
  explicit EF, KNN with a merged filter predicate, brute-force cosine /
  euclidean / manhattan, brute-force with filter). Verified against SurrealDB
  3.2.4.

### Fixed

- **Indexed KNN is no longer emitted with a bare `<|K|>` operator.** SurrealDB
  3.x removed the single-argument form (it was the KTree/M-Tree spelling) and
  rejects it with *"Invalid query: the `<|k|>` KNN operator … is no longer
  supported"*. Since `VectorSearchOptions.Ef` defaults to null, the default
  indexed search path failed outright on current SurrealDB. The two-argument
  form is now always emitted, with an unset `Ef` falling back to `max(K, 40)`.
  Caught by the new live tests — the SQL-shape unit tests never reach a
  parser, so they could not have caught it.

### Changed

- `Directory.Build.props` version bumped to `0.5.0`; the publish workflow now
  packs `SurrealForge.Vector` alongside the other three packages.

## [0.4.0] — 2026-07-25

### Highlights

Real SurrealDB vector indexes (HNSW / MTREE), typed conditional mutation
builders, namespace/database-scoped authentication, and a license change to
**MIT**.

### Added

- **Vector index support** — `[HnswIndex]` and the new `[MTreeIndex]` now emit
  real vector index DDL: `DEFINE INDEX … HNSW DIMENSION n DIST d [TYPE t]
  [EFC n] [M n]` / `MTREE … [CAPACITY n]`.
  - Property placement on a numeric vector CLR type (`float[]`, `double[]`,
    `List<float>`, …) or class placement with `Fields = new[] { "…" }`
    targeting mapped columns or `[ExtraSurrealField]` embedding columns with
    no CLR backing.
  - Live introspection parses vector index definitions from `INFO FOR TABLE`
    output, tolerating server-derived tokens (`M0`, `LM`, `DOC_IDS_*`,
    `MTREE_CACHE`), and reconcile evolves indexes via
    `DEFINE INDEX OVERWRITE`.
  - Drift semantics: structural params (kind / `DIMENSION` / `DIST`) compare
    strictly; tuning params (`TYPE` / `EFC` / `M` / `CAPACITY`) left unset in
    the model act as wildcards so server defaults never register as phantom
    drift.
  - Verified against a live SurrealDB: indexed KNN (`<|K,EF|>`) returns
    correct neighbors and repeated reconciles report zero drift.
- **Typed conditional mutation builders** —
  `SurrealWriter.UpdateOnly<T>(id)` / `SurrealWriter.DeleteOnly<T>(id)` build
  parameterized `UPDATE ONLY … SET … WHERE … RETURN AFTER` /
  `DELETE ONLY … WHERE … RETURN BEFORE` statements from typed expressions:
  `Where` (AND-combined), `Set` (typed field/value pairing), `Unset` (emits
  SurrealDB `NONE`, schema-optional fields only). Values are always bound
  parameters; affected-count inspection makes conditional writes race-safe
  (exactly one concurrent winner, verified live).
- **Scoped authentication** — `SurrealConnectionOptions.AuthenticationScope`
  (`Root` / `Namespace` / `Database`) emits `Surreal-Auth-NS` /
  `Surreal-Auth-DB` headers with HTTP Basic auth, for namespace- and
  database-level system users (SurrealDB v2+ authenticates Basic credentials
  at root unless the scope is supplied).
- `SurrealQuery<T>.ThenByDescending(...)`, wired into the LINQ provider.
- `SurrealId` record-id parsing helpers: `BareRecordId`, `ParseRecordGuid`,
  `ParseOptionalRecordGuid` — normalize bare / `table:id` / quoted response
  renderings to a `Guid`.
- `ExpressionTranslator.TranslateMemberPath<T,TValue>` typed overload.
- `eng/Test-LocalClientPrerelease.ps1` — local release-gate script: packs the
  client, asserts nupkg shape, and smoke-tests the package from an isolated
  feed.
- Integration-test fixture honors `SURREALFORGE_TEST_ENDPOINT` /
  `SURREALFORGE_TEST_USER` / `SURREALFORGE_TEST_PASS` for containerized
  SurrealDB instances.

### Changed

- **License changed from UNLICENSED to MIT** across all packages.
- Multi-key ordering now renders as a single `ORDER BY` clause with
  comma-separated keys. Previously each chained `OrderBy` appended its own
  `ORDER BY` fragment, which was not valid multi-key SurrealQL.
- `[Column("name")]` now takes precedence over `[JsonPropertyName]` when
  resolving column names in predicates and ordering, matching how the schema
  emitter resolves the same property.
- Docs and examples rewritten with neutral domain models.

### Fixed

- `[HnswIndex]` (advertised since 0.3.0) previously emitted a **plain**
  `DEFINE INDEX`, silently dropping `Dimension`/`Distance` — no KNN
  acceleration existed, and a real vector index in the database showed as
  permanent reconcile drift. It now emits the full vector clause and
  round-trips cleanly.

### Breaking / upgrade notes

1. **Vector index misconfiguration now throws at scan time.**
   `[HnswIndex]`/`[MTreeIndex]` with a missing or non-positive `Dimension`, a
   non-numeric-vector CLR property, or unknown class-level `Fields` names
   throws instead of silently emitting a useless plain index (the 0.3.0
   behavior was the bug). Fix the attribute; do not suppress the error.
2. **One-time index rebuild on the first reconcile after upgrading.**
   Deployments that used `[HnswIndex]` under 0.3.0 have a plain index under
   the HNSW name; the first 0.4.0 reconcile reports it as Changed and applies
   `DEFINE INDEX OVERWRITE … HNSW …`, which rebuilds the index. This is
   non-destructive, but budget real build time and CPU for large tables.
3. **`SchemaIndex` constructor gained optional vector parameters.**
   Source-compatible; binary-breaking against 0.3.0 — recompile consumers
   (packages ship in lockstep).

## [0.3.0] — 2026-07-11

Model-driven reconcile (`DEFINE … OVERWRITE` evolution of live schemas),
configurable idempotency-ledger retry with colon-key encoding and generic
replay, `SurrealId` Guid ↔ record-id-hex helpers.

## [0.2.0] — 2026-07-10

Schema generator/CLI growth: attribute-driven `.surql` emit, Mermaid
flowchart emitter, migration runner.

## [0.1.1] — 2026-07-05

Package metadata fixes (repository URL, embedded README).

## [0.1.0] — 2026-07-05

Initial release: parameterized SurrealQL client (HTTP + WebSocket), query
builder, schema attributes, `SRDB0001` safety analyzer.
