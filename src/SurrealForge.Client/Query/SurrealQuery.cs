using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SurrealForge.Client.Query
{
    /// <summary>
    /// Immutable, parameterized SurrealQL query.
    ///
    /// All call sites MUST compose queries through this type — never via
    /// string interpolation or concatenation.  Every <c>$param</c> token in
    /// <see cref="Sql"/> must have a corresponding entry in
    /// <see cref="Params"/>; extra params are tolerated in lenient mode but
    /// strict mode (the default) rejects them too.
    ///
    /// The only safe way to inject table or record-id names that cannot be
    /// parameterized is through <see cref="SurrealIdentifier"/> — see that class.
    ///
    /// SurrealQuery is **strictly single-statement**: a literal <c>;</c> in
    /// the SQL body is rejected up-front.  Multiple statements must be
    /// composed through <see cref="Combine"/>; the executor returns a
    /// per-statement <see cref="SurrealResponse"/> so no statement's result
    /// can silently swallow another's.  Closes code-review C5 design root.
    /// </summary>
    public sealed class SurrealQuery
    {
        // Matches SurrealQL named params: $identifier
        private static readonly Regex ParamTokenRegex =
            new Regex(@"\$([a-zA-Z_][a-zA-Z0-9_]*)", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

        // Matches the assignment target of a `LET $var = ...` statement. Such
        // variables are DEFINED inside the SQL body itself (not supplied via the
        // param bag), so they must be excluded from the "missing parameter"
        // check — e.g. a BEGIN; LET $x = ...; RELATE $x->edge->$y; COMMIT block.
        private static readonly Regex LetTargetRegex =
            new Regex(@"(?:^|;)\s*LET\s+\$([a-zA-Z_][a-zA-Z0-9_]*)\s*=",
                RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));

        /// <summary>The SurrealQL statement.  Must not be interpolated at call sites.</summary>
        public string Sql { get; }

        /// <summary>Named parameters bound to this query.  Keys must NOT
        /// include the leading <c>$</c>.</summary>
        public IReadOnlyDictionary<string, object?> Params { get; }

        /// <summary>True when this query was produced by <see cref="Combine"/>
        /// and therefore consists of two or more statements joined by
        /// <c>;</c>. Multi-statement queries skip the single-statement
        /// semicolon guard.</summary>
        public bool IsMultiStatement { get; }

        // MEDIUM #M3: clause-presence is tracked structurally via dedicated
        // flags that are set by the builder methods (or by a literal-stripped
        // scan when an opaque body is supplied via Of). The old approach used
        // \bWHERE\b regex against the entire SQL body, which produced
        // false-positives whenever the keyword appeared inside a string
        // literal (e.g. `CREATE wallet CONTENT { note: "check WHERE field" }`).
        private readonly bool _hasWhere;
        private readonly bool _hasOrderBy;
        private readonly bool _hasLimit;
        private readonly bool _hasStart;
        private readonly bool _hasReturn;
        private readonly bool _hasFetch;

        private SurrealQuery(
            string sql,
            IReadOnlyDictionary<string, object?> @params,
            bool isMultiStatement,
            bool hasWhere,
            bool hasOrderBy,
            bool hasLimit,
            bool hasStart,
            bool hasReturn,
            bool hasFetch)
        {
            Sql = sql;
            Params = @params;
            IsMultiStatement = isMultiStatement;
            _hasWhere   = hasWhere;
            _hasOrderBy = hasOrderBy;
            _hasLimit   = hasLimit;
            _hasStart   = hasStart;
            _hasReturn  = hasReturn;
            _hasFetch   = hasFetch;
        }

        // ─── Factory ─────────────────────────────────────────────────────────

        /// <summary>
        /// Begin building a query from a <em>compile-time constant</em>
        /// SurrealQL string.
        ///
        /// <code>
        /// var q = SurrealQuery.Of("SELECT * FROM wallet WHERE owner = $owner")
        ///                     .WithParam("owner", avatarId);
        /// </code>
        ///
        /// Never pass an interpolated string here; that is exactly what the
        /// SurrealQlSafetyAnalyzer (SRDB0001) is designed to detect and
        /// reject at compile time.
        ///
        /// <para>
        /// As of Phase 3, the body MUST be a single statement — a literal
        /// <c>;</c> anywhere in the SQL is rejected with a clear error.
        /// Use <see cref="Combine"/> to assemble multiple statements into a
        /// single request.  Closes code-review C5 design root.
        /// </para>
        /// </summary>
        public static SurrealQuery Of(string sql)
        {
            if (string.IsNullOrWhiteSpace(sql))
                throw new ArgumentException("SurrealQL statement must not be empty.", nameof(sql));

            if (sql.IndexOf(';') >= 0)
                throw new ArgumentException(
                    "SurrealQuery.Of accepts a single statement only — found ';' in the body. " +
                    "Use SurrealQuery.Combine(q1, q2, ...) to compose multi-statement requests; " +
                    "the per-statement results are then surfaced via SurrealResponse[i] (closes " +
                    "code-review C5).",
                    nameof(sql));

            // M3: conservatively scan the raw SQL for already-present clauses
            // so a subsequent .Where() on `Of("... WHERE x = 1")` still emits
            // AND (not WHERE). String literals are stripped first so a stray
            // "WHERE" inside a string does not flip the flag.
            ScanClauseFlagsFromLiteral(
                sql,
                out var hasWhere, out var hasOrderBy, out var hasLimit,
                out var hasStart, out var hasReturn, out var hasFetch);

            return new SurrealQuery(
                sql,
                new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?>(StringComparer.Ordinal)),
                isMultiStatement: false,
                hasWhere: hasWhere,
                hasOrderBy: hasOrderBy,
                hasLimit: hasLimit,
                hasStart: hasStart,
                hasReturn: hasReturn,
                hasFetch: hasFetch);
        }

        // ─── Typed convenience factories ─────────────────────────────────────

        /// <summary>
        /// Builds <c>SELECT * FROM {table} WHERE id = $id</c> for a single
        /// record lookup. The table name is validated through
        /// <see cref="SurrealIdentifier"/> before use.
        /// </summary>
        public static SurrealQuery SelectById(string table, object id)
        {
            // SurrealDB 3.x: `WHERE id = $id` with a plain STRING id matches
            // nothing — the stored id is a record id (table:key), not the bare
            // string. Address the record directly via type::record($_t, $_id)
            // (table + bare key both parameter-bound). The key strips any
            // leading `table:` so a link-form or bare id both work.
            // Validate the table name (throws on an invalid identifier) even
            // though it is parameter-bound — preserves the build-time guard.
            var safeTable = SurrealIdentifier.ForTable(table);
            return Of("SELECT * FROM type::record($_t, $_id)")
                   .WithParam("_t", safeTable)
                   .WithParam("_id", BareKey(id));
        }

        /// <summary>
        /// Builds <c>SELECT * FROM {table}</c>.
        /// The table name is validated through <see cref="SurrealIdentifier"/>
        /// before use.
        /// </summary>
        public static SurrealQuery SelectAll(string table)
        {
            var safeTable = SurrealIdentifier.ForTable(table);
            return Of("SELECT * FROM " + safeTable);
        }

        /// <summary>
        /// Builds <c>DELETE FROM {table} WHERE id = $id</c>.
        /// The table name is validated through <see cref="SurrealIdentifier"/>
        /// before use.
        /// </summary>
        public static SurrealQuery DeleteById(string table, object id)
        {
            // See SelectById: address the record via type::record($_t, $_id)
            // rather than `WHERE id = $stringId` (which matches nothing on 3.x).
            var safeTable = SurrealIdentifier.ForTable(table);
            return Of("DELETE type::record($_t, $_id)")
                   .WithParam("_t", safeTable)
                   .WithParam("_id", BareKey(id));
        }

        /// <summary>
        /// Strip a leading <c>table:</c> prefix from a record id so it binds as
        /// the bare key to <c>type::record($_t, $_id)</c>. Non-string ids pass
        /// through unchanged.
        /// </summary>
        private static object BareKey(object id)
        {
            if (id is string s)
            {
                int colon = s.IndexOf(':');
                return colon >= 0 ? s.Substring(colon + 1) : s;
            }
            return id;
        }

        // ─── Builder — params ────────────────────────────────────────────────

        /// <summary>
        /// Returns a new <see cref="SurrealQuery"/> with the given parameter
        /// added. The key must NOT include a leading <c>$</c>.
        /// </summary>
        public SurrealQuery WithParam(string key, object? value)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("Parameter key must not be empty.", nameof(key));
            if (key.StartsWith("$", StringComparison.Ordinal))
                throw new ArgumentException(
                    "Parameter key must not include the leading '$' — the SDK adds it.", nameof(key));
            RejectMixedCaseParam(key);

            var newParams = CloneParams(Params);
            newParams[key] = value;

            return CloneWith(Sql, new ReadOnlyDictionary<string, object?>(newParams));
        }

        /// <summary>
        /// Guards against a SurrealDB 3.x footgun: a <c>$param</c> token whose
        /// name contains an uppercase letter is case-folded in the query text but
        /// NOT in the RPC vars key, so the binding silently resolves to NONE and
        /// the predicate matches nothing (no error is raised). Reserved system
        /// params (<c>_t</c>, <c>_id</c>, <c>_body</c>, <c>_f_*</c>) and the
        /// snake_case names the LINQ translator emits are all lowercase, so this
        /// only ever fires on a hand-written camel/PascalCase key.
        /// </summary>
        private static void RejectMixedCaseParam(string key)
        {
            foreach (var c in key)
            {
                if (c >= 'A' && c <= 'Z')
                    throw new ArgumentException(
                        $"Parameter key '{key}' contains an uppercase letter. SurrealDB 3.x " +
                        "case-folds $param tokens but not the vars key, so a mixed-case name " +
                        "binds to NONE and silently matches nothing. Use a lowercase " +
                        "(snake_case) key, e.g. 'avatar_nft_id'.", nameof(key));
            }
        }

        /// <summary>
        /// Returns a new <see cref="SurrealQuery"/> with all given parameters
        /// merged in. Existing keys are overwritten.
        /// </summary>
        public SurrealQuery WithParams(IEnumerable<KeyValuePair<string, object?>> parameters)
        {
            if (parameters is null) throw new ArgumentNullException(nameof(parameters));

            var newParams = CloneParams(Params);
            foreach (var kv in parameters)
            {
                if (string.IsNullOrWhiteSpace(kv.Key))
                    throw new ArgumentException("A parameter key must not be empty.");
                newParams[kv.Key] = kv.Value;
            }
            return CloneWith(Sql, new ReadOnlyDictionary<string, object?>(newParams));
        }

        // ─── Builder — fluent clauses ────────────────────────────────────────
        //
        // Each method returns a *new* SurrealQuery — the receiver is never
        // mutated. Parameters merged in by the clause builder use the supplied
        // anonymous-object property names (or an explicit IDictionary) so the
        // resulting SQL stays parameterized.

        /// <summary>
        /// Appends a <c>WHERE {predicate}</c> clause (or <c>AND {predicate}</c>
        /// if a WHERE clause is already present) and merges the supplied
        /// parameter bag into <see cref="Params"/>.
        ///
        /// The <paramref name="predicate"/> is expected to reference
        /// <c>$param</c> tokens that line up with property names on
        /// <paramref name="paramObj"/>.  No interpolation is performed; this
        /// is a parameter-binding helper, not a SQL templater.
        /// </summary>
        public SurrealQuery Where(string predicate, object? paramObj = null)
        {
            if (string.IsNullOrWhiteSpace(predicate))
                throw new ArgumentException("WHERE predicate must not be empty.", nameof(predicate));

            var newSql = _hasWhere
                ? Sql + " AND " + predicate
                : Sql + " WHERE " + predicate;

            var merged = MergeParams(Params, paramObj);
            return CloneWith(
                newSql,
                new ReadOnlyDictionary<string, object?>(merged),
                setHasWhere: true);
        }

        /// <summary>
        /// Appends an <c>ORDER BY {field} {ASC|DESC}</c> clause.  The field
        /// name is identifier-validated to prevent token smuggling.
        /// </summary>
        public SurrealQuery OrderBy(string field, OrderDirection direction = OrderDirection.Asc)
        {
            var safeField = ValidateFieldPath(field, nameof(field));
            var dir = direction == OrderDirection.Desc ? "DESC" : "ASC";
            return CloneWith(
                Sql + " ORDER BY " + safeField + " " + dir,
                Params,
                setHasOrderBy: true);
        }

        /// <summary>Appends a <c>LIMIT n</c> clause. <paramref name="n"/> must
        /// be non-negative.</summary>
        public SurrealQuery Limit(int n)
        {
            if (n < 0)
                throw new ArgumentOutOfRangeException(nameof(n), "LIMIT must be non-negative.");
            return CloneWith(
                Sql + " LIMIT " + n.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Params,
                setHasLimit: true);
        }

        /// <summary>Appends a <c>START n</c> clause (page offset).
        /// <paramref name="n"/> must be non-negative.</summary>
        public SurrealQuery Start(int n)
        {
            if (n < 0)
                throw new ArgumentOutOfRangeException(nameof(n), "START must be non-negative.");
            return CloneWith(
                Sql + " START " + n.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Params,
                setHasStart: true);
        }

        /// <summary>
        /// Appends a <c>RETURN {BEFORE|AFTER|DIFF|NONE}</c> clause for DML
        /// statements.  Closed enum prevents arbitrary tokens.
        /// </summary>
        public SurrealQuery Return(ReturnClause clause)
        {
            string token;
            switch (clause)
            {
                case ReturnClause.Before: token = "BEFORE"; break;
                case ReturnClause.After:  token = "AFTER";  break;
                case ReturnClause.Diff:   token = "DIFF";   break;
                case ReturnClause.None:   token = "NONE";   break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(clause), clause, "Unknown ReturnClause value.");
            }
            return CloneWith(Sql + " RETURN " + token, Params, setHasReturn: true);
        }

        /// <summary>
        /// String-overload for <see cref="Return(ReturnClause)"/>.
        /// Accepts <c>"BEFORE"</c>, <c>"AFTER"</c>, <c>"DIFF"</c>, <c>"NONE"</c>
        /// (case-insensitive); anything else throws.
        /// </summary>
        public SurrealQuery Return(string clause)
        {
            if (string.IsNullOrWhiteSpace(clause))
                throw new ArgumentException("RETURN clause must not be empty.", nameof(clause));

            switch (clause.Trim().ToUpperInvariant())
            {
                case "BEFORE": return Return(ReturnClause.Before);
                case "AFTER":  return Return(ReturnClause.After);
                case "DIFF":   return Return(ReturnClause.Diff);
                case "NONE":   return Return(ReturnClause.None);
                default:
                    throw new ArgumentException(
                        "RETURN clause must be one of BEFORE, AFTER, DIFF, NONE — got '" + clause + "'.",
                        nameof(clause));
            }
        }

        /// <summary>
        /// Appends a <c>FETCH {path}</c> clause for graph traversal.  The
        /// path expression is identifier-validated (supports dotted paths and
        /// <c>-></c> arrows; see <see cref="ValidateFieldPath"/>).
        /// </summary>
        public SurrealQuery Fetch(string path)
        {
            var safePath = ValidateFieldPath(path, nameof(path));
            return CloneWith(Sql + " FETCH " + safePath, Params, setHasFetch: true);
        }

        // ─── G2 — UpdateOnly(...).Where(...).Set(...) ────────────────────────
        //
        // The conditional-state-transition primitive. The shape is:
        //
        //   UPDATE type::record($_t, $_id) WHERE $field = $value SET $field = $value RETURN AFTER;
        //
        // Internally we route through a tiny builder type (UpdateOnlyBuilder)
        // that holds the in-progress state until the user calls .Set(...) to
        // finalize. .Where(...) is required; missing-where throws at .Set time.

        /// <summary>
        /// Begins a single-record conditional update (the G2 primitive).
        ///
        /// Usage:
        /// <code>
        /// SurrealQuery.UpdateOnly("operation_log", id)
        ///             .Where("status", "pending")
        ///             .Set("status", "complete");
        /// </code>
        ///
        /// Emits, after <c>.Set(...)</c>:
        /// <code>
        /// UPDATE type::record($_t, $_id)
        ///   WHERE status = $_w_status SET status = $_s_status
        ///   RETURN AFTER;
        /// </code>
        ///
        /// The execute path returns a <see cref="SurrealStatementResult"/>
        /// whose <see cref="SurrealStatementResultExtensions.EnsureSingleAffected{T}"/>
        /// extension guarantees exactly one row matched. Closes code-review
        /// C5 use-case.
        /// </summary>
        public static UpdateOnlyBuilder UpdateOnly(string table, string id)
        {
            var safeTable = SurrealIdentifier.ForTable(table);
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException("Record id must not be empty.", nameof(id));
            return new UpdateOnlyBuilder(safeTable, id);
        }

        // ─── Relate(...).WithContent(...) ────────────────────────────────────

        /// <summary>
        /// Builds a SurrealQL <c>RELATE</c> graph statement of the form
        /// <c>RELATE $_from -> edge -> $_to CONTENT $_content;</c>.
        ///
        /// The edge table name is identifier-validated; the from/to record
        /// IDs are already validated by <see cref="SurrealRecordId.Create"/>.
        /// The content payload is JSON-serialized at execute time.
        /// </summary>
        public static RelateBuilder Relate(SurrealRecordId from, string edgeTable, SurrealRecordId to)
        {
            var safeEdge = SurrealIdentifier.ForTable(edgeTable);
            return new RelateBuilder(from, safeEdge, to);
        }

        // ─── Combine — multi-statement composition ───────────────────────────

        /// <summary>
        /// Combines two or more single-statement <see cref="SurrealQuery"/>
        /// instances into a single multi-statement query.
        ///
        /// The combined query's <see cref="Sql"/> is the inner statements
        /// joined by <c>;</c> separators (with a trailing terminator on the
        /// last one); the parameter dictionaries are merged with last-write-
        /// wins on key collisions (callers are responsible for picking
        /// disjoint parameter names — typical practice prefixes each
        /// statement's params, e.g. <c>$s1_owner</c>, <c>$s2_owner</c>).
        ///
        /// The combined query is marked <see cref="IsMultiStatement"/>;
        /// executors return a per-statement <see cref="SurrealResponse"/>
        /// instead of collapsing onto the first result.  This is the **only**
        /// legal multi-statement path — <see cref="Of"/> rejects semicolons
        /// in single statements.  Closes code-review C5.
        /// </summary>
        public static SurrealQuery Combine(params SurrealQuery[] queries)
        {
            if (queries is null) throw new ArgumentNullException(nameof(queries));
            if (queries.Length < 2)
                throw new ArgumentException(
                    "Combine requires at least two queries; for a single statement use SurrealQuery.Of(...) directly.",
                    nameof(queries));
            foreach (var q in queries)
            {
                if (q is null)
                    throw new ArgumentException("Combine does not accept null queries.", nameof(queries));
                if (q.IsMultiStatement)
                    throw new ArgumentException(
                        "Combine cannot nest multi-statement queries — flatten the list before combining.",
                        nameof(queries));
            }

            var sb = new StringBuilder();
            var merged = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (int i = 0; i < queries.Length; i++)
            {
                var q = queries[i];
                var trimmed = q.Sql.TrimEnd();
                sb.Append(trimmed);
                if (!trimmed.EndsWith(";", StringComparison.Ordinal))
                    sb.Append(';');
                if (i < queries.Length - 1)
                    sb.Append(' ');

                foreach (var kv in q.Params)
                    merged[kv.Key] = kv.Value;
            }

            return new SurrealQuery(
                sb.ToString(),
                new ReadOnlyDictionary<string, object?>(merged),
                isMultiStatement: true,
                // Flags are not meaningful on a multi-statement composite —
                // chaining .Where() / .OrderBy() etc. onto a Combine() result
                // would produce ill-formed SurrealQL regardless. Default-false
                // is the safe stance.
                hasWhere: false, hasOrderBy: false, hasLimit: false,
                hasStart: false, hasReturn: false, hasFetch: false);
        }

        // ─── Validation ──────────────────────────────────────────────────────

        /// <summary>
        /// Validates that every <c>$param</c> token in <see cref="Sql"/> has a
        /// corresponding entry in <see cref="Params"/>.
        ///
        /// In strict mode (the default, and the only mode used in production),
        /// extra parameters that are provided but not referenced in the SQL
        /// are also rejected — they are usually a sign of a typo or a
        /// refactoring mistake.
        ///
        /// Throws <see cref="SurrealQueryValidationException"/> on any
        /// violation.
        /// </summary>
        public void Validate(bool strict = true)
        {
            var referenced = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match m in ParamTokenRegex.Matches(Sql))
                referenced.Add(m.Groups[1].Value);

            // Variables defined by `LET $x = ...` inside the body are supplied by
            // the SQL itself, not the param bag — drop them from the reference set.
            var letDefined = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match m in LetTargetRegex.Matches(Sql))
                letDefined.Add(m.Groups[1].Value);
            referenced.ExceptWith(letDefined);

            var provided = new HashSet<string>(Params.Keys, StringComparer.Ordinal);

            var missing = referenced.Except(provided).ToList();
            if (missing.Count > 0)
                throw new SurrealQueryValidationException(
                    "SurrealQL query references parameter(s) that were not supplied: " +
                    string.Join(", ", missing.Select(p => "$" + p)) + ".\n" +
                    "SQL: " + Sql);

            if (strict)
            {
                var extra = provided.Except(referenced).ToList();
                if (extra.Count > 0)
                    throw new SurrealQueryValidationException(
                        "SurrealQL query was supplied parameter(s) that are not referenced in the SQL (strict mode): " +
                        string.Join(", ", extra.Select(p => "$" + p)) + ".\n" +
                        "SQL: " + Sql + "\n" +
                        "Pass strict: false to allow extra parameters, or remove the unused binding.");
            }
        }

        /// <summary>
        /// Renders the full SQL body (for diagnostics or direct dispatch by
        /// the transport). Identical to <see cref="Sql"/>; provided as a
        /// builder-conventional alias.
        /// </summary>
        public string Build() => Sql;

        // ─── Diagnostics ─────────────────────────────────────────────────────

        public override string ToString() =>
            "SurrealQuery { Sql = \"" + Sql + "\", Params = [" +
            string.Join(", ", Params.Select(kv => "$" + kv.Key + "=" + kv.Value)) + "] }";

        // ─── Helpers ─────────────────────────────────────────────────────────

        /// <summary>Internal constructor used by the nested builders to produce
        /// the final immutable query.</summary>
        internal static SurrealQuery FromBuilder(
            string sql,
            IReadOnlyDictionary<string, object?> @params)
        {
            // M3: builders (UpdateOnly, Relate, ...) produce SurrealQL bodies
            // that may already contain WHERE / RETURN / etc. Scan once at
            // construction so any subsequent fluent .Where() etc. routes
            // through the correct branch.
            ScanClauseFlagsFromLiteral(
                sql,
                out var hasWhere, out var hasOrderBy, out var hasLimit,
                out var hasStart, out var hasReturn, out var hasFetch);

            return new SurrealQuery(
                sql,
                new ReadOnlyDictionary<string, object?>(CloneParams(@params)),
                isMultiStatement: false,
                hasWhere: hasWhere,
                hasOrderBy: hasOrderBy,
                hasLimit: hasLimit,
                hasStart: hasStart,
                hasReturn: hasReturn,
                hasFetch: hasFetch);
        }

        private static Dictionary<string, object?> CloneParams(
            IReadOnlyDictionary<string, object?> source)
        {
            var clone = new Dictionary<string, object?>(source.Count, StringComparer.Ordinal);
            foreach (var kv in source) clone[kv.Key] = kv.Value;
            return clone;
        }

        // MEDIUM #M3 — flag-threaded immutable clone. Each fluent method
        // produces a new SurrealQuery via this helper so existing flag state
        // is preserved AND the appropriate flag for the clause being added is
        // set to true. The optional setHas* parameters default to false so
        // helpers that only mutate params (WithParam / WithParams) leave the
        // flag set untouched.
        private SurrealQuery CloneWith(
            string sql,
            IReadOnlyDictionary<string, object?> @params,
            bool setHasWhere = false,
            bool setHasOrderBy = false,
            bool setHasLimit = false,
            bool setHasStart = false,
            bool setHasReturn = false,
            bool setHasFetch = false)
        {
            return new SurrealQuery(
                sql,
                @params,
                IsMultiStatement,
                hasWhere:   _hasWhere   || setHasWhere,
                hasOrderBy: _hasOrderBy || setHasOrderBy,
                hasLimit:   _hasLimit   || setHasLimit,
                hasStart:   _hasStart   || setHasStart,
                hasReturn:  _hasReturn  || setHasReturn,
                hasFetch:   _hasFetch   || setHasFetch);
        }

        /// <summary>
        /// One-time scan of a raw SurrealQL body to detect already-present
        /// clauses (WHERE / ORDER BY / LIMIT / START / RETURN / FETCH).
        ///
        /// Strips single- and double-quoted string literals before scanning so
        /// the keyword inside <c>"check WHERE field"</c> no longer flips the
        /// WHERE flag — that false-positive is the exact defect M3 closes.
        /// </summary>
        private static void ScanClauseFlagsFromLiteral(
            string sql,
            out bool hasWhere,
            out bool hasOrderBy,
            out bool hasLimit,
            out bool hasStart,
            out bool hasReturn,
            out bool hasFetch)
        {
            var stripped = StripStringLiterals(sql);
            hasWhere   = ContainsKeyword(stripped, "WHERE");
            hasOrderBy = ContainsKeyword(stripped, "ORDER BY");
            hasLimit   = ContainsKeyword(stripped, "LIMIT");
            hasStart   = ContainsKeyword(stripped, "START");
            hasReturn  = ContainsKeyword(stripped, "RETURN");
            hasFetch   = ContainsKeyword(stripped, "FETCH");
        }

        private static string StripStringLiterals(string sql)
        {
            // Simple state machine: copy chars unless inside a "..." or '...'
            // span. Escape sequences inside literals are handled by tracking
            // the previous char and skipping the escaped quote.
            var sb = new StringBuilder(sql.Length);
            char quote = '\0';
            for (int i = 0; i < sql.Length; i++)
            {
                char c = sql[i];
                if (quote == '\0')
                {
                    if (c == '"' || c == '\'') { quote = c; continue; }
                    sb.Append(c);
                }
                else
                {
                    // Inside literal: skip everything except the matching
                    // close-quote, and handle backslash-escaped quotes.
                    if (c == '\\' && i + 1 < sql.Length) { i++; continue; }
                    if (c == quote) { quote = '\0'; continue; }
                }
            }
            return sb.ToString();
        }

        private static bool ContainsKeyword(string sql, string keyword)
        {
            // Word-boundary scan (case-insensitive). Multi-word keywords such
            // as "ORDER BY" use a single regex with \s+ between the parts so
            // arbitrary whitespace between them still matches.
            var pattern = keyword.Contains(' ')
                ? @"\b" + keyword.Replace(" ", @"\s+") + @"\b"
                : @"\b" + keyword + @"\b";
            return Regex.IsMatch(sql, pattern, RegexOptions.IgnoreCase);
        }

        // Field-path validator used by OrderBy / Fetch. Permits dotted paths
        // (a.b.c) and graph arrows (->edge.field). Rejects whitespace,
        // semicolons, quotes, and anything else that could alter clause shape.
        private static readonly Regex FieldPathRegex =
            new Regex(@"^[a-zA-Z_][a-zA-Z0-9_]*((\.[a-zA-Z_][a-zA-Z0-9_]*)|(->[a-zA-Z_][a-zA-Z0-9_]*))*$",
                RegexOptions.Compiled, TimeSpan.FromSeconds(1));

        internal static string ValidateFieldPath(string path, string paramName)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Field path must not be empty.", paramName);
            if (!FieldPathRegex.IsMatch(path))
                throw new ArgumentException(
                    "Field path '" + path + "' contains invalid characters. " +
                    "Allowed shape: identifier (a.b.c) or graph-arrow path (a->b->c). " +
                    "No whitespace, quotes, or punctuation.",
                    paramName);
            return path;
        }

        private static Dictionary<string, object?> MergeParams(
            IReadOnlyDictionary<string, object?> existing,
            object? paramObj)
        {
            var merged = CloneParams(existing);
            if (paramObj is null) return merged;

            // Anonymous object: read public properties via reflection.
            if (paramObj is IDictionary<string, object?> dict)
            {
                foreach (var kv in dict) merged[kv.Key] = kv.Value;
                return merged;
            }
            if (paramObj is IEnumerable<KeyValuePair<string, object?>> kvs)
            {
                foreach (var kv in kvs) merged[kv.Key] = kv.Value;
                return merged;
            }

            var type = paramObj.GetType();
            foreach (var prop in type.GetProperties(
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public))
            {
                if (!prop.CanRead) continue;
                merged[prop.Name] = prop.GetValue(paramObj);
            }
            return merged;
        }
    }

    /// <summary>
    /// Thrown when a <see cref="SurrealQuery"/> fails parameter validation.
    /// </summary>
    public sealed class SurrealQueryValidationException : Exception
    {
        public SurrealQueryValidationException(string message) : base(message) { }
    }
}
