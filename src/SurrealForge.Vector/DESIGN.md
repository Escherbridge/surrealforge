# SurrealForge.Vector — package family design

Status: **approved plan** (Phase 0 landed in this branch; Phases 1–4 pending).
Target version: 0.4.0 (all packages release in lockstep via `Directory.Build.props`).

## Goal

First-class vector search + embeddings for SurrealForge users against
SurrealDB's native vector indexes (HNSW approximate KNN, MTREE exact KNN),
with an optional local ONNX embedding engine and a pluggable model package.

## Package topology

| Package | Contents | Deps added |
|---|---|---|
| `SurrealForge.Client` (existing) | `AddSurrealForge` DI entry point (fixes the phantom `SurrealDbServiceCollectionExtensions.AddSurrealForge` referenced at `DefaultSurrealExecutor.cs:14`) | `Microsoft.Extensions.DependencyInjection.Abstractions` |
| `SurrealForge.Schema` (existing) | Vector index emit/introspect/diff (**Phase 0, done**) | — |
| `SurrealForge.Vector` (new, light) | `IVectorEncoder`, `VectorSearchAsync<T>`, `TextChunker`, `IEmbeddingCache`, embedding-mode config + scheduling/backfill jobs, `AddSurrealVectorSearch` | DI abstractions only — **no ONNX** |
| `SurrealForge.Vector.Onnx` (new, engine) | Tokenize → infer → mean-pool → L2-normalize pipeline, `OnnxEncoderOptions` | `Microsoft.ML.OnnxRuntime`, `Microsoft.ML.Tokenizers` |
| `SurrealForge.Vector.Onnx.MiniLM` (new, content) | `model.onnx` + `vocab.txt` as content files + `UseMiniLm()` | `SurrealForge.Vector.Onnx` |

## Locked decisions (do not re-litigate)

1. **Model ships as a separate content package** (`…Onnx.MiniLM`), not embedded
   in the engine package.
2. **DI split:** `AddSurrealForge` lives in Client; `AddSurrealVectorSearch`
   lives in Vector.
3. **Embedding execution is configurable per embedded field** (user decision,
   2026-07-25): *write-time* inline embedding OR *batched job* embedding run by
   a dedicated forge (its own connection/context + worker), declared through
   schema conventions — see §Embedding modes below.

## Phase 0 — schema-emit fix (landed with this design doc)

SurrealForge advertised HNSW support that emitted a **plain** `DEFINE INDEX`:
the scanner dropped `Dimension`/`Distance` because `SchemaIndex` could not
represent them, so no KNN acceleration existed and a real vector index showed
as permanent reconcile drift. Fixed across:

- `SchemaModel.cs` — `SchemaIndex` gained nullable `VectorKind`/`Dimension`/
  `Distance`/`VectorType`/`Efc`/`M`/`Capacity` (null ⇒ legacy byte-identical emit).
- `SurrealAttributes.cs` — `[HnswIndex]` gained `Type`/`Efc`/`M`/`Fields` +
  class placement; new sibling `[MTreeIndex]` with `Capacity`.
- `AttributeSchemaScanner.cs` — carries all params; loud scan-time validation
  (positive Dimension, numeric-vector CLR type, known Distance/Type keywords,
  class-level Fields must resolve to mapped columns or `[ExtraSurrealField]`s).
- `SurqlEmitter.cs` — emits `HNSW DIMENSION … DIST … [TYPE …] [EFC …] [M …]`
  / `MTREE … [CAPACITY …]`; reconcile reuses `EmitIndexStatement`, so evolve
  emits `DEFINE INDEX OVERWRITE … HNSW …`.
- `LiveSchemaIntrospector.cs` — token-walks the vector tail, tolerating INFO's
  derived tokens (`M0`, `LM`, `DOC_IDS_*`, `MTREE_CACHE`).
- `SchemaDiff.cs` — structural params (kind/dimension/dist) strict; tuning
  params (`TYPE`/`EFC`/`M`/`CAPACITY`) desired-null-as-wildcard so server
  defaults never phantom-drift. Rationale: `Migration/AGENTS.md` §Vector indexes.

Done-when (all covered by `tests/SurrealForge.Schema.Tests/Migration/VectorIndexTests.cs`):
golden HNSW/MTREE DDL; INFO-shaped parse; no-drift diff vs server defaults;
plain-index emit byte-identical; scan→emit→read→diff round-trip clean.

## Phase 1 — `SurrealForge.Vector` (core, no ONNX)

### Query surface

- `VectorSearchAsync<T>(field, float[] query, int k, VectorMetric metric, …)`
  supporting **both** paths:
  - **Indexed KNN:** `SELECT …, vector::distance::knn() AS dist FROM t WHERE field <|K[,EF]|> $q ORDER BY dist` —
    requires the Phase-0 index.
  - **Brute-force:** `vector::similarity::cosine(field, $q)` (and distance
    variants) for un-indexed/small tables.
- The embedding is always bound as a `$param` — never interpolated. K and
  metric are `int` + enum.
- **Analyzer (SRDB0001):** the vector query builder namespace must be added to
  the SurrealQL-safety analyzer's allowlist (same trust status as
  `SurrealQuery`). Integration seams: `ISurrealExecutor`, `SurrealQuery.cs:112`
  (`Of`/`WithParam`/`Combine`).

### Encoder abstraction

