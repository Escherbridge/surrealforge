// SPDX-License-Identifier: UNLICENSED
// SurrealForge.Schema -- Deterministic .surql generator (Phase 4 task 21).
//
// SchemaModel  ->  SurqlEmitter.Emit  ->  string
//
// Determinism contract: same model input always produces the same byte
// sequence on output. No clock reads, no environment lookups, no
// HashSet enumeration order leakage. Entities, fields, indexes, and
// header lines are emitted in their original source order.
//
// Output shape (matches wave-1 stylistic conventions):
//
//   -- ============================================================
//   -- Table: <name>
//   -- Aggregate: <text>
//   -- Guardrail: <text>
//   -- Note: <text>
//   -- ============================================================
//
//   DEFINE TABLE <name> SCHEMAFULL;
//
//   DEFINE FIELD <name> ON TABLE <name> TYPE <type>
//       ASSERT <expr>
//       DEFAULT <value>;
//   ...
//
//   -- <section header>
//
//   DEFINE INDEX <idx_name>
//       ON TABLE <name>
//       FIELDS f1, f2
//       UNIQUE;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SurrealForge.Schema.Model;

namespace SurrealForge.Schema.Generator
{
    /// <summary>
    /// Emits Surreal-QL DDL (`.surql`) from a parsed
    /// <see cref="SchemaModel"/>. Pure function — no I/O, no clocks.
    /// </summary>
    public static class SurqlEmitter
    {
        /// <summary>
        /// Map a numbered <c>NNN_name.mermaid</c> path to its sibling
        /// <c>NNN_name.surql</c> path (Phase 4 task 21 prefix preservation).
        /// </summary>
        public static string MapMermaidPathToSurql(string mermaidPath)
        {
            if (mermaidPath == null) throw new ArgumentNullException(nameof(mermaidPath));
            var dir = Path.GetDirectoryName(mermaidPath) ?? string.Empty;
            var name = Path.GetFileNameWithoutExtension(mermaidPath);
            return string.IsNullOrEmpty(dir)
                ? name + ".surql"
                : Path.Combine(dir, name + ".surql");
        }

        /// <summary>
        /// Emit-time toggles. Currently a single knob: <see cref="Idempotent"/>
        /// inserts <c>IF NOT EXISTS</c> on every <c>DEFINE TABLE/FIELD/INDEX</c>
        /// so the .surql can be applied repeatedly to keep the deployed DB in
        /// sync with the schema (CREATE-or-leave-alone semantics rather than
        /// CREATE-and-fail-if-present).
        /// </summary>
        public readonly struct EmitOptions
        {
            /// <summary>
            /// When true, emit <c>DEFINE TABLE/FIELD/INDEX IF NOT EXISTS</c>.
            /// Default <c>true</c> -- the canonical sync-friendly form. Set
            /// to <c>false</c> for first-deploy strictness (every DEFINE
            /// must hit a fresh namespace).
            /// </summary>
            public bool Idempotent { get; }

            /// <summary>
            /// When true, emit <c>DEFINE TABLE/FIELD/INDEX OVERWRITE</c> — the
            /// evolve-in-place form. Unlike <c>IF NOT EXISTS</c> (which never
            /// touches an existing definition), OVERWRITE REPLACES the current
            /// definition, so a field TYPE / DEFAULT / ASSERT change actually
            /// applies. This is the fix for the "field-type change silently
            /// never applies" bug. Takes precedence over
            /// <see cref="Idempotent"/> when both are set.
            /// </summary>
            public bool Overwrite { get; }

            public EmitOptions(bool idempotent) : this(idempotent, overwrite: false) { }

            public EmitOptions(bool idempotent, bool overwrite)
            {
                Idempotent = idempotent;
                Overwrite = overwrite;
            }

            /// <summary>Default emit shape (idempotent).</summary>
            public static EmitOptions Default => new EmitOptions(idempotent: true, overwrite: false);

            /// <summary>Strict shape -- DEFINE without IF NOT EXISTS.</summary>
            public static EmitOptions Strict => new EmitOptions(idempotent: false, overwrite: false);

            /// <summary>Evolve shape -- DEFINE ... OVERWRITE (replaces existing definitions).</summary>
            public static EmitOptions Evolve => new EmitOptions(idempotent: false, overwrite: true);

            /// <summary>
            /// Append the correct existence clause (<c>OVERWRITE </c>,
            /// <c>IF NOT EXISTS </c>, or nothing) after a <c>DEFINE &lt;kind&gt; </c>
            /// prefix. OVERWRITE wins over Idempotent.
            /// </summary>
            internal void AppendExistenceClause(StringBuilder sb)
            {
                if (Overwrite) sb.Append("OVERWRITE ");
                else if (Idempotent) sb.Append("IF NOT EXISTS ");
            }
        }

