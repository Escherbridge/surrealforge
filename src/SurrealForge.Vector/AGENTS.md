# SurrealForge.Vector — design notes

Phase 1 of the vector package family (DESIGN.md in this directory is the
approved plan). This package is the **no-ONNX core**: query surface, encoder
abstraction, chunking, caching, and schema-declared backfill jobs. The
directory-level "why" lives here; source files carry only terse one-line
pointers.

## Query surface (`SurrealVectorSearchExtensions`, `VectorSearchOptions`)

Two SQL paths, per DESIGN.md §Phase 1:

- **Indexed KNN** — `SELECT *, vector::distance::knn() AS dist FROM t WHERE
  field <|K[,EF]|> $q ORDER BY dist`. Requires the Phase-0 HNSW/MTREE index;
  the operator caps results at K, so no LIMIT is emitted. The index's own
  metric applies — `VectorSearchOptions.Metric` is ignored on this path.
- **Brute-force** — `vector::similarity::cosine(field, $q)` (ORDER BY dist
  **DESC**: similarity ranks high-is-close) or
  `vector::distance::{euclidean,manhattan}` (ORDER BY dist **ASC**), always
  with `LIMIT k`. For un-indexed/small tables.

Parameterization contract: the embedding is ALWAYS bound as `$q` — never
interpolated. K and EF are ints formatted invariantly into the operator
because SurrealDB cannot parameterize them; both are range-checked first.
Table names resolve through `SurrealSchemaRegistry` and are re-validated by
`SurrealIdentifier.ForTable`; field paths go through the package-local
`VectorFieldPath` (same allowlist shape as `SurrealQuery.ValidateFieldPath`,
minus the reserved-word denylist — a *column* named `content` is legal where a
*table* named `select` is not).

Extra predicates ride in as a `SurrealQuery` fragment
(`VectorSearchOptions.Filter`): its SQL is appended inside parentheses and its
params merged, so the executor's strict validation still covers every token.
`$q` is reserved and rejected as a filter param.

Result mapping: rows are fetched as `JsonElement` and split — `dist` read off
the object, the record re-deserialized as `T` via `SurrealJsonOptions.Default`
(unknown members such as `dist` are ignored by STJ). This avoids forcing a
`dist` property onto consumer POCOs.

**Analyzer:** the `SurrealForge.Vector` namespace is on the SRDB0001
allowlist (`SurrealQlSafetyAnalyzerDiagnostic.IsInsideSafeLayer`) — this
package IS a safe-construction layer, same trust status as `SurrealQuery`.

## Encoder abstraction (`IVectorEncoder`, `CachedVectorEncoder`)

Interface copied verbatim from DESIGN.md §Encoder abstraction. The batch
overload is **mandatory, not sugar**: batching is the only real ONNX
throughput lever (Phase 2), and every backfill path drains through it.
Search-side encoding is one string per query — negligible; insert-side is N
documents — the cost center. That asymmetry is why the encoder is decoupled
from the write path and why `EmbeddingMode` exists.

`CachedVectorEncoder` is the WriteTime-mode seam: wrap any encoder with the
cache and inline (request-path) embedding gets the same unchanged-text no-op
guarantee the backfill paths have.

## Chunking (`TextChunker`)

Token-budgeted overlapping windows using the ~4-chars/token heuristic
(`CharsPerToken`, configurable). No model dependency by design — exact token
counts belong to the Onnx package (Phase 2). Mid-document windows prefer a
whitespace boundary so words are not split; the overlap re-covers whatever the
cut trimmed. Forward progress is guaranteed (`pos` advances by at least one
char) even under pathological overlap settings.

## Caching (`IEmbeddingCache`, `EmbeddingContentHash`, `InMemoryEmbeddingCache`)

Keyed by content hash, reusing `IdempotencyReplay.ContentHash` from the Client
package (SHA-256 lowercase hex — one canonical hash across the codebase, and
it compiles on netstandard2.0). The in-memory default returns shared arrays —
callers must not mutate them; a distributed implementation can swap in via DI
because registration uses TryAdd.

## Schema scanning (`EmbeddedAttribute`, `EmbeddingSchemaScanner`, `EmbeddingJobDefinition`)

`[Embedded("target_column", Profile = "...", Mode = ...)]` sits on the string
source property, mirroring how indexes are declared. **Placement note:** the
attribute AND the `EmbeddingMode` enum live in `SurrealForge.Client.Schema`
(not here) because the attribute's `Mode` member needs the enum type and the
package reference direction is Vector → Client; putting them beside the other
schema attributes also lets `AttributeSchemaScanner` surface them later
without a new dependency.

The scanner is loud at scan time (non-string source ⇒ throw), matching the
Phase-0 index scanner convention. Source column names resolve exactly like the
schema emitter: explicit `[Column(Name=...)]` wins, else
`SurrealNaming.ToColumnName`. Each definition derives:

- `HashColumn` = `{target}_hash` — the sibling column persisting the source
  text's content hash at embed time (the idempotency skip below needs it).
- `JobName` = `{table}.{target}` — stable job identity; the checkpoint record
  id is this with `.` → `_`.

## Backfill jobs (`EmbeddingBackfillConfigs`, `EmbeddingBackfillRunner`)

Job runner shape per DESIGN.md: bounded `Channel` (backpressure instead of
unbounded memory) with a producer per job kind and one shared consumer that
drains via the batch `EncodeAsync`. One job = one config = one forge.

Consumer semantics (shared by all three kinds):

- **Content-hash skip**: a row whose `stored_hash` equals the hash of its
  current text is skipped (idempotency). Empty/null text is skipped too.
- **Cache-first flush**: within a batch, cache hits are written without
  re-encoding; only misses go to the encoder — in ONE batch call.
- **Writes are parameterized**: per-row
  `UPDATE type::record($_tbl, $_rN) SET target = $_vN, target_hash = $_hN`,
  Combine'd into a single round-trip per flush (params are index-suffixed so
  the merge is collision-free).

### Incremental

Two cursors, deliberately distinct:

- The **paging cursor** (producer-local) is the last id of the previous page —
  it advances even over skipped rows so a fully-embedded table still pages
  forward.
- The **checkpoint** (durable, `UPSERT`ed into the ledger table keyed by job
  name) only advances AFTER a batch's UPDATE round-trip succeeds — crash
  between page-read and write re-processes rows instead of losing them
  (re-processing is safe because of the hash skip). A final checkpoint write
  covers trailing skipped rows so a caught-up table doesn't rescan forever.

Record-id pagination uses `id > type::record($_tbl, $_cursor)` with the bare
key bound as a param — the same `type::record` pattern the Client adopted for
SurrealDB 3.x (a bare string never compares equal to a record id).

### Ad-hoc

Run-once, no ledger. Three scopes: explicit `Ids` (fetched via comma-joined
`type::record($_tbl, $_rN)` FROM sources, all bound), a `Filter` fragment, or
the whole table (the model-upgrade path — pair with `Reembed = true`, which
bypasses the hash skip because the stored hash still matches the *old* model's
input text).

### Dynamic

LIVE-query driven. Phase 1 ships the runner half only: a
`LiveSource` delegate (adapter over
`WebSocketSurrealConnection.LiveAsync`) feeds the same channel/consumer.
Without a delegate it throws `NotSupportedException` naming Phase 2 — the
client's live support is on the concrete WebSocket transport, not
`ISurrealExecutor`, so first-class wiring lands with the Phase 2 package work.
Dynamic rows carry no `stored_hash` (the notification is the change), so the
hash skip degrades to the encode-cache; the write always happens.

### Hosting (`EmbeddingBackfillService`)

`IHostedService` (registered via `AddHostedService`, which is in
Hosting.Abstractions) spawning one worker task per configured job.
Incremental/ad-hoc workers complete when caught up; job failures are logged
(optional `ILogger`) and isolated — one bad job does not take down the host.
`SurrealVectorOptions.ExecutorFactory` is the "dedicated forge" seam: supply
it to give jobs their own connection instead of the app's shared executor.

## DI wiring (`AddSurrealVectorSearch`, `SurrealVectorOptions`, `VectorEncoderRegistry`)

DI split is a locked decision: `AddSurrealForge` lives in Client,
`AddSurrealVectorSearch` lives here. Encoder profiles are a package-local
named registry (options dictionary → `VectorEncoderRegistry`) rather than
keyed DI services — keyed services would pin the minimum DI.Abstractions
feature surface for netstandard2.0 consumers for no gain. Duplicate profile
registration throws (drift is a bug, not a feature — same stance as
`SurrealSchemaRegistry.Register`). The cache registers with TryAdd so a
consumer-supplied cache wins.

## netstandard2.0 notes

Same discipline as Client: no `SHA256.HashData`, no `string.Contains(char)`,
`ValueTask`/`IAsyncEnumerable` come transitively via
`Microsoft.Bcl.AsyncInterfaces`, `System.Threading.Channels` referenced
explicitly for the netstandard2.0 TFM only. No records/init-only members, so
no `IsExternalInit` polyfill is needed here.

## Deferred (not Phase 1)

- ONNX pipeline, tokenizer, pooling (Phase 2 — `SurrealForge.Vector.Onnx`).
- MiniLM content package + `UseMiniLm()` (Phase 3).
- First-class LIVE-query wiring for Dynamic jobs (Phase 2; placeholder throws).
- Write-time interceptor hooked into `SurrealContext`'s save pipeline — Phase 1
  ships the mode declaration + `CachedVectorEncoder` seam only.
- Schema-emitter surfacing of `[Embedded]` (auto-defining the vector + hash
  columns) — the attribute is inert to `AttributeSchemaScanner` today.
- README/sample/packaging polish (Phase 4).
