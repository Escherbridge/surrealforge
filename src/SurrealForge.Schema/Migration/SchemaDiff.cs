// SPDX-License-Identifier: UNLICENSED
// SurrealForge.Schema -- model-driven schema diff.
//
// Diffs a DESIRED SchemaModel (from AttributeSchemaScanner over C# POCOs)
// against an ACTUAL SchemaModel (from LiveSchemaIntrospector over a connected
// SurrealDB) and produces a SchemaChangeSet: tables added/removed, and per
// table, fields added / removed / type-changed / default-changed /
// constraint-changed, plus index add/remove. The critical case is a field
// whose TYPE differs — the production bug SurrealForge previously could not
// evolve. See src/SurrealForge.Schema/Migration/AGENTS.md.

#nullable enable

using System;
using System.Collections.Generic;
using System.Text;
using SurrealForge.Schema.Model;

namespace SurrealForge.Schema.Migration
{
    /// <summary>Kind of change detected for a single field.</summary>
    public enum FieldChangeKind
    {
        /// <summary>Field exists in desired, absent in actual → DEFINE FIELD.</summary>
        Added,
        /// <summary>Field exists in actual, absent in desired → DROP (destructive).</summary>
        Removed,
        /// <summary>Field TYPE token differs → DEFINE FIELD OVERWRITE (the keystone case).</summary>
        TypeChanged,
        /// <summary>DEFAULT / ASSERT / VALUE / READONLY / FLEXIBLE differs → OVERWRITE.</summary>
        ConstraintChanged,
    }

    /// <summary>Kind of change detected for a single index.</summary>
    public enum IndexChangeKind
    {
        Added,
        Removed,
        Changed,
    }

    /// <summary>A single field-level change.</summary>
    public sealed class FieldChange
    {
        public string Table { get; }
        public string Field { get; }
        public FieldChangeKind Kind { get; }
        /// <summary>The desired attribute (null for <see cref="FieldChangeKind.Removed"/>).</summary>
        public SchemaAttribute? Desired { get; }
        /// <summary>The actual attribute (null for <see cref="FieldChangeKind.Added"/>).</summary>
        public SchemaAttribute? Actual { get; }
        /// <summary>Human-readable summary of what changed (e.g. "type option&lt;string&gt; → array&lt;string&gt;").</summary>
        public string Detail { get; }
        /// <summary>
        /// True when the change narrows/removes data such that live rows may
        /// fail to coerce (field removal, or a type change that is not a known
        /// widening). Gated behind --allow-destructive at apply time.
        /// </summary>
        public bool IsDestructive { get; }

        public FieldChange(string table, string field, FieldChangeKind kind,
            SchemaAttribute? desired, SchemaAttribute? actual, string detail, bool isDestructive)
        {
            Table = table;
            Field = field;
            Kind = kind;
            Desired = desired;
            Actual = actual;
            Detail = detail;
            IsDestructive = isDestructive;
        }
    }

    /// <summary>A single index-level change.</summary>
    public sealed class IndexChange
    {
        public string Table { get; }
        public string Index { get; }
        public IndexChangeKind Kind { get; }
        public SchemaIndex? Desired { get; }
        public SchemaIndex? Actual { get; }
        public string Detail { get; }
        public bool IsDestructive { get; }

        public IndexChange(string table, string index, IndexChangeKind kind,
            SchemaIndex? desired, SchemaIndex? actual, string detail, bool isDestructive)
        {
            Table = table;
            Index = index;
            Kind = kind;
            Desired = desired;
            Actual = actual;
            Detail = detail;
            IsDestructive = isDestructive;
        }
    }

    /// <summary>A table added (desired-only) or removed (actual-only).</summary>
    public sealed class TableChange
    {
        public string Table { get; }
        /// <summary>True = table exists in desired but not actual (additive create).</summary>
        public bool Added { get; }
        /// <summary>True = table exists in actual but not desired (destructive drop).</summary>
        public bool Removed => !Added;

        public TableChange(string table, bool added)
        {
            Table = table;
            Added = added;
        }
    }

