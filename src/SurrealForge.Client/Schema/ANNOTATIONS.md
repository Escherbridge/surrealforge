# SurrealDB attribute annotations & naming conventions

Reference for the package's C#-first schema-authoring surface.

These attributes live in `SurrealForge.Client.Schema`. POCOs in the
consumer project decorate themselves with them; the `surrealforge`
CLI's `generate-from-assembly` and `flowcharts-from-assembly`
subcommands consume them at build / publish time.

## Cheat sheet — one table, end-to-end

```csharp
using SurrealForge.Client.Schema;

[SurrealTable("customer_profile",
    Aggregate = "CustomerProfile (Models/CustomerProfile.cs)",
    Guardrail = "G6 SCHEMAFULL")]
[SurrealNote("Display preferences are stored separately from credentials.")]
[Slice("customer_management")]
[Index("customer_profile_customer_region_handle",
       Fields = new[] { "customer_id", "region", "handle" },
       Unique = true)]
public sealed class CustomerProfile
{
    [Id]
    [Column(Order = 1, Type = "string")]
    public string Id { get; set; } = "";

    [Column(Order = 2, Type = "string")]
    [Assert("$value != NONE AND $value != \"\"")]
    public string CustomerId { get; set; } = "";

    [Column(Order = 8, Type = "string")]
    [Inside("Standard", "Managed")]
    public string ProfileType { get; set; } = "";

    [Column(Order = 7, Type = "bool")]
    [Default("false")]
    public bool IsDefault { get; set; }
}
```

Emits one `.surql` file:

```sql
-- ============================================================
-- Table: customer_profile
-- Aggregate: CustomerProfile (Models/CustomerProfile.cs)
-- Guardrail: G6 SCHEMAFULL
-- Note: Display preferences are stored separately from credentials.
-- ============================================================

DEFINE TABLE customer_profile SCHEMAFULL;

DEFINE FIELD id ON TABLE customer_profile TYPE string;
DEFINE FIELD customer_id ON TABLE customer_profile TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD is_default ON TABLE customer_profile TYPE bool
    DEFAULT false;
DEFINE FIELD profile_type ON TABLE customer_profile TYPE string
    ASSERT $value INSIDE ["Standard", "Managed"];

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX customer_profile_customer_region_handle
    ON TABLE customer_profile
    FIELDS customer_id, region, handle
    UNIQUE;
```

## Class-level attributes

| Attribute | Purpose | Notes |
|---|---|---|
| `[SurrealTable("name")]` | Required. Declares the SurrealDB table this POCO maps to. | `Schemafull = true` by default (G6 guardrail). |
| `[SurrealTable(Aggregate = "...")]` | Free-text aggregate header line. | Emitted as `-- Aggregate: …`. |
| `[SurrealTable(Guardrail = "...")]` | Guardrail tag header line. | Emitted as `-- Guardrail: …`. |
| `[SurrealNote("…")]` | Long-form note. Stack as many as needed. | One `-- Note:` line per attribute occurrence. Multi-line strings split on `\n`. |
| `[Slice("customer_management")]` | Aggregate-diagram slice membership. | Drives `<slice>.flowchart.mermaid` grouping + the `_unassigned` orphan bucket. |
| `[Index("name", Fields=…, Unique=true)]` | Multi-column index. | Use on the class when more than one column participates. `Fields` is required at the class level. |

## Property-level attributes

