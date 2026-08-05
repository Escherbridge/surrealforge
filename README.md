# SurrealForge

A homebaked **SurrealDB toolkit for .NET** — a dependency-light HTTP client, a
C#-first schema/migration engine, a vector-search and embedding-pipeline layer,
and a Roslyn analyzer that keeps your SurrealQL injection-safe. Built as a
focused alternative to the pre-1.0 `SurrealDb.Net` SDK, extracted from a
production workflow engine and released under MIT.

Targets `netstandard2.0` and `net10.0`, so it runs everywhere from .NET
Framework tooling to the latest runtime.

## Packages

| Package | Target | What it does |
|---|---|---|
| [`SurrealForge.Client`](src/SurrealForge.Client) | `netstandard2.0;net10.0` | HTTP transport (`POST /sql`), a **parameterized** `SurrealQuery` builder, a `SurrealIdentifier` reserved-word denylist, multi-statement composition, explicit `BeginTransactionAsync()`, JSON converters (with `JsonStringEnumConverter` on by default), and a connection pool with jittered retry. |
| [`SurrealForge.Schema`](src/SurrealForge.Schema) | `netstandard2.0;net10.0` (+ CLI on `net10.0`) | Mermaid-ER parser, `.surql` generator, migration runner backed by a `schema_migration` checksum table, **model-driven reconcile** (live introspection + diff + `DEFINE … OVERWRITE` field evolution), and the `surrealforge` dotnet tool (`up`, `migrate up\|status\|dry-run`, `generate <file>`, `validate <file>`). |
| [`SurrealForge.Analyzer`](src/SurrealForge.Analyzer) | `netstandard2.0` | Roslyn analyzer **SRDB0001** (error severity) — bans string-interpolated / concatenated SurrealQL outside the safe query-builder layer, with one-hop variable resolution to close the most common bypass. |
| [`SurrealForge.Vector`](src/SurrealForge.Vector) | `netstandard2.0;net10.0` | Vector search (indexed HNSW KNN + brute-force `vector::similarity::*`), an `IVectorEncoder` abstraction with a mandatory batch overload, content-hash embedding cache, token-budgeted `TextChunker`, and schema-declared embedding backfill jobs. **No ONNX dependency** — bring your own encoder. |

## Install

```bash
dotnet add package SurrealForge.Client
dotnet add package SurrealForge.Schema    # optional: schema + migrations + CLI
dotnet add package SurrealForge.Analyzer  # optional: compile-time SurrealQL safety
dotnet add package SurrealForge.Vector    # optional: vector search + embedding pipeline
```

The CLI tool:

```bash
dotnet tool install -g SurrealForge.Schema
surrealforge --help
```

## Quick start

```csharp
using SurrealForge.Client;

using SurrealForge.Client;
using SurrealForge.Client.Connection;
using SurrealForge.Client.Query;

var options = new SurrealConnectionOptions
{
    Endpoint  = "http://127.0.0.1:8000",
    Namespace = "app",
    Database  = "main",
    User      = "root",
    Password  = "root",
};

// The connection takes an HttpClient you own (inject a pooled one in real apps).
await using var conn = new HttpSurrealConnection(new HttpClient(), options);

// Parameterized — never string-interpolate user input into SurrealQL.
var query = SurrealQuery
    .Of("SELECT * FROM person WHERE age > $min")
    .WithParam("min", 18);

// The executor maps statement results into typed rows.
var executor = new DefaultSurrealExecutor(conn);
IReadOnlyList<Person> people = await executor.QueryAsync<Person>(query);
```

The `SurrealForge.Analyzer` package flags any raw interpolation
(`$"SELECT * FROM {table}"`) at compile time as **SRDB0001**, so unsafe query
construction fails the build rather than shipping.

## Usage guide

### 1. Connect

`SurrealConnectionOptions` configures endpoint, namespace/database, credentials,
pool size, timeout, and retry. `HttpSurrealConnection` implements
`ISurrealConnection` — depend on the interface, not the concrete transport.

```csharp
var conn = new HttpSurrealConnection(new HttpClient(), options);

// Switch namespace/database at runtime (issues USE NS <ns> DB <db>):
await conn.UseAsync("analytics", "events");
```

SurrealDB 2+ authenticates HTTP Basic credentials at root unless an explicit
system-user scope is supplied. Database users must opt in with the namespace
and database where the user is defined:

```csharp
options.AuthenticationScope = SurrealAuthenticationScope.Database;
```

This adds `Surreal-Auth-NS` and `Surreal-Auth-DB`; it is separate from the
query scope headers and defaults to `Root` for backwards compatibility.