        /// <summary>
        /// Emit the model as a Surreal-QL string. Always uses Unix-style
        /// newlines and a single trailing newline for byte-stable output.
        /// </summary>
        public static string Emit(SchemaModel model) => Emit(model, EmitOptions.Default);

        /// <summary>
        /// Emit the model as a Surreal-QL string with explicit emit options.
        /// </summary>
        public static string Emit(SchemaModel model, EmitOptions options)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            var sb = new StringBuilder();

            for (int i = 0; i < model.Entities.Count; i++)
            {
                if (i > 0) sb.Append('\n');
                EmitEntity(sb, model.Entities[i], options);
            }

            // Always exactly one trailing newline.
            if (sb.Length == 0 || sb[sb.Length - 1] != '\n') sb.Append('\n');
            return sb.ToString();
        }

        /// <summary>
        /// Emit a single <c>DEFINE FIELD …</c> statement for one attribute,
        /// honouring <paramref name="options"/> (use <see cref="EmitOptions.Evolve"/>
        /// for the OVERWRITE form). This is the diff-apply seam: given a
        /// type-/constraint-changed field from <see cref="Model.SchemaModel"/>
        /// via a <c>SchemaDiff</c>, emit the exact DDL that evolves it in place.
        /// </summary>
        public static string EmitFieldStatement(string table, SchemaAttribute attr, EmitOptions options)
        {
            if (table == null) throw new ArgumentNullException(nameof(table));
            if (attr == null) throw new ArgumentNullException(nameof(attr));
            var sb = new StringBuilder();
            EmitField(sb, table, attr, options);
            return sb.ToString();
        }

        /// <summary>Emit a single <c>DEFINE INDEX …</c> statement for one index.</summary>
        public static string EmitIndexStatement(string table, SchemaIndex idx, EmitOptions options)
        {
            if (table == null) throw new ArgumentNullException(nameof(table));
            if (idx == null) throw new ArgumentNullException(nameof(idx));
            var sb = new StringBuilder();
            EmitIndex(sb, table, idx, options);
            return sb.ToString();
        }

        private static void EmitEntity(StringBuilder sb, SchemaEntity entity, EmitOptions options)
        {
            EmitEntityHeader(sb, entity);

            // Closed-set enum decls: emit one DEFINE PARAM $<param> VALUE [...]
            // block per [Inside]-marked field BEFORE the DEFINE TABLE so the
            // ASSERT clauses below can reference them. Also surface them as
            // doc comments grouped under the originating C# enum type name.
            EmitEnumBlocks(sb, entity, options);

            // DEFINE TABLE -- two shapes depending on whether the entity is
            // a RELATE edge table:
            //   DEFINE TABLE [IF NOT EXISTS] <name> TYPE RELATION FROM <a> TO <b> [SCHEMAFULL];
            //   DEFINE TABLE [IF NOT EXISTS] <name> [SCHEMAFULL];
            bool isSchemafull = HasDirective(entity.Annotations, "schemafull");
            var relation = FindAnnotation(entity.Annotations, "relation");
            sb.Append("DEFINE TABLE ");
            options.AppendExistenceClause(sb);
            sb.Append(entity.Name);
            if (relation != null)
            {
                relation.Arguments.TryGetValue("from", out var fromTbl);
                relation.Arguments.TryGetValue("to", out var toTbl);
                sb.Append(" TYPE RELATION FROM ").Append(fromTbl).Append(" TO ").Append(toTbl);
            }
            if (isSchemafull) sb.Append(" SCHEMAFULL");

            // CHANGEFEED clause (from @surreal.changefeed): CDC retention window.
            var changefeed = FindAnnotation(entity.Annotations, "changefeed");
            if (changefeed != null
                && changefeed.Arguments.TryGetValue("duration", out var cfDuration)
                && !string.IsNullOrEmpty(cfDuration))
            {
                sb.Append(" CHANGEFEED ").Append(cfDuration);
                if (changefeed.Arguments.TryGetValue("original", out var cfOriginal)
                    && cfOriginal == "true")
                {
                    sb.Append(" INCLUDE ORIGINAL");
                }
            }

            // PERMISSIONS clause (from @surreal.permissions): row-level security.
            var permissions = FindAnnotation(entity.Annotations, "permissions");
            if (permissions != null)
            {
                EmitPermissions(sb, permissions);
            }

            sb.Append(";\n\n");

            // Fields
            string? lastFieldGroup = null;
            for (int i = 0; i < entity.Attributes.Count; i++)
            {
                var attr = entity.Attributes[i];

                // Field-group separator (renders as `-- <text>` comment line).
                var group = FirstArg(attr.Annotations, "fieldgroup");
                if (group != null && group != lastFieldGroup)
                {
                    if (i > 0) sb.Append('\n');
                    sb.Append("-- ").Append(group).Append('\n');
                    lastFieldGroup = group;
                }

                EmitField(sb, entity.Name, attr, options);
            }

            // Indexes
            if (entity.Indexes.Count > 0)
            {
                sb.Append('\n');
                var sectionHeader = FirstArg(entity.Annotations, "section") ?? DefaultIndexSection();
                sb.Append("-- ").Append(sectionHeader).Append('\n');
                sb.Append('\n');
                for (int i = 0; i < entity.Indexes.Count; i++)
                {
                    if (i > 0) sb.Append('\n');
                    EmitIndex(sb, entity.Name, entity.Indexes[i], options);
                }
            }
        }

