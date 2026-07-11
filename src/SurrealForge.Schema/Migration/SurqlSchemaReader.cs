// SPDX-License-Identifier: UNLICENSED
// SurrealForge.Schema -- parse emitted .surql schema files back into a
// SchemaModel (the DESIRED model source for reconcile when no assembly is
// available, e.g. inside the deploy container which ships only the generated
// .surql files + the CLI dll).
//
// The .surql the SurqlEmitter writes has a known, deterministic shape:
//   DEFINE TABLE [IF NOT EXISTS] <t> [SCHEMAFULL] …;
//   DEFINE FIELD [IF NOT EXISTS] <f> ON TABLE <t> TYPE <type> [DEFAULT …] …;
//   DEFINE INDEX [IF NOT EXISTS] <i> ON TABLE <t> FIELDS … [UNIQUE];
// We parse those statements with the same DDL-token extractor the live
// introspector uses, so the desired model and the actual model are produced by
// identical parsing rules — the diff then compares apples to apples.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SurrealForge.Schema.Model;

namespace SurrealForge.Schema.Migration
{
    /// <summary>
    /// Reads a directory of emitted <c>.surql</c> schema files into a
    /// <see cref="SchemaModel"/> for use as the reconcile DESIRED model.
    /// </summary>
    public static class SurqlSchemaReader
    {
        /// <summary>Read every <c>.surql</c> under <paramref name="directory"/> into one model.</summary>
        public static SchemaModel ReadDirectory(string directory)
        {
            if (directory == null) throw new ArgumentNullException(nameof(directory));
            if (!Directory.Exists(directory))
                throw new DirectoryNotFoundException("schema directory not found: " + directory);
            var sb = new StringBuilder();
            foreach (var path in SortedSurqlFiles(directory))
            {
                sb.Append(File.ReadAllText(path)).Append('\n');
            }
            return Parse(sb.ToString());
        }

        private static IEnumerable<string> SortedSurqlFiles(string directory)
        {
            var files = new List<string>(Directory.EnumerateFiles(directory, "*.surql", SearchOption.TopDirectoryOnly));
            files.Sort(StringComparer.Ordinal);
            return files;
        }

        /// <summary>
        /// Parse a blob of <c>.surql</c> DDL into a <see cref="SchemaModel"/>.
        /// Comments (<c>-- …</c>) and unrecognised statements (DEFINE PARAM,
        /// DEFINE NAMESPACE, nested <c>.*</c> field slots) are ignored — the
        /// model carries only what the diff compares (tables, top-level fields,
        /// indexes).
        /// </summary>
        public static SchemaModel Parse(string surql)
        {
            if (surql == null) throw new ArgumentNullException(nameof(surql));

            var fieldsByTable = new Dictionary<string, List<SchemaAttribute>>(StringComparer.Ordinal);
            var indexesByTable = new Dictionary<string, List<SchemaIndex>>(StringComparer.Ordinal);
            var tableOrder = new List<string>();

            foreach (var statement in SplitStatements(surql))
            {
                var s = statement.Trim();
                if (s.Length == 0) continue;

                if (StartsWithKeyword(s, "DEFINE TABLE"))
                {
                    var name = ParseDefineTableName(s);
                    if (name != null) EnsureTable(name, tableOrder, fieldsByTable, indexesByTable);
                }
                else if (StartsWithKeyword(s, "DEFINE FIELD"))
                {
                    ParseDefineField(s, tableOrder, fieldsByTable, indexesByTable);
                }
                else if (StartsWithKeyword(s, "DEFINE INDEX"))
                {
                    ParseDefineIndex(s, tableOrder, fieldsByTable, indexesByTable);
                }
            }

            var entities = new List<SchemaEntity>();
            foreach (var table in tableOrder)
            {
                var attrs = fieldsByTable[table];
                attrs.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
                var idxs = indexesByTable[table];
                idxs.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
                entities.Add(new SchemaEntity(table, attrs, Array.Empty<SchemaAnnotation>(), idxs, 0));
            }
            entities.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            return new SchemaModel("(surql-files)", entities, Array.Empty<SchemaRelationship>());
        }

        // ── statement parsing ──────────────────────────────────────────────

        private static string? ParseDefineTableName(string stmt)
        {
            // DEFINE TABLE [IF NOT EXISTS|OVERWRITE] <name> [TYPE …|SCHEMAFULL|…]
            var tokens = HeadTokens(stmt, skip: 2); // skip "DEFINE" "TABLE"
            int i = SkipExistenceClause(tokens, 0);
            return i < tokens.Count ? tokens[i] : null;
        }

        private static void ParseDefineField(
            string stmt,
            List<string> tableOrder,
            Dictionary<string, List<SchemaAttribute>> fields,
            Dictionary<string, List<SchemaIndex>> indexes)
        {
            // DEFINE FIELD [IF NOT EXISTS|OVERWRITE] <name> ON TABLE <t> TYPE …
            var tokens = HeadTokens(stmt, skip: 2);
            int i = SkipExistenceClause(tokens, 0);
            if (i >= tokens.Count) return;
            var fieldName = tokens[i];
            // Skip nested object slots (foo.* / foo[*]) — diff is top-level only.
            if (fieldName.IndexOf('.') >= 0 || fieldName.IndexOf('*') >= 0 || fieldName.IndexOf('[') >= 0) return;

            var table = ExtractOnTable(stmt);
            if (table == null) return;
            EnsureTable(table, tableOrder, fields, indexes);

            var attr = LiveSchemaIntrospector.ParseFieldDefinition(fieldName, stmt);
            if (attr != null) fields[table].Add(attr);
        }