### 2. Raw parameterized queries

`ExecuteRawAsync` returns a `SurrealResponse` — an `IReadOnlyList<SurrealStatementResult>`,
one entry per semicolon-separated statement. Read typed rows with `GetValues<T>(index)`.

```csharp
var response = await conn.ExecuteRawAsync(
    "SELECT * FROM person WHERE city = $city; SELECT count() FROM person",
    new { city = "Cairo" });

response.EnsureAllOk();                                 // throw on any statement error
IReadOnlyList<Person> matches = response.GetValues<Person>(0);
long total = response.GetValues<long>(1).FirstOrDefault();
```

Bind parameters with an anonymous object or an `IDictionary<string, object?>` —
values are serialized with `SurrealJsonOptions.Default`. Prefer the fluent
`SurrealQuery` builder to keep parameters and SQL together:

```csharp
var q = SurrealQuery.Of("SELECT * FROM person")
    .Where("age >= $min", new { min = 21 })
    .OrderBy("name")
    .Fetch("company");                                 // resolve a record link

var adults = await executor.QueryAsync<Person>(q);
var one    = await executor.QuerySingleAsync<Person>(SurrealQuery<Person>.Key("person:jade"));
```

### 3. Strongly-typed query builder

`SurrealQuery<T>` translates C# expressions to SurrealQL:

```csharp
var q = SurrealQuery<Person>.From()
    .Where(p => p.Age >= 21 && p.City == "Cairo")
    .OrderByDescending(p => p.Age);

var rows = await executor.QueryAsync<Person>(q);
```

Typed writes cover inserts, record-shaped upserts, conditional partial updates,
and conditional deletes. Mutation builders require a predicate; updates accept
multiple typed assignments and use an explicit `Unset` for SurrealDB's `NONE`
sentinel. Fluent calls are immutable, so a common predicate can safely branch
into independent mutations:

```csharp
var insert = SurrealWriter.Create(new Person { Id = "jade", Name = "Jade" });
var upsert = SurrealWriter.Upsert(new Person { Id = "jade", Name = "Jade A." });

var update = SurrealWriter.UpdateOnly<Person>("person:jade")
    .Where(p => p.City == "Cairo" && p.Age >= 21)
    .Set(p => p.Name, "Jade A.")
    .Build();

var response = await executor.ExecuteAsync(update);
if (response[0].AffectedCount() != 1)
    throw new InvalidOperationException("The conditional update lost its race.");

var delete = SurrealWriter.DeleteOnly<Person>("person:jade")
    .Where(p => p.Name == "Archived")
    .Build();
```

Prefer these typed primitives for single-table reads and mutations. Raw
parameterized SurrealQL remains the standing escape hatch only for atomic
multi-table or multi-statement transactions the typed builders cannot preserve.
An unsupported single statement, DDL operation, or dynamic administrative query
requires a documented owner, reason, and expiry for removal. Never interpolate
identifiers or values.

### 4. EF-style context (`SurrealContext`)

For a DbContext-like experience with LINQ and change tracking. `T` must
implement `ISurrealRecord`.

```csharp
var ctx = new SurrealContext(conn);

// Query — SurrealQueryable<T> is IQueryable<T> with async terminals:
List<Person> adults = await ctx.Set<Person>()
    .Where(p => p.Age >= 18)
    .ToListAsync();

// Change tracking:
ctx.Add(new Person { Id = "person:new", Name = "Sam", Age = 30 });
await ctx.SaveChangesAsync();
```

`FirstOrDefaultAsync`, `SingleOrDefaultAsync`, `CountAsync`, and `AnyAsync`
terminals are also available.

### 5. Transactions

Statements are buffered and flushed as a single `BEGIN; …; COMMIT;` on commit.
Disposing without committing discards the buffer — nothing was sent — so no
server-side transaction can leak.

```csharp
await using var tx = await conn.BeginTransactionAsync();
// While the transaction handle is open, calls on the connection are buffered:
await conn.ExecuteRawAsync("CREATE account:a SET balance = 100");
await conn.ExecuteRawAsync("UPDATE account:a SET balance -= 10");
await tx.CommitAsync();   // flushes BEGIN; …; COMMIT; — omit to roll back (nothing is sent)
```

### 6. Live queries (WebSocket)

`LIVE SELECT` push notifications arrive over the WebSocket RPC transport
(`WebSocketSurrealConnection`), which runs alongside the HTTP connection —
HTTP `/sql` is request/response and cannot carry push frames.