        /// <summary>
        /// Emit <c>DEFINE PARAM $&lt;name&gt; VALUE [...]</c> blocks for every
        /// closed-set enum field on this entity. Grouped under a "-- Enums:"
        /// header that also renders the originating C# enum type name (if
        /// any) so operators can match the DDL back to the POCO surface.
        /// </summary>
        private static void EmitEnumBlocks(StringBuilder sb, SchemaEntity entity, EmitOptions options)
        {
            var enums = new List<(string Param, string CsEnum, string ValuesEncoded)>();
            foreach (var attr in entity.Attributes)
            {
                foreach (var ann in attr.Annotations)
                {
                    if (ann.Directive != "enum") continue;
                    ann.Arguments.TryGetValue("param", out var paramName);
                    ann.Arguments.TryGetValue("name", out var enumName);
                    ann.Arguments.TryGetValue("values", out var values);
                    if (string.IsNullOrEmpty(paramName) || string.IsNullOrEmpty(values)) continue;
                    enums.Add((paramName!, enumName ?? string.Empty, values!));
                }
            }
            if (enums.Count == 0) return;

            sb.Append("-- ── Enums ─────────────────────────────────────────────────\n");
            sb.Append('\n');
            foreach (var (param, csEnum, valuesEncoded) in enums)
            {
                if (!string.IsNullOrEmpty(csEnum))
                {
                    sb.Append("-- ").Append(csEnum).Append('\n');
                }
                sb.Append("DEFINE PARAM ");
                options.AppendExistenceClause(sb);
                sb.Append('$').Append(param).Append(" VALUE [");
                var first = true;
                foreach (var v in DecodeValues(valuesEncoded))
                {
                    if (!first) sb.Append(", ");
                    first = false;
                    sb.Append('"').Append(EscapeSurqlString(v)).Append('"');
                }
                sb.Append("];\n");
            }
            sb.Append('\n');
        }

        private static IEnumerable<string> DecodeValues(string encoded)
        {
            // Reverse of AttributeSchemaScanner.EncodeEnumValue: split on
            // unescaped commas, then un-escape each segment.
            var sb = new StringBuilder();
            for (int i = 0; i < encoded.Length; i++)
            {
                char c = encoded[i];
                if (c == '\\' && i + 1 < encoded.Length)
                {
                    sb.Append(encoded[i + 1]);
                    i++;
                }
                else if (c == ',')
                {
                    yield return sb.ToString();
                    sb.Clear();
                }
                else sb.Append(c);
            }
            if (sb.Length > 0) yield return sb.ToString();
        }

        private static string EscapeSurqlString(string s)
            => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

        private static string DefaultIndexSection()
            => "── Indexes ──────────────────────────────────────────────────";

        private static void EmitEntityHeader(StringBuilder sb, SchemaEntity entity)
        {
            sb.Append("-- ============================================================\n");
            sb.Append("-- Table: ").Append(entity.Name).Append('\n');

            var aggregate = FirstArg(entity.Annotations, "aggregate");
            if (!string.IsNullOrEmpty(aggregate))
            {
                sb.Append("-- Aggregate: ").Append(aggregate).Append('\n');
            }
            var guardrail = FirstArg(entity.Annotations, "guardrail");
            if (!string.IsNullOrEmpty(guardrail))
            {
                sb.Append("-- Guardrail: ").Append(guardrail).Append('\n');
            }
            foreach (var note in AllArgs(entity.Annotations, "note"))
            {
                // Allow embedded `\n` so a single note directive can carry a
                // multi-line block (each line gets its own `-- ` prefix).
                foreach (var line in note.Split('\n'))
                {
                    sb.Append("-- Note: ").Append(line).Append('\n');
                }
            }
            sb.Append("-- ============================================================\n");
            sb.Append('\n');
        }