    /// <summary>
    /// The full set of differences between a desired and an actual schema.
    /// <see cref="HasChanges"/> is false when the two are equivalent (a no-op
    /// reconcile). <see cref="IsEmpty"/> is its alias for readability.
    /// </summary>
    public sealed class SchemaChangeSet
    {
        public IReadOnlyList<TableChange> Tables { get; }
        public IReadOnlyList<FieldChange> Fields { get; }
        public IReadOnlyList<IndexChange> Indexes { get; }

        public SchemaChangeSet(
            IReadOnlyList<TableChange> tables,
            IReadOnlyList<FieldChange> fields,
            IReadOnlyList<IndexChange> indexes)
        {
            Tables = tables ?? Array.Empty<TableChange>();
            Fields = fields ?? Array.Empty<FieldChange>();
            Indexes = indexes ?? Array.Empty<IndexChange>();
        }

        public bool HasChanges => Tables.Count > 0 || Fields.Count > 0 || Indexes.Count > 0;
        public bool IsEmpty => !HasChanges;

        /// <summary>True when any change in the set is destructive (drop / narrowing).</summary>
        public bool HasDestructive
        {
            get
            {
                foreach (var f in Fields) if (f.IsDestructive) return true;
                foreach (var i in Indexes) if (i.IsDestructive) return true;
                foreach (var t in Tables) if (t.Removed) return true;
                return false;
            }
        }
    }

    /// <summary>
    /// Computes a <see cref="SchemaChangeSet"/> from a desired vs actual
    /// <see cref="SchemaModel"/>. Pure — no I/O, deterministic ordering.
    /// </summary>
    public static class SchemaDiff
    {
        /// <summary>
        /// Diff <paramref name="desired"/> (C#-derived) against
        /// <paramref name="actual"/> (live-introspected). Tables present only in
        /// desired are reported as additive creates; tables present only in
        /// actual are reported as destructive drops. For tables present in both,
        /// every field is compared by name for add / remove / type / constraint
        /// changes, and every index by name.
        /// </summary>
        public static SchemaChangeSet Diff(SchemaModel desired, SchemaModel actual)
        {
            if (desired == null) throw new ArgumentNullException(nameof(desired));
            if (actual == null) throw new ArgumentNullException(nameof(actual));

            var desiredTables = IndexEntities(desired);
            var actualTables = IndexEntities(actual);

            var tableChanges = new List<TableChange>();
            var fieldChanges = new List<FieldChange>();
            var indexChanges = new List<IndexChange>();

            // Tables only in desired → create; only in actual → drop.
            foreach (var name in SortedUnion(desiredTables.Keys, actualTables.Keys))
            {
                bool inDesired = desiredTables.TryGetValue(name, out var d);
                bool inActual = actualTables.TryGetValue(name, out var a);

                if (inDesired && !inActual)
                {
                    tableChanges.Add(new TableChange(name, added: true));
                    // A brand-new table's fields are all additive — the DEFINE
                    // TABLE + DEFINE FIELD DDL from the normal emit path covers
                    // it, so we do NOT enumerate them as per-field changes here
                    // (keeps additive-migration behaviour unchanged).
                    continue;
                }
                if (!inDesired && inActual)
                {
                    tableChanges.Add(new TableChange(name, added: false));
                    continue;
                }

                DiffFields(name, d!, a!, fieldChanges);
                DiffIndexes(name, d!, a!, indexChanges);
            }

            return new SchemaChangeSet(tableChanges, fieldChanges, indexChanges);
        }