```csharp
await using var socket = new WebSocketSurrealConnection(options);
await socket.ConnectAsync();          // signs in + selects ns/db

var live = SurrealQuery.Of("LIVE SELECT * FROM person WHERE city = $city")
    .WithParam("city", "Cairo");

await foreach (LiveNotification<Person> n in socket.LiveAsync<Person>(live))
{
    Console.WriteLine($"{n.Action}: {n.Record.Name}");   // Create / Update / Delete
}
// Leaving the loop (or disposing the socket) KILLs the live query.
```

You can also convert a typed `SurrealContext` query into a live subscription via
`ExecuteLiveAsync<T>(socket)`. See **Live queries: status** below for maturity
and current limitations.

## Idempotency ledger

`SurrealForge.Client.Idempotency` ships an exactly-once execution ledger for
irreversible operations, backed by a SurrealDB table with a UNIQUE index on the
key (`SurrealIdempotencyLedger` — `TryClaimAsync` / `CompleteAsync` /
`FailAsync` / `GetAsync`). Behaviour that used to live in each consuming app is
now folded into the package and turned on through options:

```csharp
using SurrealForge.Client.Idempotency;

var ledger = new SurrealIdempotencyLedger(executor, new IdempotencyLedgerOptions
{
    // Retry the claim on SurrealDB 3.x RocksDB transient write-write conflicts
    // ("Transaction conflict: Resource busy … can be retried"). Off by default.
    RetryOnTransientConflict = true,
    MaxConflictRetries       = 8,

    // Base64url-encode stored keys that contain a ':' (the record-id separator),
    // transparently decoded on read. Off by default; the deterministic record id
    // is always SHA-256 of the ORIGINAL key.
    EncodeColonKeys = true,
});

// Or bind from appsettings.json under SurrealDb:Idempotency:
services.Configure<IdempotencyLedgerOptions>(config.GetSection("SurrealDb:Idempotency"));
```

Two more application-agnostic helpers round out the surface:

- **`SurrealTransientConflict`** — the standalone bounded retry primitive
  (`RetryOnConflictAsync` + `IsRetryableConflict`) for any contended
  single-winner claim, usable independently of the ledger.
- **`IdempotencyReplay`** — the content-hash key (`ContentHash`), the JSON
  round-trip (`SerializeForReplay` / `DeserializeForReplay`), and the replay
  state machine (`ReplayFromRecord`). The state machine is generic over your own
  result envelope via `IReplayResultFactory<T, TResult>` (or two lambdas), so
  the package never depends on any app's result type.

## Schema as C# — source of truth

Decorate POCOs with the schema attributes from `SurrealForge.Client`; the
`SurrealForge.Schema` generator reflects over a compiled assembly and emits
deterministic (byte-stable) `.surql` schema files. Regeneration is idempotent,
so a CI drift-check keeps the generated SQL and the C# in lockstep.

```bash
surrealforge generate-from-assembly path/to/YourApp.dll
surrealforge migrate up --endpoint http://127.0.0.1:8000
```

### Model-driven migrations — evolving an existing field

The checksum-tracked file applier can **create** tables/fields, but the
idempotent `DEFINE … IF NOT EXISTS` it emits can never **alter** an existing
one — so a change like `option<string>` → `array<string>` on a live table used
to silently no-op. `surrealforge up` now closes that gap with a **reconcile**
pass:

1. introspect the live schema (`INFO FOR DB` / `INFO FOR TABLE`) into the same
   model shape the C# scanner produces,
2. diff it against the desired model (parsed from the generated `.surql`, or
   from `--assembly <dll>`),
3. emit `DEFINE FIELD OVERWRITE … TYPE <new>` for each drifted field/index and
   apply it — `OVERWRITE` **replaces** the definition, so the type actually
   evolves.

```bash
# Apply schema files + migrations, THEN reconcile drifted field types/defaults:
surrealforge up --connection http://127.0.0.1:8000 \
                --namespace app --database main \
                --schemas-dir ./Schemas --migrations-dir ./Migrations

surrealforge up ... --dry-run            # print the OVERWRITE plan, write nothing
surrealforge up ... --allow-destructive  # also apply drops / narrowing type changes
surrealforge up ... --no-reconcile       # legacy additive-only apply (skip reconcile)
```

Reconcile runs by default; `--force` guarantees it. Destructive changes
(field/table/index removal, narrowing type changes) are planned but **skipped**
unless `--allow-destructive` is set. If existing row data can't coerce to a new
type, the apply fails with a clear `SchemaCoercionException` (exit code 4)
naming the field — never a silent corruption. See
[`src/SurrealForge.Schema/Migration/AGENTS.md`](src/SurrealForge.Schema/Migration/AGENTS.md).

