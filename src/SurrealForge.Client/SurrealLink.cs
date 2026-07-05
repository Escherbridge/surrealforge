// SPDX-License-Identifier: UNLICENSED
// SurrealForge.Client -- helpers for foreign-key (record link) fields.
//
// SurrealDB schema fields typed `record<table>` (or `option<record<table>>`)
// are FOREIGN KEYS. SurrealDB 1.5.x silently coerced a bare id string into the
// link; 3.x strictly rejects it ("Couldn't coerce value ... Expected
// record<avatar> but found 'b0a7...'"). The wire form 3.x DOES accept for such
// a field is the record-link STRING `table:id` (the object form {tb,id} is
// rejected). These helpers produce/parse exactly that string so store POCOs can
// keep a plain `string?` field while writing a value the engine accepts.

using System;

namespace SurrealForge.Client
{
    /// <summary>
    /// Converts between a 32-char hex id (the form stores use for primary keys)
    /// and the SurrealDB record-link string <c>table:id</c> required by
    /// <c>record&lt;table&gt;</c> foreign-key fields on SurrealDB 3.x.
    /// </summary>
    public static class SurrealLink
    {
        /// <summary>
        /// Build the record-link string for a foreign-key field, e.g.
        /// <c>ToLink("avatar", "b0a7...")</c> → <c>"avatar:b0a7..."</c>.
        /// Returns <c>null</c> when <paramref name="id"/> is null/empty so an
        /// <c>option&lt;record&lt;...&gt;&gt;</c> field stays unset.
        /// </summary>
        public static string? ToLink(string table, string? id)
        {
            if (string.IsNullOrEmpty(table)) throw new ArgumentException("table is required.", nameof(table));
            if (string.IsNullOrEmpty(id)) return null;
            // Already a link (contains the table prefix)? Pass through untouched.
            return id!.IndexOf(':') >= 0 ? id : table + ":" + id;
        }

        /// <summary>
        /// Strip the <c>table:</c> prefix from a record-link string read back
        /// from SurrealDB, returning the bare id. Tolerates a value that is
        /// already bare (no colon) and <c>null</c>.
        /// </summary>
        public static string? FromLink(string? link)
        {
            if (string.IsNullOrEmpty(link)) return null;
            var idx = link!.IndexOf(':');
            return idx >= 0 ? link.Substring(idx + 1) : link;
        }
    }
}