```csharp
public interface IVectorEncoder
{
    int Dimension { get; }
    ValueTask<float[]> EncodeAsync(string text, CancellationToken ct = default);
    ValueTask<float[][]> EncodeAsync(IReadOnlyList<string> texts, CancellationToken ct = default); // batch overload is MANDATORY
}
```

The batch overload is not optional sugar: batching is the only real ONNX
throughput lever (see Phase 2), and every batched/backfill job path calls it.

### Read/write cost asymmetry (core operational insight)

Search embeds **one** string per query — negligible. Inserts embed **N**
documents — the entire cost center. Therefore the encoder is deliberately
decoupled from the write path, and where embedding happens is a mode choice:

### Embedding modes (per embedded field / job)

```csharp
public enum EmbeddingMode { WriteTime, Batched }
```

- **WriteTime** — encode inline during the insert/update (simplest; costs
  billable CPU on the request path; fine for low write volume).
- **Batched** — writes land with a null/stale vector; a **dedicated forge**
  (its own `SurrealContext`/connection + hosted worker, isolated from the
  request path) fills vectors asynchronously.

### Schema-based scheduling utility + backfill jobs (user decision)

Embedding work is declared in the schema, mirroring how indexes are declared:
an `[Embedded]`-style attribute on the source text column names the target
vector column, encoder profile, and mode. The scanner surfaces these as
**embedding job definitions**; a scheduling utility turns each definition into
a named job with its **own config convention** covering backfill of embeddings
over a historical period on an object or set of objects:

- **Incremental** — checkpointed pages over a historical window (resumable
  cursor persisted in a ledger table; bounded batch size; runs until caught up).
- **Ad-hoc** — run-once invocation over an explicit range/set (CLI or API
  triggered; e.g. re-embed after a model upgrade).
- **Dynamic** — LIVE-query-driven: subscribe to creates/updates on the source
  field and patch vectors back as rows change.

Job runner shape: `IHostedService` + bounded `System.Threading.Channels.Channel`
(backpressure instead of unbounded memory), draining via the batch
`EncodeAsync`. One job = one config = one forge; jobs are independently
schedulable and idempotent (content-hash skip, below).

### Caching

`IEmbeddingCache` keyed by **content hash** (reuse `IdempotencyReplay.ContentHash`)
so re-embedding unchanged text is a no-op across write-time, incremental, and
ad-hoc paths alike.

### Chunking

`TextChunker`: token-budgeted splitter (overlapping windows) so callers can
embed long documents into chunk tables; no model dependency (approximate
char/token heuristics in core; exact token counts only in the Onnx package).

## Phase 2 — `SurrealForge.Vector.Onnx`

Pipeline: **tokenize → infer → mean-pool over attention mask → L2-normalize**.

- **Pooling is the #1 implementation gate.** Raw ONNX output is
  `[batch, seq, 384]`, *not* a sentence vector. Mean-pool over the attention
  mask, then L2-normalize. The correctness test compares against a captured
  reference vector for a fixed input — that test gates the phase.
- **Tokenizer:** BERT WordPiece `vocab.txt` via `Microsoft.ML.Tokenizers` —
  NOT `tokenizer.json`, NOT SharpToken.
- **Session lifetime:** `InferenceSession` is a singleton; `session.Run()` is
  CPU-bound and blocking. `EncodeAsync` returning `ValueTask` buys no
  concurrency — real async = deferring off the request path (Batched mode) +
  batching. Do not pretend otherwise in docs.

```csharp
public sealed class OnnxEncoderOptions
{
    public int IntraOpNumThreads { get; set; } = 1;   // ORT defaults to ALL cores; on fractional vCPU that thrashes — cap to 1–2
    public int MaxBatchSize { get; set; } = 16;
    public int MaxSequenceLength { get; set; } = 256;
    public bool EnableCpuMemArena { get; set; } = true;
}
```

## Phase 3 — `SurrealForge.Vector.Onnx.MiniLM`

`model.onnx` (quantized all-MiniLM-L6-v2, 384-dim) + `vocab.txt` as NuGet
content files copied to output; `o.UseMiniLm()` wires paths + dimension.
Note: bge-small is **not** Matryoshka — do not claim truncatable dims.

## Phase 4 — docs / sample / packaging

README + sample app; `AGENTS.md` per new directory; add projects to
`SurrealForge.slnx`; bump `Directory.Build.props` to 0.4.0.

## Ops guidance (Railway / small-container deployments)

- Railway is CPU-metered, no GPU → CPU EP only → **inline (WriteTime) embedding
  is billable CPU per insert**; prefer Batched mode for meaningful write volume.
- #1 misconfig: ORT `IntraOpNumThreads` defaulting to all cores on fractional
  vCPU — thrashes. Cap to 1–2 (the options default above encodes this).
- Memory: quantized MiniLM engine ≈ +50–140 MB RSS. Provision ≥512 MB; prefer 1 GB.

## Corrections vs earlier drafts (do not reintroduce)

- Mean-pooling + L2-norm is mandatory (raw output is per-token).
- WordPiece `vocab.txt` tokenizer, not `tokenizer.json`/SharpToken.
- Support indexed `<|K|>` KNN **and** brute-force `vector::similarity::*`,
  not brute-force only.
- bge-small is not Matryoshka.
- All source citations in the original pasted advice were hallucinated — cite
  nothing from it.