        private static void DiffFields(
            string table, SchemaEntity desired, SchemaEntity actual, List<FieldChange> sink)
        {
            var d = IndexAttributes(desired);
            var a = IndexAttributes(actual);
            foreach (var name in SortedUnion(d.Keys, a.Keys))
            {
                bool inD = d.TryGetValue(name, out var da);
                bool inA = a.TryGetValue(name, out var aa);

                if (inD && !inA)
                {
                    sink.Add(new FieldChange(table, name, FieldChangeKind.Added, da, null,
                        $"add field {name} {da!.Type}", isDestructive: false));
                    continue;
                }
                if (!inD && inA)
                {
                    sink.Add(new FieldChange(table, name, FieldChangeKind.Removed, null, aa,
                        $"remove field {name} (was {aa!.Type})", isDestructive: true));
                    continue;
                }

                // Both present: compare type, then constraints.
                var desiredType = LiveSchemaIntrospector.NormalizeType(da!.Type);
                var actualType = LiveSchemaIntrospector.NormalizeType(aa!.Type);
                if (!string.Equals(desiredType, actualType, StringComparison.Ordinal))
                {
                    bool destructive = !IsWidening(actualType, desiredType);
                    sink.Add(new FieldChange(table, name, FieldChangeKind.TypeChanged, da, aa,
                        $"type {aa.Type} → {da.Type}", isDestructive: destructive));
                    continue;
                }

                var cd = ConstraintDelta(da, aa);
                if (cd != null)
                {
                    sink.Add(new FieldChange(table, name, FieldChangeKind.ConstraintChanged, da, aa,
                        cd, isDestructive: false));
                }
            }
        }

        private static void DiffIndexes(
            string table, SchemaEntity desired, SchemaEntity actual, List<IndexChange> sink)
        {
            var d = IndexIndexes(desired);
            var a = IndexIndexes(actual);
            foreach (var name in SortedUnion(d.Keys, a.Keys))
            {
                bool inD = d.TryGetValue(name, out var di);
                bool inA = a.TryGetValue(name, out var ai);
                if (inD && !inA)
                {
                    sink.Add(new IndexChange(table, name, IndexChangeKind.Added, di, null,
                        $"add index {name}", isDestructive: false));
                    continue;
                }
                if (!inD && inA)
                {
                    sink.Add(new IndexChange(table, name, IndexChangeKind.Removed, null, ai,
                        $"remove index {name}", isDestructive: true));
                    continue;
                }
                if (!IndexEquivalent(di!, ai!))
                {
                    // A redefinition rebuilds the index; not data-destructive.
                    sink.Add(new IndexChange(table, name, IndexChangeKind.Changed, di, ai,
                        $"redefine index {name} (fields [{string.Join(",", ai!.Fields)}] → [{string.Join(",", di!.Fields)}], unique {ai.IsUnique} → {di.IsUnique})",
                        isDestructive: false));
                }
            }
        }

        // ── constraint comparison ──────────────────────────────────────────

        /// <summary>
        /// Compare the diff-relevant constraint annotations (DEFAULT, ASSERT,
        /// VALUE, READONLY, FLEXIBLE) of two attributes with equal type. Returns
        /// a human-readable delta string, or null when equivalent.
        /// </summary>
        private static string? ConstraintDelta(SchemaAttribute desired, SchemaAttribute actual)
        {
            var parts = new List<string>();
            CompareValue(parts, "default", SchemaModelAccessors.FirstArg(desired.Annotations, "default"),
                SchemaModelAccessors.FirstArg(actual.Annotations, "default"));
            CompareValue(parts, "assert", SchemaModelAccessors.FirstArg(desired.Annotations, "assert"),
                SchemaModelAccessors.FirstArg(actual.Annotations, "assert"));
            CompareValue(parts, "value", SchemaModelAccessors.FirstArg(desired.Annotations, "value"),
                SchemaModelAccessors.FirstArg(actual.Annotations, "value"));
            CompareFlag(parts, "readonly", SchemaModelAccessors.HasDirective(desired.Annotations, "readonly"),
                SchemaModelAccessors.HasDirective(actual.Annotations, "readonly"));
            CompareFlag(parts, "flexible", SchemaModelAccessors.HasDirective(desired.Annotations, "flexible"),
                SchemaModelAccessors.HasDirective(actual.Annotations, "flexible"));
            return parts.Count == 0 ? null : string.Join("; ", parts);
        }

        private static void CompareValue(List<string> parts, string label, string? desired, string? actual)
        {
            var d = NormalizeExpr(desired);
            var a = NormalizeExpr(actual);
            if (!string.Equals(d, a, StringComparison.Ordinal))
                parts.Add($"{label} '{actual ?? "<none>"}' → '{desired ?? "<none>"}'");
        }