        private static void EmitField(StringBuilder sb, string table, SchemaAttribute attr, EmitOptions options)
        {
            // DEFINE FIELD [IF NOT EXISTS] <name> ON TABLE <table> TYPE <token> [FLEXIBLE]
            // Type preserved verbatim (so `option<string>` flows through).
            // FLEXIBLE modifier (driven by @surreal.flexible annotation) sits
            // AFTER the TYPE keyword + token per SurrealDB DDL grammar
            // (verified against a live SurrealDB 1.5+ instance -- earlier
            // versions tolerated FLEXIBLE-before-TYPE; current parsers do not).
            bool flexible = HasDirective(attr.Annotations, "flexible");
            sb.Append("DEFINE FIELD ");
            options.AppendExistenceClause(sb);
            sb.Append(attr.Name)
              .Append(" ON TABLE ").Append(table);
            sb.Append(" TYPE ").Append(attr.Type);
            if (flexible) sb.Append(" FLEXIBLE");

            // ASSERT clause (from @surreal.assert "<expr>").
            var assertExpr = FirstArg(attr.Annotations, "assert");
            if (!string.IsNullOrEmpty(assertExpr))
            {
                sb.Append('\n').Append("    ASSERT ").Append(assertExpr);
            }

            // DEFAULT clause (from @surreal.default "<value>").
            var defaultValue = FirstArg(attr.Annotations, "default");
            if (!string.IsNullOrEmpty(defaultValue))
            {
                sb.Append('\n').Append("    DEFAULT ").Append(defaultValue);
            }

            // VALUE clause (from @surreal.value "<expr>"): server-side computed
            // expression re-evaluated on every write.
            var valueExpr = FirstArg(attr.Annotations, "value");
            if (!string.IsNullOrEmpty(valueExpr))
            {
                sb.Append('\n').Append("    VALUE ").Append(valueExpr);
            }

            // READONLY modifier (from @surreal.readonly): set-once column.
            if (HasDirective(attr.Annotations, "readonly"))
            {
                sb.Append('\n').Append("    READONLY");
            }

            // COMMENT clause (from SchemaAttribute.Comment): quoted free text.
            if (!string.IsNullOrEmpty(attr.Comment))
            {
                sb.Append('\n').Append("    COMMENT \"").Append(EscapeSurqlString(attr.Comment!)).Append('"');
            }

            sb.Append(";\n");

            // FLEXIBLE on an `array<object>` only relaxes the array container, NOT
            // the embedded objects' fields: SurrealDB 3.x SCHEMAFULL still rejects
            // any undefined nested field (`Found field 'edges[0].edge_type', but no
            // such field exists`). Defining `array<object>` auto-creates a strict
            // `<name>.*` object slot, so OVERWRITE it with a FLEXIBLE object to let
            // arbitrary nested fields through (their shape is validated by the C#
            // POCO at deserialization time, which is the whole point of FLEXIBLE).
            if (flexible && IsArrayOfObject(attr.Type))
            {
                // OVERWRITE (not IF NOT EXISTS): the parent `array<object>` field
                // auto-creates a strict `<name>.*` slot we must replace. OVERWRITE
                // is also idempotent (re-running the DDL re-applies cleanly).
                sb.Append("DEFINE FIELD OVERWRITE ")
                  .Append(attr.Name).Append(".*")
                  .Append(" ON TABLE ").Append(table)
                  .Append(" TYPE object FLEXIBLE;\n");
            }
        }

        /// <summary>True for a SurrealDB <c>array&lt;object&gt;</c> column type
        /// (optionally bounded, e.g. <c>array&lt;object, 8&gt;</c>) and its
        /// <c>option&lt;…&gt;</c> wrapping.</summary>
        private static bool IsArrayOfObject(string? type)
        {
            if (string.IsNullOrEmpty(type)) return false;
            var t = type!.Replace(" ", "");
            return t.IndexOf("array<object", StringComparison.Ordinal) >= 0;
        }