## Vector search and embeddings

`SurrealForge.Vector` adds KNN search and an embedding pipeline on top of the
client. It has **no ONNX or model dependency** — you supply the encoder, so the
package stays small and you keep control of where embedding CPU is spent.

### Searching

Declare the vector column and its index on the model, reconcile as usual, then
search through any `ISurrealExecutor`:

```csharp
[SurrealTable("article")]
public sealed class Article
{
    [Column(Order = 1)]
    public string? Title { get; set; }

    [Column(Order = 2, Type = "array<float>")]
    [HnswIndex("hnsw_article_embedding", Dimension = 384, Distance = "COSINE")]
    public float[]? Embedding { get; set; }
}

// Indexed HNSW KNN — the fast path.
var hits = await executor.VectorSearchAsync<Article>("embedding", queryVector, k: 10);

foreach (var hit in hits)
    Console.WriteLine($"{hit.Record.Title} (dist {hit.Distance:F4})");
```

The embedding is **always** bound as `$q`, never interpolated — the
`SurrealForge.Vector` namespace is on the SRDB0001 allowlist for exactly this
reason. `K` and `EF` are range-checked ints (SurrealDB cannot parameterize
them).

Both search paths are available:

```csharp
// Brute-force, for un-indexed or small tables — no index required.
var hits = await executor.VectorSearchAsync<Article>(
    "embedding", queryVector,
    new VectorSearchOptions
    {
        K = 10,
        Strategy = VectorSearchStrategy.BruteForce,
        Metric = VectorMetric.Cosine,          // or Euclidean / Manhattan
        Filter = SurrealQuery.Of("status = $s").WithParam("s", "published"),
    });
```

`Filter` merges an extra parameterized predicate into the same `WHERE` on
either path. On the indexed path the index's own metric applies, so `Metric` is
ignored there; `Ef` (the HNSW beam width) defaults to `max(K, 40)`.

> **SurrealDB 3.x note:** the single-argument `<|K|>` operator was removed, so
> the indexed path always emits `<|K,EF|>`. Verified live against SurrealDB
> 3.2.4.

### Embedding pipeline

Encoders implement `IVectorEncoder`; the batch overload is mandatory, because
batching is the only real throughput lever:

```csharp
public interface IVectorEncoder
{
    int Dimension { get; }
    ValueTask<float[]> EncodeAsync(string text, CancellationToken ct = default);
    ValueTask<float[][]> EncodeAsync(IReadOnlyList<string> texts, CancellationToken ct = default);
}
```

Search embeds **one** string per query — negligible. Inserts embed **N**
documents — the entire cost center. So where embedding happens is a per-field
choice declared on the schema, not baked into the write path.

> **What ships in 0.5.0:** `Batched` is wired end-to-end — declare it, register
> the job, and the hosted service fills vectors for you. `WriteTime` is
> currently a **declaration only**: no interceptor hooks `SurrealContext`'s save
> pipeline yet, so a `WriteTime` field registers no job and nothing encodes on
> your behalf. Until the interceptor lands, call the encoder yourself before
> saving (wrap it in `CachedVectorEncoder` to get the content-hash skip).

```csharp
[SurrealTable("article")]
public sealed class Article
{
    // Source text -> target vector column, with the mode declared inline.
    [Column(Order = 1)]
    [Embedded("embedding", Mode = EmbeddingMode.Batched)]   // or WriteTime
    public string? Body { get; set; }

    [Column(Order = 2, Type = "array<float>")]
    [HnswIndex("hnsw_article_embedding", Dimension = 384, Distance = "COSINE")]
    public float[]? Embedding { get; set; }
}
```

The vector column can also be schema-only — put `[ExtraSurrealField("embedding",
"array<float>")]` on the class instead, and the column exists in SurrealDB with
no CLR property surfacing it.

`EmbeddingSchemaScanner` turns those declarations into job definitions, and the
hosted `EmbeddingBackfillService` drains them through a bounded channel
(backpressure, not unbounded memory):

```csharp
services.AddSurrealForge(...);                 // executor, from SurrealForge.Client
services.AddSurrealVectorSearch(o =>
{
    o.AddEncoder(new MyEncoder());             // default profile
    o.AddJobsFrom<Article>();                  // Batched fields become jobs
});
```