        private static void CompareFlag(List<string> parts, string label, bool desired, bool actual)
        {
            if (desired != actual) parts.Add($"{label} {actual} → {desired}");
        }

        /// <summary>Collapse whitespace so `a  AND  b` == `a AND b` when comparing expressions.</summary>
        private static string NormalizeExpr(string? expr)
        {
            if (string.IsNullOrWhiteSpace(expr)) return string.Empty;
            var sb = new StringBuilder(expr!.Length);
            bool prevSpace = false;
            foreach (var c in expr!.Trim())
            {
                if (char.IsWhiteSpace(c))
                {
                    if (!prevSpace) sb.Append(' ');
                    prevSpace = true;
                }
                else { sb.Append(c); prevSpace = false; }
            }
            return sb.ToString();
        }

        // ── widening heuristic ─────────────────────────────────────────────

        /// <summary>
        /// True when evolving <paramref name="from"/> → <paramref name="to"/> is
        /// a value-preserving widening that live rows can always coerce into,
        /// so the change is non-destructive. Conservative: only well-known safe
        /// cases return true; everything else is treated as potentially
        /// coercion-breaking and requires --allow-destructive.
        /// <para>Known-safe:
        ///   * X → option&lt;X&gt; (making a field nullable never loses data);
        ///   * X → any / X → flexible object relaxations;
        ///   * int → decimal (numeric widening).
        /// </para>
        /// The AZOA case (option&lt;string&gt; → array&lt;string&gt;) is NOT a
        /// widening — it worked because the column was empty/compatible, which
        /// is exactly why a general tool must surface it as destructive unless
        /// the operator opts in.
        /// </summary>
        internal static bool IsWidening(string from, string to)
        {
            from = LiveSchemaIntrospector.NormalizeType(from);
            to = LiveSchemaIntrospector.NormalizeType(to);
            if (from == to) return true;
            if (to == "any") return true;
            if (to == "option<" + from + ">") return true;              // X → option<X>
            if (from == "int" && to == "decimal") return true;          // numeric widen
            if (from == "int" && to == "float") return true;
            if (to == "option<" + Unwrap(from) + ">" && Unwrap(from) == Unwrap(to)) return true;
            return false;
        }

        private static string Unwrap(string t)
        {
            if (t.StartsWith("option<", StringComparison.Ordinal) && t.EndsWith(">", StringComparison.Ordinal))
                return t.Substring("option<".Length, t.Length - "option<".Length - 1);
            return t;
        }

        private static bool IndexEquivalent(SchemaIndex a, SchemaIndex b)
        {
            if (a.IsUnique != b.IsUnique) return false;
            if (a.Fields.Count != b.Fields.Count) return false;
            for (int i = 0; i < a.Fields.Count; i++)
            {
                if (!string.Equals(a.Fields[i].Trim(), b.Fields[i].Trim(), StringComparison.Ordinal))
                    return false;
            }
            return true;
        }

        // ── indexing helpers ───────────────────────────────────────────────

        private static Dictionary<string, SchemaEntity> IndexEntities(SchemaModel model)
        {
            var d = new Dictionary<string, SchemaEntity>(StringComparer.Ordinal);
            foreach (var e in model.Entities) d[e.Name] = e;
            return d;
        }

        private static Dictionary<string, SchemaAttribute> IndexAttributes(SchemaEntity e)
        {
            var d = new Dictionary<string, SchemaAttribute>(StringComparer.Ordinal);
            foreach (var a in e.Attributes) d[a.Name] = a;
            return d;
        }

        private static Dictionary<string, SchemaIndex> IndexIndexes(SchemaEntity e)
        {
            var d = new Dictionary<string, SchemaIndex>(StringComparer.Ordinal);
            foreach (var i in e.Indexes) d[i.Name] = i;
            return d;
        }

        private static IEnumerable<string> SortedUnion(
            IEnumerable<string> a, IEnumerable<string> b)
        {
            var set = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var x in a) set.Add(x);
            foreach (var x in b) set.Add(x);
            return set;
        }
    }
}
