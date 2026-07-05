using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace SurrealForge.Client.Query
{
    /// <summary>
    /// Validates and sanitizes SurrealQL identifiers (table names, record-id
    /// suffixes).
    ///
    /// This is the ONLY approved way to inject a name that cannot be a query
    /// parameter — for example when the table name itself is determined at
    /// runtime.  Do NOT roll your own interpolation: use this class, or
    /// parameterize instead.
    ///
    /// Three concentric checks are applied (in order):
    /// <list type="number">
    ///   <item>Non-empty / non-whitespace.</item>
    ///   <item>Strict character allowlist (lowercase + digit + underscore).</item>
    ///   <item>Reserved-word denylist (closes code-review H4). SurrealQL
    ///         keywords like <c>SELECT</c>, <c>WHERE</c>, <c>DELETE</c>, etc.
    ///         are rejected even when they pass the allowlist — they would
    ///         otherwise smuggle clause-altering tokens through to the wire.</item>
    /// </list>
    ///
    /// <example>
    /// <code>
    /// // Correct — table name validated before use:
    /// var table = SurrealIdentifier.ForTable("wallet");
    /// var query = SurrealQuery.Of($"SELECT * FROM {table} WHERE owner = $owner")
    ///                         .WithParam("owner", avatarId);
    ///
    /// // Wrong — never do this:
    /// var query = SurrealQuery.Of($"SELECT * FROM {userInput} WHERE owner = $owner");
    /// </code>
    /// </example>
    /// </summary>
    public static class SurrealIdentifier
    {
        // Strict allowlist: lowercase, starts with a letter, only [a-z0-9_].
        // Rejects: uppercase, leading digits, spaces, semicolons, backticks,
        // quotes, hyphens, or any other character that could alter query
        // semantics.
        private static readonly Regex TableNameRegex =
            new Regex(@"^[a-z][a-z0-9_]*$", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

        // SurrealDB record IDs: <table>:<id> — both segments validated
        // individually.
        private static readonly Regex RecordIdSuffixRegex =
            new Regex(@"^[a-zA-Z0-9_\-]+$", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

        // SurrealQL reserved-word denylist. Curated from
        // https://surrealdb.com/docs/surrealql/datamodel and the broader
        // SurrealQL statement vocabulary. The list is intentionally
        // **inclusive** — a small false-positive rate (e.g. you can't have a
        // table named "user") is far cheaper than a single false-negative
        // (e.g. a table named "select" smuggling a SELECT clause through).
        //
        // Comparison is performed against the lower-cased input (table names
        // are already constrained to lowercase by the allowlist; record-id
        // suffixes are compared case-insensitively for defence-in-depth).
        //
        // Closes code-review H4.
        private static readonly HashSet<string> ReservedWords = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase)
        {
            // DML
            "select", "from", "where", "create", "update", "delete", "insert",
            "relate", "merge", "set", "content", "patch",
            // DDL
            "define", "remove", "field", "table", "index", "type", "assert",
            "option", "schemafull", "schemaless", "namespace", "database",
            "user", "token", "scope", "permissions", "full", "none",
            // LIVE / KILL
            "live", "kill",
            // Transactions
            "begin", "commit", "cancel", "transaction",
            // Return / fetch
            "return", "fetch", "omit",
            // Ordering / paging
            "order", "group", "limit", "start", "with", "noindex",
            // Predicates
            "contains", "inside", "outside", "intersects",
            "allinside", "anyinside", "noneinside",
            "is", "not", "and", "or",
            // Control flow
            "if", "then", "else", "end", "function",
            "for", "break", "continue",
            // Literals / info
            "null", "true", "false", "info",
        };

        /// <summary>
        /// Validates <paramref name="name"/> as a SurrealDB table identifier.
        ///
        /// Accepted: <c>wallet</c>, <c>bridge_tx</c>, <c>avatar_nft</c>.
        /// Rejected: <c>USERS</c> (uppercase), <c>1users</c> (leading digit),
        ///           <c>users x</c> (space), <c>users; DROP TABLE x</c> (injection),
        ///           <c>select</c> (reserved word), <c>`users`x</c> (backtick).
        ///
        /// Returns the validated name unchanged so it can be used inline in
        /// <see cref="SurrealQuery.Of"/> string literals (compile-time constant
        /// interpolation with a validated value is safe).
        /// </summary>
        /// <exception cref="SurrealIdentifierException">
        /// Thrown when <paramref name="name"/> is empty, fails the strict
        /// allowlist, or matches a SurrealQL reserved word.
        /// </exception>
        public static string ForTable(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new SurrealIdentifierException(
                    "SurrealDB table name must not be empty.", nameof(name));

            if (!TableNameRegex.IsMatch(name))
                throw new SurrealIdentifierException(
                    "SurrealDB table name '" + name + "' is not a valid identifier. " +
                    "Must match ^[a-z][a-z0-9_]*$ — lowercase letters, digits, " +
                    "underscores; must start with a letter. No spaces, uppercase, " +
                    "punctuation, or SQL keywords.",
                    nameof(name));

            if (ReservedWords.Contains(name))
                throw new SurrealIdentifierException(
                    "SurrealDB table name '" + name + "' is a SurrealQL reserved word " +
                    "and would alter query semantics if used unquoted. Choose a " +
                    "different identifier (e.g. prefix with the aggregate name). " +
                    "See https://surrealdb.com/docs/surrealql/datamodel for the full " +
                    "reserved-word list.",
                    nameof(name));

            return name;
        }

        /// <summary>
        /// Constructs a validated SurrealDB record ID in the form <c>table:id</c>.
        ///
        /// Use this when you need to reference a specific record (e.g. as a typed
        /// <c>record()</c> field default or a relation endpoint) and cannot use a
        /// parameter.  The table portion is validated by <see cref="ForTable"/>;
        /// the id suffix is validated to contain only alphanumerics, underscores,
        /// and hyphens (safe for SurrealQL unquoted record IDs) and is also
        /// checked against the reserved-word denylist.
        ///
        /// For record IDs that are GUIDs or other opaque values, prefer using the
        /// SDK's typed <c>RecordId</c> as a query parameter via
        /// <see cref="SurrealQuery.WithParam"/> instead of constructing a string ID.
        /// </summary>
        /// <exception cref="SurrealIdentifierException">
        /// Thrown when either segment fails validation, or when the id segment
        /// matches a SurrealQL reserved word.
        /// </exception>
        public static string ForRecordId(string table, string id)
        {
            var safeTable = ForTable(table); // validates table name (+ reserved)

            if (string.IsNullOrWhiteSpace(id))
                throw new SurrealIdentifierException(
                    "SurrealDB record ID suffix must not be empty.", nameof(id));

            if (!RecordIdSuffixRegex.IsMatch(id))
                throw new SurrealIdentifierException(
                    "SurrealDB record ID suffix '" + id + "' contains invalid characters. " +
                    "Allowed: alphanumerics, underscores, hyphens. " +
                    "For arbitrary/opaque IDs use a typed parameter via SurrealQuery.WithParam.",
                    nameof(id));

            if (ReservedWords.Contains(id))
                throw new SurrealIdentifierException(
                    "SurrealDB record ID suffix '" + id + "' is a SurrealQL reserved word " +
                    "and would alter query semantics if used unquoted. Use a typed " +
                    "parameter via SurrealQuery.WithParam for opaque IDs.",
                    nameof(id));

            return safeTable + ":" + id;
        }
    }
}