Backfill runs in three shapes — **incremental** (checkpointed pages over a
historical window, resumable), **ad-hoc** (run-once over an explicit range,
e.g. after a model upgrade), and **dynamic** (change-feed driven). Re-embedding
unchanged text is a no-op: `IEmbeddingCache` is keyed by content hash, so
write-time, incremental, and ad-hoc paths all skip work they've already done.
`TextChunker` splits long documents into overlapping token-budgeted windows
using char/token heuristics — no model dependency.

Design rationale and the full job/config contract:
[`src/SurrealForge.Vector/AGENTS.md`](src/SurrealForge.Vector/AGENTS.md).

**Current limitations:** `WriteTime` mode is declarative only (see the note
above). Dynamic (LIVE-query) jobs require you to supply your own
`DynamicBackfillConfig.LiveSource` adapter — `RunDynamicAsync` throws
`NotSupportedException` without one — because the client's live support sits on
the concrete WebSocket transport rather than `ISurrealExecutor`. Both are
tracked follow-ups, as is having the schema emitter auto-define the vector and
hash columns from `[Embedded]` (today the attribute is inert to
`AttributeSchemaScanner`, so you declare those columns yourself).

## Building from source

```bash
dotnet restore SurrealForge.slnx
dotnet build   SurrealForge.slnx -c Release
dotnet test    SurrealForge.slnx
```

- **Unit tests** (`SurrealForge.Client.Tests`, `.Schema.Tests`,
  `.Analyzer.Tests`) run with no external dependencies.
- **Integration tests** (`SurrealForge.Client.IntegrationTests`) require a
  running SurrealDB instance and are skipped without one.

## Versioning

The version lives in one place — `Directory.Build.props` — and applies to all
four packages, which are released in lockstep. Publishing happens via the
`publish` GitHub Actions workflow on a release tag (`v0.1.0`, …).

## License

MIT — see [LICENSE](LICENSE).

## Status

`0.5.0` — adds the new `SurrealForge.Vector` package (KNN + brute-force search,
encoder abstraction, chunking, content-hash embedding cache, schema-declared
backfill jobs). Additive for existing users: the other three packages are
unchanged apart from the version bump. The public API may still shift before
`1.0`.

Known limitations / roadmap:

- **Vector dynamic jobs**: LIVE-query-driven backfill needs a caller-supplied
  `LiveSource` adapter; first-class wiring is a tracked follow-up, as are the
  planned `SurrealForge.Vector.Onnx` / `.Onnx.MiniLM` encoder packages.

- **Typed-builder `Contains` → `INSIDE`**: under the .NET 10 SDK, translating
  `list.Contains(x.Field)` through the strongly-typed `SurrealQuery<T>.Where`
  builder is not yet supported (two tests skipped). Use
  `SurrealQuery.Of(...).Where(...)` for `INSIDE` membership in the meantime.
- **Analyzer allowlist** is namespace-substring based; making it
  consumer-configurable (rather than shipping a fixed allowlist) is a tracked
  follow-up.

### Live queries: status

The WebSocket LIVE-query transport (`WebSocketSurrealConnection`,
`LiveAsync<T>`, `ExecuteLiveAsync<T>`) is **implemented but experimental** in
`0.1.0`. It handles sign-in, `use`, `LIVE SELECT`, JSON-RPC request/notification
demultiplexing, CREATE/UPDATE/DELETE notification parsing, and `KILL`-on-teardown.

Before it graduates to "supported", the remaining work is:

1. **Test coverage** — there are currently no automated tests for the live path.
   Needed: a fake-socket unit test for the demux/parse logic, plus integration
   tests that drive `CREATE`/`UPDATE`/`DELETE` against a live SurrealDB and
   assert the streamed notifications.
2. **Reconnect / resilience** — on a dropped socket the receive loop exits
   silently and the stream simply ends; there is no auto-reconnect or
   re-subscribe, and mid-stream errors are swallowed rather than surfaced to the
   consumer. Production use needs reconnect-with-resubscribe and an error
   channel on the async stream.
3. **Auth parity** — sign-in currently uses user/password only; token/JWT
   sign-in (the `Jwt` option) is not yet wired for the WebSocket transport.
4. **Backpressure policy** — notifications buffer in an unbounded channel; a slow
   consumer can grow memory unbounded. A bounded-channel + drop/oldest policy
   should be configurable.
5. **Ergonomics** — optionally expose live subscriptions through a small facade
   (or `IServiceCollection` registration) rather than constructing the WebSocket
   connection directly.

In short: the protocol plumbing already exists and works against SurrealDB 3.x;
what's left is hardening (reconnect + errors), tests, and auth/backpressure
polish — not a ground-up build.