| Attribute | Purpose | Notes |
|---|---|---|
| `[Id]` | Marks the SurrealDB row id column. | Required even if the property is named `Id`. No assert is auto-added; add `[Assert(...)]` explicitly when you want one. |
| `[Column(Order = N)]` | Required for every persisted column. | `Order` drives source-order of fields in the emitted `.surql`. The reflection API does not guarantee CLR property ordering; **always set `Order` explicitly**. |
| `[Column(Name = "snake_case")]` | Override the wire column name. | Default: snake_case of the property name. |
| `[Column(Type = "option<string>")]` | Explicit SurrealDB type token. | Recommended for clarity; the scanner can infer from CLR but explicit beats implicit, especially for `option<...>` wrap intent. |
| `[Column(Type = "array<float, 384>")]` | Dimensioned vector type. | Pass the full token verbatim; the emitter does not parse the inner shape. |
| `[Column(Flexible = true)]` | Emit `FLEXIBLE` modifier before `TYPE`. | Use when the SurrealDB schema is permissive about the inner shape (e.g. embedded JSON blobs whose contract is enforced by the C# POCO at deserialization time). Combines with any `Type`. |
| `[Optional]` | Force `option<T>` wrap. | Use when CLR cannot express the nullability (e.g. an `object?` reference). |
| `[Assert("$value != NONE")]` | Emit a `ASSERT <expr>` clause. | Stackable; multiple `[Assert]`s render in source order. |
| `[Inside("A", "B")]` | Closed-set string field. | Renders as `ASSERT $value INSIDE ["A", "B"]`. Mutually exclusive with `[Assert]` for the same closed-set intent. |
| `[Default("false")]` / `[Default("\"Pending\"")]` | Emit a `DEFAULT <value>` clause. | Value is unquoted — string defaults must include their own quoting (`"\"Pending\""`). |
| `[References(typeof(TTarget))]` | FK to another POCO's table. | Emits `record<target_table>` (or `option<record<…>>` with `Optional = true`). Also drives the slice-flowchart edge from this entity to the target with cardinality `N:1` (or `N:0..1`). Escape hatch: `EmitAsString = true` keeps the wire type as `string` for adapters not yet migrated to record-typed traversal. The netstandard2.0 attribute layer requires `[References(typeof(T))]` form (C# 11+ generic attributes are not available). |
| `[Index("name")]` | Single-column index on this column. | Property-level shortcut; `Fields` defaults to the property's resolved column name. |
| `[HnswIndex("hnsw_document_embedding", Dimension = 384)]` | HNSW vector index (approximate KNN). | Emits the full `HNSW DIMENSION … DIST …` clause. `Dimension` required; optional `Distance` (default `COSINE`), `Type` (`F32`/`F64`/…), `Efc`, `M`. See §Vector indexes. |
| `[MTreeIndex("mtree_document_embedding", Dimension = 384)]` | MTREE vector index (exact KNN). | Same placement rules as `[HnswIndex]`; optional `Capacity` instead of `Efc`/`M`. See §Vector indexes. |
| `[FieldGroup("Logical group header")]` | Cosmetic separator above this field. | Renders as `-- Logical group header` on its own line in the `.surql`. Place on the **first** field of each group only. |

## Field-group placement (subtle but important)

`SurqlEmitter` only emits a `-- <group>` separator line when the
current field's `[FieldGroup]` value differs from the previous field's.
That means:

- Attach `[FieldGroup("Source/target values")]` to the **first**
  field of the group (e.g. `SourceValue`).
- **Do not** repeat it on the following fields (`TargetValue`,
  `InputAmount`, …); they stay within the group implicitly until the next
  `[FieldGroup]` appears.

Matches the long-standing legacy `%% @surreal.fieldgroup "…"` Mermaid
behaviour byte-for-byte.

## Vector indexes

`[HnswIndex]` (approximate KNN) and `[MTreeIndex]` (exact KNN) emit real
SurrealDB vector index DDL:

```sql
DEFINE INDEX hnsw_document_embedding
    ON TABLE document
    FIELDS embedding
    HNSW DIMENSION 384 DIST COSINE TYPE F32 EFC 150 M 12;
```

Rules the scanner enforces (violations throw at scan time):

- `Dimension` is **required** and must be positive — SurrealDB demands it
  at `DEFINE INDEX` time, and there is no CLR type that can carry it.
- Property placement requires a numeric vector CLR type (`float[]`,
  `double[]`, `List<float>`, …). Anything else throws.
- Class placement requires `Fields = new[] { "…" }` naming mapped
  columns or `[ExtraSurrealField]` names — the pattern for embedding
  columns with no POCO surface. Unknown names throw.
- `Distance` (default `COSINE`) and `Type` are validated against the
  SurrealDB keyword sets and upper-cased on emit.

Tuning knobs left unset (`Efc`, `M`, `Capacity`, `Type`) are omitted
from the DDL so the server picks its defaults — and the reconcile diff
treats unset-vs-server-default as **no drift** (see
`SurrealForge.Schema/Migration/AGENTS.md` §Vector indexes).

## Default-value quoting rules

The `Value` passed to `[Default(...)]` is emitted **verbatim** into the
SurrealQL output. SurrealQL is strongly typed, so:

| Column type | Author this | Emitter renders |
|---|---|---|
| `bool` | `[Default("true")]` | `DEFAULT true` |
| `int` | `[Default("50")]` | `DEFAULT 50` |
| `decimal` | `[Default("0.0")]` | `DEFAULT 0.0` |
| `string` | `[Default("\"Pending\"")]` | `DEFAULT "Pending"` |
| `array<string>` | `[Default("[]")]` | `DEFAULT []` |

Forgetting the inner quotes on a string default is the most common
authoring mistake — the emit succeeds but SurrealDB raises a type
error at DDL apply time.

## Slice + flowchart conventions

- Every persisted POCO **should** carry `[Slice("...")]`. Slice names
  use `snake_case`; convention is one slice per logical aggregate
  (`customer_management`, `project_planning`, `document_collection`).
- POCOs without `[Slice]` cluster under the literal name
  `_unassigned` in the master flowchart. This is intentional — an
  orphan bucket is more useful than silently dropping the entity.
- The literal slice `"_skip"` excludes the entity from both per-slice
  and master flowcharts. Reserved for index-only pseudo-tables
  (e.g. an `[HnswIndex]`-only table that exists purely for vector
  search infrastructure).

### Per-slice flowchart shape

```
graph LR
    %% Define Nodes
    customer[customer: Node]:::nodeClass
    customer_profile[customer_profile: Node]:::nodeClass

    %% Define Edges
    customer -- "OWNS [1:N]" --> customer_profile

    classDef nodeClass fill:#f9f9f9,stroke:#333,stroke-width:2px,rx:10px,ry:10px;
```

- Nodes use the `[label: Node]:::nodeClass` shape with the rounded
  `classDef` styling.
- Edge labels follow `"VERB [cardinality]"` where cardinality is one
  of `1:1`, `1:N`, `N:1`, `N:M`, `0..1:1`, `1:0..1`, `0..1:0..1`.
- The emitter sorts every node + edge by ordinal name → byte-stable
  diffs across regenerations.

### Master flowchart shape

```
graph LR

    subgraph customer_management ["customer_management"]
        customer[customer: Node]:::nodeClass
        customer_profile[customer_profile: Node]:::nodeClass
    end

    subgraph project_planning ["project_planning"]
        project[project: Node]:::nodeClass
        project_task[project_task: Node]:::nodeClass
    end

    subgraph _unassigned ["_unassigned"]
        loose_legacy_thing[loose_legacy_thing: Node]:::nodeClass
    end

    %% Edges (slice-local + cross-slice)
    customer -- "OWNS [1:N]" --> customer_profile
    project -- "USES [N:1]" --> project_template

    classDef nodeClass fill:…
```

Every entity (including orphans) shows up; slices are visual
clusters; cross-slice edges connect cluster boundaries.

## SurrealDB special-object naming conventions

| SurrealDB construct | Naming rule | Example |
|---|---|---|
| Table | `snake_case` singular noun | `customer`, `project_task` |
| Column | `snake_case` | `customer_id`, `created_at` |
| Index | `<table>_<columns>_<suffix>` | `customer_region_handle` (compound), `document_owner_id` (single), `order_idempotency_key` |
| Unique index | …`_unique` suffix when the uniqueness is the discriminator; otherwise just include in the name | `customer_region_handle` is implicitly unique by name shape |
| HNSW index | `hnsw_<table>_<column>` | `hnsw_document_embedding` |
| Slice | `snake_case`, aggregate-named | `customer_management`, `document_collection` |
| RELATE edge table | `verb_past_tense` | `forked_from`, `executes`, `follows` |
| State-machine enum | PascalCase CLR name; SurrealDB column stores the snake-cased enum-member name | `OrderStatus` enum → `order.status` column with `INSIDE ["Pending", "Processing", …]` |
| Idempotency key column | `idempotency_key`, always `option<string>`, always `UNIQUE` indexed | required by the G2 guardrail |
| Timestamp columns | `created_at` / `updated_at` / `completed_at` | type `datetime` (or `option<datetime>` for nullable end-states) |
| Soft-delete marker | `is_active: bool DEFAULT true` | the *adapter* (not SurrealDB) enforces hide-on-delete |
| Audit-trail boolean | `is_current: bool` | history rows accumulate with `is_current=false`; uniqueness of the `is_current=true` tuple is enforced by the adapter |

## SurrealQL ASSERT patterns

| Pattern | What it guards | When to use |
|---|---|---|
| `$value != NONE AND $value != ""` | Required string field. | Every required string column. Default for `[Id]` on `string`-keyed tables that don't auto-generate ids. |
| `$value INSIDE ["A", "B"]` | Closed-set string. | Status fields, enum-shaped columns. Use the `[Inside(...)]` attribute. |
| `$value >= 0` | Non-negative number. | Counts, quantities, timestamps-as-ints. |
| `$value > 0` | Strictly positive. | Quantities that must be non-zero. |
| `array::len($value) <= N` | Bounded list. | Tag arrays, metadata key bags. |
| `$value > time::now() - 30d` | Bounded recency. | Cache invalidation keys, presence rows. |

## What does NOT cross to the attribute layer

- **Executable validation logic** lives in a sibling partial-class
  file (`CustomerProfile.Validation.cs`), not in an attribute. The
  `OnValidating` partial method hook drives FluentValidation calls.
- **Cross-aggregate composition** (e.g. "is this profile owned by an
  active customer?") lives in the manager layer. Entity-level attributes
  never touch another aggregate.
- **Generated POCO members** (equality, copy constructors) — those
  are emitted by the Roslyn source-gen and do not appear at the
  attribute layer.

## Foreign keys and graph edges

Decorating a property with `[References(typeof(TargetPoco))]` emits a
`record<target>`-typed column in the `.surql` and renders a directed
edge on the flowchart (per-slice + master). The cardinality glyph
depends on `Optional`:

| Decoration | Emitted type | Flowchart label |
|---|---|---|
| `[References(typeof(Customer))]` | `record<customer>` | `[N:1]` |
| `[References(typeof(Customer), Optional = true)]` | `option<record<customer>>` | `[N:0..1]` |
| `[References(typeof(Customer), EmitAsString = true)]` | `string` (escape hatch) | `[N:1]` |
| `[References(typeof(Customer), Optional = true, EmitAsString = true)]` | `option<string>` | `[N:0..1]` |

The scanner silently strips the legacy `ASSERT $value != NONE AND $value != ""`
on record-typed columns — record IDs cannot be empty strings, and the
assert against a record literal is invalid SurrealQL.

**Adapter migration**: SurrealDB serialises a `record<target>` field on
the wire as either the string form `"target:abc"` or the object form
`{ "tb": "target", "id": "abc" }`. C# stores in `string PropertyName`
properties retain the prefixed string form. Adapters that previously
wrote `CustomerId = guid.ToString("N")` must now write
`CustomerId = "customer:" + guid.ToString("N")`. Consumer migrations own
that adapter rewrite.

## RELATE-edge tables

Tables that exist purely as graph edges between two other tables carry
`[RelateEdge(typeof(From), typeof(To))]` at the class level:

```csharp
[SurrealTable("forked_from")]
[RelateEdge(typeof(WorkflowRun), typeof(WorkflowRun))]
[Slice("workflow")]
public partial class ForkedFrom : ISurrealRecord { ... }
```

The emitter renders this as a native SurrealDB relation table:

```sql
DEFINE TABLE IF NOT EXISTS forked_from
    TYPE RELATION FROM workflow_run TO workflow_run SCHEMAFULL;
```

The synthetic `in` / `out` record columns SurrealDB creates from the
`TYPE RELATION` clause replace any hand-declared `in` / `out` columns
on the POCO. Authors typically still declare `In` / `Out` C# properties
(with `[JsonPropertyName("in")]` / `[JsonPropertyName("out")]`) for
wire-shape round-trip but those declarations are skipped at .surql emit.

On the flowchart, a RELATE edge renders as an N:M arrow from `From` to
`To` with the table name as the label.

## Closed-set enums (DEFINE PARAM)

`[Inside("A", "B", "C")]` on a column emits two things:

1. A `DEFINE PARAM IF NOT EXISTS $<table>_<column> VALUE [...]` block at
   the top of the file (before `DEFINE TABLE`).
2. An `ASSERT $value INSIDE $<table>_<column>` on the column itself.

```sql
-- ── Enums ─────────────────────────────────────────────────

-- Order.StatusKind
DEFINE PARAM IF NOT EXISTS $order_status VALUE
    ["Pending", "Processing", "Completed", "Failed", "Cancelled"];

DEFINE TABLE IF NOT EXISTS order SCHEMAFULL;

...

DEFINE FIELD IF NOT EXISTS status ON TABLE order TYPE string
    ASSERT $value INSIDE $order_status;
```

The C# enum name (e.g. `Order.StatusKind`) is preserved as a doc
comment above the `DEFINE PARAM` so operators can match the closed set
back to the POCO surface. The master flowchart also surfaces every
closed set under a `%% Enums:` legend block at the top.

## Configuration: SurrealForgeOptions

The toolkit reads a single top-level options object,
[SurrealForgeOptions](SurrealForgeOptions.cs), under the
`SurrealDb` section of `appsettings.json`:

```json
{
  "SurrealDb": {
    "Connection": {
      "Url":       "http://localhost:8442",
      "Username":  "root",
      "Password":  "root",
      "Namespace": "test",
      "Database":  "test",
      "JwtToken":  null,
      "ApiKey":    null
    },
    "Generation": {
      "GeneratedPath":         "Persistence/SurrealDb/Generated",
      "GeneratedNamespace":    "YourApp.Generated.SurrealDb",
      "EmitSurql":             true,
      "EmitFlowcharts":        true,
      "EmitDbml":              false,
      "EmitTwinPartialClasses": true
    }
  }
}
```

Register via DI:

```csharp
services.Configure<SurrealForgeOptions>(
    configuration.GetSection(SurrealForgeOptions.ConfigSectionName));
```

Or read directly from the CLI:

```
surrealforge generate-from-assembly bin/Debug/net8.0/YourApp.dll
surrealforge flowcharts-from-assembly bin/Debug/net8.0/YourApp.dll
```

Both CLI subcommands default to the `Generation.GeneratedPath` value
(`Persistence/SurrealDb/Generated/`) so a hand-run regen matches the
build-time output location.

## Acceptance gate

[`AttributePocoByteEquivalenceTests`](../../../tests/YourApp.Tests/Persistence/SurrealDb/AttributePocoByteEquivalenceTests.cs)
discovers every `[SurrealTable]`-decorated POCO in the YourApp
assembly at test time and asserts byte-identical `.surql` emit
against the committed file in
`Persistence/SurrealDb/Generated/Schemas/<table>.surql`.

Three failure modes the gate catches:

1. **POCO drift** — a property was added/removed/retyped without
   regenerating the `.surql`. Fix: run
   `surrealforge generate-from-assembly bin/Debug/net8.0/YourApp.dll`
   and commit the diff.
2. **Missing committed `.surql`** — a new POCO was added without an
   accompanying golden file. Same fix (regenerate).
3. **Drift between intent and emit** — the emitter has a bug. Fix
   the emitter, regenerate, and add a regression test that
   exercises the specific shape.

The legacy Mermaid pipeline (`MermaidParser`, `MermaidSchemaModel`,
`MermaidParseException`, `AggregateEmitter`, the `SurrealForge.SourceGen`
package, the 24 `.mermaid` source files) was retired on 2026-06-03;
attributed POCOs are the only authoring surface.
