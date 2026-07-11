// SPDX-License-Identifier: UNLICENSED
// SurrealForge.Schema -- live-schema introspection.
//
// Reads the ACTUAL deployed schema from a connected SurrealDB via
// `INFO FOR DB` (table list) + `INFO FOR TABLE <t>` (field / index list) and
// parses the returned `DEFINE ...` statement strings back into the same
// SchemaModel shape AttributeSchemaScanner produces from C# POCOs. That common
// shape is what SchemaDiff compares against the desired model. See
// src/SurrealForge.Schema/Migration/AGENTS.md for the design rationale.

#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SurrealForge.Schema.Model;

namespace SurrealForge.Schema.Migration
{
    /// <summary>
    /// Reads the live SurrealDB schema and projects it onto a
    /// <see cref="SchemaModel"/> for diffing against the desired (C#-derived)
    /// model. Pure parsing once the two INFO responses are fetched — no clock
    /// or environment reads, so its output is a deterministic function of the
    /// server's reply.
    /// </summary>
    public sealed class LiveSchemaIntrospector
    {
        private readonly ISurrealConnection _connection;

        public LiveSchemaIntrospector(ISurrealConnection connection)
        {
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        }

        /// <summary>
        /// Introspect every table in the current database. Issues one
        /// <c>INFO FOR DB</c> then one <c>INFO FOR TABLE</c> per discovered
        /// table. The <see cref="MigrationRunner.TrackingTable"/> is excluded —
        /// it is runner-internal plumbing, never part of the user's model.
        /// </summary>
        public async Task<SchemaModel> IntrospectAsync(CancellationToken ct = default)
        {
            var dbResult = await _connection.ExecuteAsync("INFO FOR DB;", ct).ConfigureAwait(false);
            if (!dbResult.IsOk)
                throw new MigrationApplyException("INFO FOR DB", dbResult.Detail ?? "could not read database info");

            var tableNames = ParseTableNames(dbResult.RawBody);
            var entities = new List<SchemaEntity>();
            foreach (var table in tableNames)
            {
                if (table == MigrationRunner.TrackingTable) continue;
                var entity = await IntrospectTableAsync(table, ct).ConfigureAwait(false);
                entities.Add(entity);
            }
            entities.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            return new SchemaModel("(live-introspection)", entities, Array.Empty<SchemaRelationship>());
        }

        /// <summary>Introspect a single named table into a <see cref="SchemaEntity"/>.</summary>
        public async Task<SchemaEntity> IntrospectTableAsync(string table, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(table)) throw new ArgumentException("table is required", nameof(table));
            var result = await _connection.ExecuteAsync("INFO FOR TABLE " + table + ";", ct).ConfigureAwait(false);
            if (!result.IsOk)
                throw new MigrationApplyException("INFO FOR TABLE " + table, result.Detail ?? "could not read table info");
            return ParseTableInfo(table, result.RawBody);
        }

        // ── DB info parse ──────────────────────────────────────────────────

