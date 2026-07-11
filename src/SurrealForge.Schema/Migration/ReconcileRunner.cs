// SPDX-License-Identifier: UNLICENSED
// SurrealForge.Schema -- model-driven reconcile.
//
// Ties the pieces together: introspect the live DB (LiveSchemaIntrospector) →
// diff it against the desired model (SchemaDiff) → emit OVERWRITE DDL for each
// evolved field/index (SurqlEmitter.Evolve) → apply. This is what makes a
// field-TYPE change ACTUALLY take effect, closing the gap where checksum-
// tracked additive migrations emit IF-NOT-EXISTS and silently skip evolutions.
// See src/SurrealForge.Schema/Migration/AGENTS.md for the full design.

#nullable enable

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SurrealForge.Schema.Generator;
using SurrealForge.Schema.Model;

namespace SurrealForge.Schema.Migration
{
    /// <summary>One planned reconcile statement + why it exists.</summary>
    public sealed class ReconcileStatement
    {
        /// <summary>The DDL that would run (a single DEFINE … / REMOVE … statement).</summary>
        public string Ddl { get; }
        /// <summary>Human-readable reason (mirrors the originating change's Detail).</summary>
        public string Reason { get; }
        /// <summary>True when this statement is destructive (DROP / narrowing OVERWRITE).</summary>
        public bool IsDestructive { get; }

        public ReconcileStatement(string ddl, string reason, bool isDestructive)
        {
            Ddl = ddl;
            Reason = reason;
            IsDestructive = isDestructive;
        }
    }

    /// <summary>Outcome of a reconcile plan or apply.</summary>
    public sealed class ReconcilePlan
    {
        public SchemaChangeSet ChangeSet { get; }
        public IReadOnlyList<ReconcileStatement> Statements { get; }
        /// <summary>Statements actually applied (empty on dry-run / no-op).</summary>
        public IReadOnlyList<ReconcileStatement> Applied { get; }
        /// <summary>
        /// Destructive statements that were SKIPPED because --allow-destructive
        /// was not set. Surfaced so the CLI can print an actionable warning.
        /// </summary>
        public IReadOnlyList<ReconcileStatement> SkippedDestructive { get; }

        public ReconcilePlan(
            SchemaChangeSet changeSet,
            IReadOnlyList<ReconcileStatement> statements,
            IReadOnlyList<ReconcileStatement> applied,
            IReadOnlyList<ReconcileStatement> skippedDestructive)
        {
            ChangeSet = changeSet;
            Statements = statements ?? Array.Empty<ReconcileStatement>();
            Applied = applied ?? Array.Empty<ReconcileStatement>();
            SkippedDestructive = skippedDestructive ?? Array.Empty<ReconcileStatement>();
        }

        public bool HasChanges => ChangeSet.HasChanges;
    }

    /// <summary>
    /// Reconciles a live SurrealDB schema to a desired C#-derived model by
    /// emitting and applying OVERWRITE DDL for drifted fields/indexes.
    /// </summary>
    public sealed class ReconcileRunner
    {
        private readonly ISurrealConnection _connection;
        private readonly LiveSchemaIntrospector _introspector;

        public ReconcileRunner(ISurrealConnection connection)
        {
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
            _introspector = new LiveSchemaIntrospector(connection);
        }

        /// <summary>
        /// Introspect the live DB, diff against <paramref name="desired"/>, and
        /// (unless <paramref name="dryRun"/>) apply the evolve DDL.
        ///
        /// <para>Additive-only DDL (brand-new tables, brand-new fields) is left
        /// to the normal file+checksum migration path — this runner ONLY emits
        /// the statements that <c>IF NOT EXISTS</c> cannot express: field
        /// TYPE / DEFAULT / ASSERT / VALUE / READONLY / FLEXIBLE changes and
        /// index redefinitions, via <c>DEFINE … OVERWRITE</c>. New fields are
        /// also emitted (as plain DEFINE FIELD) so a reconcile brings partially
        /// drifted tables fully into line.</para>
        ///
        /// <para>Destructive changes (field/table/index removal, or a type
        /// change that is not a known widening) are only applied when
        /// <paramref name="allowDestructive"/> is true; otherwise they are
        /// planned but skipped and reported via
        /// <see cref="ReconcilePlan.SkippedDestructive"/>.</para>
        ///
        /// <para>If an OVERWRITE fails server-side because existing row data
        /// cannot coerce to the new type, the apply throws
        /// <see cref="SchemaCoercionException"/> naming the field and the server
        /// detail — never a silent corruption.</para>
        /// </summary>
        public async Task<ReconcilePlan> ReconcileAsync(
            SchemaModel desired,
            bool dryRun = false,
            bool allowDestructive = false,
            CancellationToken ct = default)
        {
            if (desired == null) throw new ArgumentNullException(nameof(desired));

            var actual = await _introspector.IntrospectAsync(ct).ConfigureAwait(false);
            var changeSet = SchemaDiff.Diff(desired, actual);
            var statements = BuildStatements(desired, changeSet);

            if (dryRun)
            {
                return new ReconcilePlan(changeSet, statements, Array.Empty<ReconcileStatement>(), Array.Empty<ReconcileStatement>());
            }

            var applied = new List<ReconcileStatement>();
            var skipped = new List<ReconcileStatement>();
            foreach (var stmt in statements)
            {
                if (stmt.IsDestructive && !allowDestructive)
                {
                    skipped.Add(stmt);
                    continue;
                }
                var result = await _connection.ExecuteAsync(stmt.Ddl, ct).ConfigureAwait(false);
                if (!result.IsOk)
                {
                    if (LooksLikeCoercionError(result.Detail))
                        throw new SchemaCoercionException(stmt.Reason, stmt.Ddl, result.Detail ?? "coercion failure");
                    throw new MigrationApplyException("reconcile: " + stmt.Reason, result.Detail ?? "unknown server error");
                }
                applied.Add(stmt);
            }
            return new ReconcilePlan(changeSet, statements, applied, skipped);
        }

