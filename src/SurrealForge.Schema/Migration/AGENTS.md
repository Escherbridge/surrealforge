# SurrealForge.Schema.Migration — design notes

The `up`/`migrate` file+checksum applier PLUS the model-driven **reconcile**
layer that evolves existing fields in place. The directory-level "why" lives
here; source files carry only terse one-line pointers.

## The problem reconcile fixes (a real production incident)

The checksum-tracked file applier (`MigrationRunner`) and the idempotent emit
mode (`SurqlEmitter`, `IF NOT EXISTS`) can **create** new tables/fields but
**cannot evolve** an existing field's TYPE / DEFAULT / ASSERT.

A consumer changed a POCO field from `option<string>` to `array<string>`. The
regenerated `.surql` was correct, but the live field never changed:

- `SurqlEmitter` emits `DEFINE FIELD IF NOT EXISTS …`. In SurrealDB
  `IF NOT EXISTS` means *define only if absent* — it never alters an existing
  field, so a type change is silently ignored.
- `MigrationRunner` gates on a per-file SHA-256 checksum. On drift it aborts
  unless `--force`; but even when `--force` re-applies, the DDL inside is still
  `IF NOT EXISTS`, so the field type still never changes.

Net: additive-only. Reconcile closes the gap.

## The three pieces

1. **`LiveSchemaIntrospector`** — reads the ACTUAL deployed schema via
   `INFO FOR DB` (table list) + `INFO FOR TABLE <t>` (field/index `DEFINE …`
   strings) and parses those DDL strings back into the same `SchemaModel` shape
   `AttributeSchemaScanner` produces from C# POCOs. Parsing is bracket/quote
   aware so a nested `option<array<string>>` type or a `;` inside an ASSERT does
   not mis-split.

2. **`SchemaDiff`** — diffs DESIRED (C#-derived) vs ACTUAL (introspected) into a
   `SchemaChangeSet`: tables added/removed; fields Added / Removed / TypeChanged
   / ConstraintChanged; indexes added/removed/changed. `IsWidening` marks a
   type change non-destructive only for known value-preserving cases
   (`X → option<X>`, `int → decimal`); everything else (incl. the AZOA
   `option<string> → array<string>`) is destructive-by-default and needs opt-in.

3. **`ReconcileRunner`** — introspect → diff → emit `DEFINE … OVERWRITE`
   (`SurqlEmitter.EmitOptions.Evolve`) for each drifted field/index → apply.
   OVERWRITE *replaces* a definition (unlike IF NOT EXISTS), so the field type
   actually evolves. New fields emit plain `DEFINE FIELD`; removals emit
   `REMOVE FIELD/INDEX/TABLE IF EXISTS` and only run under `--allow-destructive`.

### Desired-model source

`ReconcileRunner` takes a desired `SchemaModel`. Two producers:

- `AttributeSchemaScanner.ScanTypes(assembly)` — when `up --assembly <dll>` is
  passed.
- `SurqlSchemaReader.ReadDirectory(schemasDir)` — the **default**, because the
  deploy container ships only the generated `.surql` files + the CLI dll (no app
  assembly). The reader parses the emitted `.surql` with the SAME DDL extractor
  the introspector uses, so desired and actual normalise identically → no false
  drift.

## SurrealDB 3.x read-back quirks handled

- An optional type reads back as a union: `option<string>` → `none | string`.
  `LiveSchemaIntrospector.NormalizeType` canonicalises a two-branch
  `none | X` / `X | none` back to `option<X>` so it compares equal to the
  scanner's token. (Verified live — see `LiveReconcileTests`.)
- `FLEXIBLE` reads back as `FLEXIBLE TYPE object`; the type token is stripped of
  `FLEXIBLE` and flexibility recorded as an annotation.
- `array<object>` auto-creates a strict `<name>.*` slot; those nested field
  slots (`.` / `[` / `*` in the name) are dropped from the model — the diff
  compares the top-level field set only.

## CLI behaviour (`surrealforge up`)

Phase 1 (schema files) → Phase 2 (hand-authored migrations) → **Phase 3
(reconcile)**. Phase 3 runs by default; flags:

| Flag | Effect |
|---|---|
| *(none)* | Reconcile runs; applies additive + evolve DDL; destructive changes are planned but SKIPPED with a warning. |
| `--force` | Forces the checksum override AND guarantees the reconcile pass (the behavioural fix: a changed field-type actually applies). |
| `--no-reconcile` | Legacy additive-only apply; skips Phase 3. `--force` overrides this. |
| `--dry-run` | Prints the reconcile diff plan (WOULD EVOLVE / WOULD SKIP); zero writes. |
| `--allow-destructive` | Applies drops + narrowing type changes. |
| `--assembly <dll>` | Desired-model source = scanned POCOs (default: parse `--schemas-dir` `.surql`). |

## Coercion failures

An OVERWRITE that narrows a type fails server-side when existing rows can't
coerce. `ReconcileRunner` recognises the SurrealDB coercion phrasings and throws
`SchemaCoercionException` naming the field + server detail (CLI exit code 4) —
never a silent corruption. The operator backfills/migrates the column, then
re-runs. The AZOA case worked because the column was empty/compatible; a general
tool must report the incompatible case legibly.

## Determinism / safety

- All parsing is a pure function of the server reply / file bytes (no clock/env).
- Statement ordering in a reconcile plan is deterministic: adds → evolves →
  index ops → destructive removals last.
- `schema_migration` (the tracking table) is excluded from introspection so it
  never shows up as drift.