        /// <summary>
        /// Extract table names from an <c>INFO FOR DB</c> reply. The result
        /// object carries a <c>tables</c> map keyed by table name (values are
        /// the table's <c>DEFINE TABLE …</c> string). Tolerant to the
        /// statement-wrapper shape (<c>[{ status, result }]</c>) and the bare
        /// result-object shape.
        /// </summary>
        internal static IReadOnlyList<string> ParseTableNames(string? rawBody)
        {
            var names = new List<string>();
            if (string.IsNullOrWhiteSpace(rawBody)) return names;
            JsonElement result;
            try
            {
                using var doc = JsonDocument.Parse(rawBody!);
                if (!TryUnwrapResult(doc.RootElement, out result)) return names;
                if (result.ValueKind == JsonValueKind.Object &&
                    result.TryGetProperty("tables", out var tables) &&
                    tables.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in tables.EnumerateObject()) names.Add(prop.Name);
                }
            }
            catch (JsonException)
            {
                return names;
            }
            names.Sort(StringComparer.Ordinal);
            return names;
        }

        // ── TABLE info parse ───────────────────────────────────────────────

        /// <summary>
        /// Parse an <c>INFO FOR TABLE</c> reply into a <see cref="SchemaEntity"/>.
        /// The result object carries <c>fields</c> and <c>indexes</c> maps whose
        /// values are the full <c>DEFINE FIELD …</c> / <c>DEFINE INDEX …</c>
        /// statement strings, which we parse for the diff-relevant tokens
        /// (type, default, assert, readonly, unique, index fields). Nested
        /// object slots (e.g. <c>edges[*]</c>) are dropped — the diff compares
        /// top-level fields only, matching what the scanner emits as the
        /// primary field set.
        /// </summary>
        internal static SchemaEntity ParseTableInfo(string table, string? rawBody)
        {
            var attributes = new List<SchemaAttribute>();
            var indexes = new List<SchemaIndex>();
            if (string.IsNullOrWhiteSpace(rawBody))
                return new SchemaEntity(table, attributes, Array.Empty<SchemaAnnotation>(), indexes, 0);

            JsonElement result;
            try
            {
                using var doc = JsonDocument.Parse(rawBody!);
                if (!TryUnwrapResult(doc.RootElement, out result))
                    return new SchemaEntity(table, attributes, Array.Empty<SchemaAnnotation>(), indexes, 0);
                result = result.Clone();
            }
            catch (JsonException)
            {
                return new SchemaEntity(table, attributes, Array.Empty<SchemaAnnotation>(), indexes, 0);
            }

            if (result.ValueKind != JsonValueKind.Object)
                return new SchemaEntity(table, attributes, Array.Empty<SchemaAnnotation>(), indexes, 0);

            if (result.TryGetProperty("fields", out var fields) && fields.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in fields.EnumerateObject())
                {
                    // Skip nested field slots (`foo[*]`, `foo.bar`): the diff
                    // operates on the top-level field set the scanner emits.
                    if (prop.Name.IndexOf('[') >= 0 || prop.Name.IndexOf('.') >= 0 || prop.Name.IndexOf('*') >= 0)
                        continue;
                    var ddl = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() : null;
                    var attr = ParseFieldDefinition(prop.Name, ddl);
                    if (attr != null) attributes.Add(attr);
                }
            }

            if (result.TryGetProperty("indexes", out var idxObj) && idxObj.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in idxObj.EnumerateObject())
                {
                    var ddl = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() : null;
                    var idx = ParseIndexDefinition(prop.Name, ddl);
                    if (idx != null) indexes.Add(idx);
                }
            }

            attributes.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            indexes.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            return new SchemaEntity(table, attributes, Array.Empty<SchemaAnnotation>(), indexes, 0);
        }

        /// <summary>
        /// Parse a single <c>DEFINE FIELD &lt;name&gt; ON &lt;table&gt; TYPE
        /// &lt;type&gt; [DEFAULT …] [ASSERT …] [READONLY] …</c> string into a
        /// <see cref="SchemaAttribute"/> carrying the tokens the diff cares
        /// about (type, default, assert, readonly). The type token can itself
        /// contain balanced <c>&lt; &gt;</c> (e.g. <c>option&lt;array&lt;string&gt;&gt;</c>),
        /// so it is captured up to the next top-level clause keyword rather
        /// than the next whitespace.
        /// </summary>
        internal static SchemaAttribute? ParseFieldDefinition(string name, string? ddl)
        {
            if (string.IsNullOrWhiteSpace(ddl)) return null;
            var annotations = new List<SchemaAnnotation>();

            var type = ExtractClauseValue(ddl!, "TYPE") ?? "any";
            // SurrealDB normalises FLEXIBLE into the type prefix on read-back
            // ("FLEXIBLE TYPE object"); strip it so the type token matches the
            // scanner's, and record flexibility as an annotation.
            bool flexible = false;
            var flexIdx = type.IndexOf("FLEXIBLE", StringComparison.OrdinalIgnoreCase);
            if (flexIdx >= 0)
            {
                flexible = true;
                type = type.Replace("FLEXIBLE", string.Empty).Trim();
            }
            // Detect FLEXIBLE appearing before TYPE on the raw DDL too.
            if (!flexible && ddl!.IndexOf("FLEXIBLE", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                flexible = true;
            }
            if (flexible) annotations.Add(BareAnnotation("flexible"));

            var def = ExtractClauseValue(ddl!, "DEFAULT");
            if (!string.IsNullOrEmpty(def)) annotations.Add(QuotedAnnotation("default", def!));

            var assert = ExtractClauseValue(ddl!, "ASSERT");
            if (!string.IsNullOrEmpty(assert)) annotations.Add(QuotedAnnotation("assert", assert!));

            var value = ExtractClauseValue(ddl!, "VALUE");
            if (!string.IsNullOrEmpty(value)) annotations.Add(QuotedAnnotation("value", value!));

            if (HasBareKeyword(ddl!, "READONLY")) annotations.Add(BareAnnotation("readonly"));

            return new SchemaAttribute(name, NormalizeType(type), isKey: false, comment: null, annotations: annotations, sourceLine: 0);
        }

        /// <summary>
        /// Parse a <c>DEFINE INDEX &lt;name&gt; ON &lt;table&gt; FIELDS f1, f2
        /// [UNIQUE]</c> string into a <see cref="SchemaIndex"/>.
        /// </summary>
        internal static SchemaIndex? ParseIndexDefinition(string name, string? ddl)
        {
            if (string.IsNullOrWhiteSpace(ddl)) return null;
            // FIELDS clause value runs up to UNIQUE/SEARCH/COMMENT/end.
            var fieldsRaw = ExtractClauseValue(ddl!, "FIELDS") ?? ExtractClauseValue(ddl!, "COLUMNS");
            var fields = new List<string>();
            if (!string.IsNullOrEmpty(fieldsRaw))
            {
                foreach (var f in fieldsRaw!.Split(','))
                {
                    var t = f.Trim();
                    if (t.Length > 0) fields.Add(t);
                }
            }
            bool unique = HasBareKeyword(ddl!, "UNIQUE");
            return new SchemaIndex(name, fields, unique, 0);
        }

        // ── DDL token extraction ───────────────────────────────────────────

        // Clause keywords that terminate a TYPE / DEFAULT / ASSERT / VALUE /
        // FIELDS value token. Order-independent; matched at a top-level
        // (bracket-depth 0) word boundary.
        private static readonly string[] ClauseKeywords =
        {
            "TYPE", "DEFAULT", "ASSERT", "VALUE", "READONLY", "FLEXIBLE",
            "PERMISSIONS", "COMMENT", "REFERENCE", "ON", "FIELDS", "COLUMNS",
            "UNIQUE", "SEARCH", "MTREE", "HNSW", "ANALYZER", "CONCURRENTLY",
        };

        /// <summary>
        /// Extract the text following a clause keyword up to the next
        /// top-level clause keyword (or end of string). Respects bracket depth
        /// (<c>&lt; &gt;</c>, <c>( )</c>, <c>[ ]</c>) and single/double-quoted
        /// strings so a keyword appearing inside a type argument or an ASSERT
        /// expression does not prematurely terminate the value. Returns null
        /// when the keyword is absent.
        /// </summary>
        internal static string? ExtractClauseValue(string ddl, string keyword)
        {
            int start = FindKeyword(ddl, keyword, 0);
            if (start < 0) return null;
            int i = start + keyword.Length;
            // Skip separating whitespace.
            while (i < ddl.Length && char.IsWhiteSpace(ddl[i])) i++;
            int valueStart = i;

            int depth = 0;
            char quote = '\0';
            for (; i < ddl.Length; i++)
            {
                char c = ddl[i];
                if (quote != '\0')
                {
                    if (c == '\\') { i++; continue; }
                    if (c == quote) quote = '\0';
                    continue;
                }
                if (c == '"' || c == '\'') { quote = c; continue; }
                if (c == '<' || c == '(' || c == '[') { depth++; continue; }
                if (c == '>' || c == ')' || c == ']') { if (depth > 0) depth--; continue; }
                if (depth == 0 && (char.IsWhiteSpace(c)))
                {
                    // Peek the next word: if it is a clause keyword, stop.
                    int wordStart = i + 1;
                    while (wordStart < ddl.Length && char.IsWhiteSpace(ddl[wordStart])) wordStart++;
                    int wordEnd = wordStart;
                    while (wordEnd < ddl.Length && (char.IsLetter(ddl[wordEnd]))) wordEnd++;
                    if (wordEnd > wordStart)
                    {
                        var word = ddl.Substring(wordStart, wordEnd - wordStart);
                        if (IsClauseKeyword(word)) break;
                    }
                }
            }
            var value = ddl.Substring(valueStart, i - valueStart).Trim();
            // Strip a single trailing statement terminator.
            value = value.TrimEnd(';').Trim();
            return value.Length == 0 ? null : value;
        }

        /// <summary>Find a top-level (depth-0, word-boundary) keyword occurrence.</summary>
        private static int FindKeyword(string ddl, string keyword, int from)
        {
            int depth = 0;
            char quote = '\0';
            for (int i = from; i <= ddl.Length - keyword.Length; i++)
            {
                char c = ddl[i];
                if (quote != '\0')
                {
                    if (c == '\\') { i++; continue; }
                    if (c == quote) quote = '\0';
                    continue;
                }
                if (c == '"' || c == '\'') { quote = c; continue; }
                if (c == '<' || c == '(' || c == '[') { depth++; continue; }
                if (c == '>' || c == ')' || c == ']') { if (depth > 0) depth--; continue; }
                if (depth != 0) continue;
                if (!MatchesWordAt(ddl, i, keyword)) continue;
                return i;
            }
            return -1;
        }

        private static bool MatchesWordAt(string ddl, int i, string keyword)
        {
            if (string.Compare(ddl, i, keyword, 0, keyword.Length, StringComparison.OrdinalIgnoreCase) != 0)
                return false;
            // Word boundaries: previous + next char must not be identifier chars.
            if (i > 0 && (char.IsLetterOrDigit(ddl[i - 1]) || ddl[i - 1] == '_')) return false;
            int after = i + keyword.Length;
            if (after < ddl.Length && (char.IsLetterOrDigit(ddl[after]) || ddl[after] == '_')) return false;
            return true;
        }

        private static bool HasBareKeyword(string ddl, string keyword) => FindKeyword(ddl, keyword, 0) >= 0;

        private static bool IsClauseKeyword(string word)
        {
            foreach (var k in ClauseKeywords)
                if (string.Equals(k, word, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// Normalise a type token to the canonical form the scanner emits so
        /// desired (POCO) and actual (introspected) tokens compare equal:
        /// <list type="bullet">
        ///   <item>collapse internal whitespace + lower-case;</item>
        ///   <item>rewrite SurrealDB 3.x's read-back union rendering of an
        ///     optional type — <c>none | string</c> / <c>string | none</c> —
        ///     back into the <c>option&lt;string&gt;</c> shape the scanner
        ///     emits (a single-branch <c>none |</c> union IS an option).</item>
        /// </list>
        /// Only the simple two-branch <c>none | X</c> optional is canonicalised;
        /// genuine multi-branch unions pass through as-is (and would legitimately
        /// diff against a scalar POCO type).
        /// </summary>
        internal static string NormalizeType(string type)
        {
            if (string.IsNullOrEmpty(type)) return type;

            // Rewrite `none | X` / `X | none` -> `option<X>` on the RAW token
            // (before whitespace strip) so the split on '|' is unambiguous.
            var canon = CanonicalizeOptionalUnion(type);

            var sb = new StringBuilder(canon.Length);
            foreach (var c in canon)
            {
                if (char.IsWhiteSpace(c)) continue;
                sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        private static string CanonicalizeOptionalUnion(string type)
        {
            // Only rewrite a TOP-LEVEL two-branch union with a `none` branch.
            // Respect bracket depth so `array<int | none>` inner unions aren't
            // mis-split (rare, but keeps the rewrite sound).
            var branches = SplitTopLevelUnion(type);
            if (branches.Count != 2) return type;
            string a = branches[0].Trim(), b = branches[1].Trim();
            bool aNone = string.Equals(a, "none", StringComparison.OrdinalIgnoreCase);
            bool bNone = string.Equals(b, "none", StringComparison.OrdinalIgnoreCase);
            if (aNone && !bNone) return "option<" + b + ">";
            if (bNone && !aNone) return "option<" + a + ">";
            return type;
        }

        private static List<string> SplitTopLevelUnion(string type)
        {
            var parts = new List<string>();
            var sb = new StringBuilder();
            int depth = 0;
            foreach (var c in type)
            {
                if (c == '<' || c == '(' || c == '[') { depth++; sb.Append(c); continue; }
                if (c == '>' || c == ')' || c == ']') { if (depth > 0) depth--; sb.Append(c); continue; }
                if (c == '|' && depth == 0) { parts.Add(sb.ToString()); sb.Clear(); continue; }
                sb.Append(c);
            }
            parts.Add(sb.ToString());
            return parts;
        }

        // ── shared JSON unwrap ─────────────────────────────────────────────

        /// <summary>
        /// Unwrap the SurrealDB response envelope to the inner result payload.
        /// Handles <c>[{ status, result }]</c>, <c>[ result ]</c>, and a bare
        /// result object.
        /// </summary>
        internal static bool TryUnwrapResult(JsonElement root, out JsonElement result)
        {
            result = root;
            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in root.EnumerateArray())
                {
                    // First statement wins (INFO is a single statement).
                    if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty("result", out var inner))
                    {
                        result = inner;
                        return true;
                    }
                    result = el;
                    return true;
                }
                return false;
            }
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("result", out var r))
            {
                result = r;
                return true;
            }
            return root.ValueKind == JsonValueKind.Object;
        }

        private static SchemaAnnotation BareAnnotation(string directive)
            => new SchemaAnnotation(directive, string.Empty, EmptyArgs, 0, 0);

        private static SchemaAnnotation QuotedAnnotation(string directive, string value)
            => new SchemaAnnotation(directive, "\"" + Escape(value) + "\"", EmptyArgs, 0, 0);

        private static string Escape(string s)
        {
            var sb = new StringBuilder(s.Length + 4);
            foreach (var ch in s)
            {
                switch (ch)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\t': sb.Append("\\t"); break;
                    default: sb.Append(ch); break;
                }
            }
            return sb.ToString();
        }

        private static readonly IReadOnlyDictionary<string, string> EmptyArgs = new Dictionary<string, string>();
    }
}