        /// <summary>
        /// Turn a change set into ordered DDL statements. Order: added fields
        /// first (DEFINE FIELD), then evolved fields (DEFINE FIELD OVERWRITE),
        /// then index add/redefine, then destructive removals last (so a
        /// dependent evolve runs before a drop it might rely on). Deterministic.
        /// </summary>
        private static IReadOnlyList<ReconcileStatement> BuildStatements(
            SchemaModel desired, SchemaChangeSet changeSet)
        {
            var desiredByTable = new Dictionary<string, SchemaEntity>(StringComparer.Ordinal);
            foreach (var e in desired.Entities) desiredByTable[e.Name] = e;

            var evolve = SurqlEmitter.EmitOptions.Evolve;   // DEFINE … OVERWRITE
            var strict = SurqlEmitter.EmitOptions.Strict;   // DEFINE … (plain, for new fields)

            var adds = new List<ReconcileStatement>();
            var evolves = new List<ReconcileStatement>();
            var indexOps = new List<ReconcileStatement>();
            var destructive = new List<ReconcileStatement>();

            foreach (var fc in changeSet.Fields)
            {
                switch (fc.Kind)
                {
                    case FieldChangeKind.Added:
                        adds.Add(new ReconcileStatement(
                            SurqlEmitter.EmitFieldStatement(fc.Table, fc.Desired!, strict).TrimEnd('\n'),
                            fc.Table + "." + fc.Field + ": " + fc.Detail, isDestructive: false));
                        break;
                    case FieldChangeKind.TypeChanged:
                    case FieldChangeKind.ConstraintChanged:
                        evolves.Add(new ReconcileStatement(
                            SurqlEmitter.EmitFieldStatement(fc.Table, fc.Desired!, evolve).TrimEnd('\n'),
                            fc.Table + "." + fc.Field + ": " + fc.Detail, fc.IsDestructive));
                        break;
                    case FieldChangeKind.Removed:
                        destructive.Add(new ReconcileStatement(
                            "REMOVE FIELD IF EXISTS " + fc.Field + " ON TABLE " + fc.Table + ";",
                            fc.Table + "." + fc.Field + ": " + fc.Detail, isDestructive: true));
                        break;
                }
            }

            foreach (var ic in changeSet.Indexes)
            {
                switch (ic.Kind)
                {
                    case IndexChangeKind.Added:
                    case IndexChangeKind.Changed:
                        indexOps.Add(new ReconcileStatement(
                            SurqlEmitter.EmitIndexStatement(ic.Table, ic.Desired!, evolve).TrimEnd('\n'),
                            ic.Table + "." + ic.Index + ": " + ic.Detail, isDestructive: false));
                        break;
                    case IndexChangeKind.Removed:
                        destructive.Add(new ReconcileStatement(
                            "REMOVE INDEX IF EXISTS " + ic.Index + " ON TABLE " + ic.Table + ";",
                            ic.Table + "." + ic.Index + ": " + ic.Detail, isDestructive: true));
                        break;
                }
            }

            // Table drops (actual-only). Table CREATES are handled by the normal
            // schema-file apply path, not here.
            foreach (var tc in changeSet.Tables)
            {
                if (tc.Removed)
                {
                    destructive.Add(new ReconcileStatement(
                        "REMOVE TABLE IF EXISTS " + tc.Table + ";",
                        "table " + tc.Table + " removed from model", isDestructive: true));
                }
            }

            var all = new List<ReconcileStatement>(adds.Count + evolves.Count + indexOps.Count + destructive.Count);
            all.AddRange(adds);
            all.AddRange(evolves);
            all.AddRange(indexOps);
            all.AddRange(destructive);
            return all;
        }

        // SurrealDB surfaces a value-cannot-coerce error when an OVERWRITE
        // narrows a field's type but existing rows hold incompatible values.
        // Recognise the common phrasings so the CLI can print a targeted,
        // actionable message instead of a generic apply failure.
        private static bool LooksLikeCoercionError(string? detail)
        {
            if (string.IsNullOrWhiteSpace(detail)) return false;
            string[] markers =
            {
                "Couldn't coerce", "cannot coerce", "Found ", "but expected",
                "does not match", "Expected a", "incompatible",
            };
            foreach (var m in markers)
                if (detail!.IndexOf(m, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }
    }

    /// <summary>
    /// Thrown when an OVERWRITE fails because existing row data cannot coerce to
    /// the new field type. Names the field/change and carries the server detail
    /// so the operator can backfill/migrate the data before retrying — the
    /// legible failure the spec requires over silent corruption.
    /// </summary>
    public sealed class SchemaCoercionException : Exception
    {
        public string Change { get; }
        public string Ddl { get; }

        public SchemaCoercionException(string change, string ddl, string detail)
            : base($"schema evolution '{change}' failed: existing data cannot coerce to the new type " +
                   $"({detail}). Backfill/migrate the column, then re-run. DDL: {ddl}")
        {
            Change = change;
            Ddl = ddl;
        }
    }
}