        private static void ParseDefineIndex(
            string stmt,
            List<string> tableOrder,
            Dictionary<string, List<SchemaAttribute>> fields,
            Dictionary<string, List<SchemaIndex>> indexes)
        {
            // DEFINE INDEX [IF NOT EXISTS|OVERWRITE] <name> ON TABLE <t> FIELDS …
            var tokens = HeadTokens(stmt, skip: 2);
            int i = SkipExistenceClause(tokens, 0);
            if (i >= tokens.Count) return;
            var indexName = tokens[i];
            var table = ExtractOnTable(stmt);
            if (table == null) return;
            EnsureTable(table, tableOrder, fields, indexes);
            var idx = LiveSchemaIntrospector.ParseIndexDefinition(indexName, stmt);
            if (idx != null) indexes[table].Add(idx);
        }

        // ── token helpers ──────────────────────────────────────────────────

        /// <summary>Extract the table after <c>ON TABLE</c> (or <c>ON</c>).</summary>
        private static string? ExtractOnTable(string stmt)
        {
            var onTable = LiveSchemaIntrospector.ExtractClauseValue(stmt, "ON");
            if (string.IsNullOrEmpty(onTable)) return null;
            // Value may be "TABLE <name>" or "<name>"; take the last bare token.
            var parts = onTable!.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return null;
            var last = parts[parts.Length - 1];
            return last.Length == 0 ? null : last;
        }

        private static int SkipExistenceClause(List<string> tokens, int i)
        {
            if (i < tokens.Count && tokens[i].Equals("OVERWRITE", StringComparison.OrdinalIgnoreCase))
                return i + 1;
            if (i + 2 < tokens.Count
                && tokens[i].Equals("IF", StringComparison.OrdinalIgnoreCase)
                && tokens[i + 1].Equals("NOT", StringComparison.OrdinalIgnoreCase)
                && tokens[i + 2].Equals("EXISTS", StringComparison.OrdinalIgnoreCase))
                return i + 3;
            return i;
        }

        /// <summary>Split the statement's leading identifier tokens, skipping <paramref name="skip"/> keywords.</summary>
        private static List<string> HeadTokens(string stmt, int skip)
        {
            var all = stmt.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            var tokens = new List<string>();
            for (int i = skip; i < all.Length; i++) tokens.Add(all[i]);
            return tokens;
        }

        private static bool StartsWithKeyword(string s, string keyword)
        {
            if (s.Length < keyword.Length) return false;
            if (string.Compare(s, 0, keyword, 0, keyword.Length, StringComparison.OrdinalIgnoreCase) != 0) return false;
            int after = keyword.Length;
            return after >= s.Length || char.IsWhiteSpace(s[after]);
        }

        private static void EnsureTable(
            string table,
            List<string> order,
            Dictionary<string, List<SchemaAttribute>> fields,
            Dictionary<string, List<SchemaIndex>> indexes)
        {
            if (fields.ContainsKey(table)) return;
            order.Add(table);
            fields[table] = new List<SchemaAttribute>();
            indexes[table] = new List<SchemaIndex>();
        }

        /// <summary>
        /// Split a .surql blob into statements on top-level semicolons,
        /// stripping <c>-- …</c> line comments and respecting quoted strings
        /// and bracket depth so a <c>;</c> inside an ASSERT string / type arg
        /// does not split mid-statement.
        /// </summary>
        internal static IEnumerable<string> SplitStatements(string surql)
        {
            var sb = new StringBuilder();
            char quote = '\0';
            int depth = 0;
            for (int i = 0; i < surql.Length; i++)
            {
                char c = surql[i];

                // Line comment (only when not inside a string).
                if (quote == '\0' && c == '-' && i + 1 < surql.Length && surql[i + 1] == '-')
                {
                    while (i < surql.Length && surql[i] != '\n') i++;
                    continue;
                }

                if (quote != '\0')
                {
                    sb.Append(c);
                    if (c == '\\' && i + 1 < surql.Length) { sb.Append(surql[++i]); continue; }
                    if (c == quote) quote = '\0';
                    continue;
                }
                if (c == '"' || c == '\'') { quote = c; sb.Append(c); continue; }
                if (c == '<' || c == '(' || c == '[') { depth++; sb.Append(c); continue; }
                if (c == '>' || c == ')' || c == ']') { if (depth > 0) depth--; sb.Append(c); continue; }
                if (c == ';' && depth == 0)
                {
                    var stmt = sb.ToString().Trim();
                    if (stmt.Length > 0) yield return stmt;
                    sb.Clear();
                    continue;
                }
                sb.Append(c);
            }
            var tail = sb.ToString().Trim();
            if (tail.Length > 0) yield return tail;
        }
    }
}
