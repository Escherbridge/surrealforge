# Query construction conventions

Prefer the strongly typed surface for ordinary single-table operations:

- `SurrealQuery<T>` for reads and projections;
- `SurrealWriter.Create` and `SurrealWriter.Upsert` for full-record writes;
- `SurrealWriter.UpdateOnly<T>` for conditional partial updates;
- `SurrealWriter.DeleteOnly<T>` for conditional deletes.

Raw parameterized SurrealQL is a standing escape hatch only for atomic
multi-table or multi-statement transactions whose guarantees the typed builders
cannot preserve. Unsupported single statements, DDL, and dynamic administrative
queries require a documented waiver with an owner, reason, and expiry. Raw is
not the default for ordinary inserts, updates, deletes, or record-by-id reads.

When touching a query path, prune hand-authored statements that the typed API
can express without weakening atomicity or result-shape guarantees. Keep
identifiers expression-derived, bind every value, require a predicate for
partial updates, and inspect `AffectedCount()` when a transition depends on a
single winner.

`Unset` represents `NONE`; do not silently translate C# `null` into either
`NONE` or JSON `null`. Coercion for string, decimal, enum, and record-reference
predicates and assignments must remain identical to
`SurrealWriter.Create`/`Upsert`.

Fluent builders are immutable: every `Where`, `Set`, and `Unset` returns a new
branch without changing its source. `Set(null)` is invalid, `Unset` accepts only
schema-optional fields, and a table-qualified string id must match `T`.

Ordering is one clause: primary and secondary keys render as
`ORDER BY first ASC, second DESC`. Never emit repeated `ORDER BY` keywords for
one query; `ThenBy`/`ThenByDescending` preserve immutable branch composition.