        /// <summary>
        /// Emit the table <c>PERMISSIONS</c> clause. <c>full=true</c> renders the
        /// table-wide <c>PERMISSIONS FULL</c> shorthand; otherwise each present
        /// operation renders <c>FOR &lt;op&gt; &lt;expr&gt;</c>. The <c>FULL</c>
        /// and <c>NONE</c> expression tokens pass through unquoted; every other
        /// value is a raw SurrealQL boolean expression also emitted verbatim.
        /// </summary>
        private static void EmitPermissions(StringBuilder sb, SchemaAnnotation permissions)
        {
            if (permissions.Arguments.TryGetValue("full", out var full) && full == "true")
            {
                sb.Append(" PERMISSIONS FULL");
                return;
            }

            // Stable operation order regardless of dictionary insertion.
            var ops = new[] { "select", "create", "update", "delete" };
            var clauses = new List<string>();
            foreach (var op in ops)
            {
                if (permissions.Arguments.TryGetValue(op, out var expr) && !string.IsNullOrEmpty(expr))
                {
                    clauses.Add("FOR " + op + " " + expr);
                }
            }
            if (clauses.Count == 0) return;
            sb.Append(" PERMISSIONS ").Append(string.Join(" ", clauses));
        }

        private static void EmitIndex(StringBuilder sb, string table, SchemaIndex idx, EmitOptions options)
        {
            // DEFINE INDEX [IF NOT EXISTS] <name>
            //     ON TABLE <table>
            //     FIELDS <f1>, <f2>
            //     [UNIQUE];
            sb.Append("DEFINE INDEX ");
            options.AppendExistenceClause(sb);
            sb.Append(idx.Name).Append('\n');
            sb.Append("    ON TABLE ").Append(table).Append('\n');
            sb.Append("    FIELDS ").Append(string.Join(", ", idx.Fields));
            if (idx.IsUnique) sb.Append('\n').Append("    UNIQUE");
            sb.Append(";\n");
        }

        // ── helpers ───────────────────────────────────────────────────────
        private static SchemaAnnotation? FindAnnotation(IReadOnlyList<SchemaAnnotation> anns, string directive)
        {
            foreach (var a in anns) if (a.Directive == directive) return a;
            return null;
        }

        private static bool HasDirective(IReadOnlyList<SchemaAnnotation> anns, string directive)
        {
            foreach (var a in anns) if (a.Directive == directive) return true;
            return false;
        }

        /// <summary>
        /// Returns the first positional / quoted argument of the first
        /// annotation matching <paramref name="directive"/>, or null if not present.
        /// Bare-token directives (e.g. <c>@surreal.aggregate Wallet</c>) return the
        /// raw arguments string; quoted directives return the unescaped value.
        /// </summary>
        private static string? FirstArg(IReadOnlyList<SchemaAnnotation> anns, string directive)
        {
            foreach (var a in anns)
            {
                if (a.Directive == directive) return ExtractPrimaryValue(a);
            }
            return null;
        }

        private static IEnumerable<string> AllArgs(IReadOnlyList<SchemaAnnotation> anns, string directive)
        {
            foreach (var a in anns)
            {
                if (a.Directive == directive)
                {
                    var v = ExtractPrimaryValue(a);
                    if (v != null) yield return v;
                }
            }
        }

        private static string? ExtractPrimaryValue(SchemaAnnotation a)
        {
            // If the arguments dict has a single key with an empty value, that
            // key is the positional bare-token value (e.g. `@surreal.schemafull`).
            // Otherwise pick the first key=value pair value.
            // For @surreal.<dir> "literal text", we wrap the literal in the raw
            // args; reinterpret it here.
            var raw = a.RawArguments?.Trim() ?? string.Empty;
            if (raw.Length == 0) return string.Empty;
            // Quoted-string special case: if the whole raw payload is a single
            // quoted string, return its unescaped body.
            if (raw[0] == '"')
            {
                var (decoded, consumed) = TryDecodeQuoted(raw);
                if (consumed == raw.Length) return decoded;
            }
            return raw;
        }

        private static (string decoded, int consumed) TryDecodeQuoted(string raw)
        {
            if (raw.Length == 0 || raw[0] != '"') return (raw, 0);
            var sb = new StringBuilder();
            for (int i = 1; i < raw.Length; i++)
            {
                char c = raw[i];
                if (c == '\\' && i + 1 < raw.Length)
                {
                    char nx = raw[i + 1];
                    switch (nx)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case 'n': sb.Append('\n'); break;
                        case 't': sb.Append('\t'); break;
                        default: sb.Append(nx); break;
                    }
                    i++; // skip the escape char
                }
                else if (c == '"')
                {
                    return (sb.ToString(), i + 1);
                }
                else sb.Append(c);
            }
            return (sb.ToString(), raw.Length);
        }
    }
}
