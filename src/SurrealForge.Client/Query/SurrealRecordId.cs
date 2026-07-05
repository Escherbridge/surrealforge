using System;

namespace SurrealForge.Client.Query
{
    /// <summary>
    /// Lightweight value object representing a SurrealDB record identifier
    /// of the form <c>table:id</c>.  Used by the query builder's graph
    /// primitives (e.g. <see cref="SurrealQuery.Relate"/>) as a typed
    /// replacement for raw <c>string</c> record IDs at the public API
    /// boundary.
    ///
    /// Construction routes through <see cref="SurrealIdentifier.ForRecordId"/>
    /// so the table portion is allowlisted, the id portion is restricted to
    /// safe characters, and neither segment can be a SurrealQL reserved word.
    ///
    /// NOTE: A1's Phase 2 introduces a richer <c>RecordId</c> type alongside
    /// the JSON converter set (it round-trips through SurrealDB's CBOR/JSON
    /// record-id encoding).  This is a separate, simpler value type used
    /// strictly inside the query-builder layer; A5 reconciles them at
    /// integration if needed.
    /// </summary>
    public readonly struct SurrealRecordId : IEquatable<SurrealRecordId>
    {
        /// <summary>Validated table portion (lowercase, allowlist, non-reserved).</summary>
        public string Table { get; }

        /// <summary>Validated id suffix (alphanumeric + underscore + hyphen).</summary>
        public string Id { get; }

        private SurrealRecordId(string table, string id)
        {
            Table = table;
            Id = id;
        }

        /// <summary>
        /// Constructs a validated record ID. Throws
        /// <see cref="SurrealIdentifierException"/> on any segment violation.
        /// </summary>
        public static SurrealRecordId Create(string table, string id)
        {
            // Validate via the central identifier policy. ForRecordId returns
            // the combined string but we split here to keep the parts as
            // separate, addressable fields.
            _ = SurrealIdentifier.ForRecordId(table, id);
            return new SurrealRecordId(table, id);
        }

        /// <summary>Renders as <c>table:id</c> for direct query interpolation.</summary>
        public override string ToString() => Table + ":" + Id;

        public bool Equals(SurrealRecordId other) =>
            string.Equals(Table, other.Table, StringComparison.Ordinal) &&
            string.Equals(Id, other.Id, StringComparison.Ordinal);

        public override bool Equals(object? obj) =>
            obj is SurrealRecordId other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                return ((Table?.GetHashCode() ?? 0) * 397) ^ (Id?.GetHashCode() ?? 0);
            }
        }

        public static bool operator ==(SurrealRecordId left, SurrealRecordId right) => left.Equals(right);
        public static bool operator !=(SurrealRecordId left, SurrealRecordId right) => !left.Equals(right);
    }
}
