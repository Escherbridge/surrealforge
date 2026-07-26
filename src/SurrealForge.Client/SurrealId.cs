// SPDX-License-Identifier: MIT
// SurrealForge.Client -- canonical Guid <-> record-id-hex conversions.
//
// Stores conventionally key SurrealDB records by a Guid rendered as 32-char
// lowercase hex (no dashes) -- the form SurrealLink.ToLink expects as `id`.
// Centralized here so consumers do not maintain incompatible private copies.

using System;

namespace SurrealForge.Client
{
    /// <summary>
    /// Canonical conversions between a <see cref="Guid"/> and its SurrealDB
    /// record-id rendering (32-char lowercase hex, no dashes) — the `id` half
    /// of the <c>table:id</c> link form produced by <see cref="SurrealLink"/>.
    /// </summary>
    public static class SurrealId
    {
        /// <summary>Render a Guid as the 32-char lowercase hex record id.</summary>
        public static string ToSurrealId(Guid id) => id.ToString("N").ToLowerInvariant();

        /// <summary>Parse a 32-char hex record id back to a Guid.</summary>
        public static Guid FromSurrealId(string id) => Guid.ParseExact(id, "N");

        /// <summary>
        /// Normalize a bare record id or <c>table:id</c> link returned by
        /// SurrealDB. Quoted id renderings are accepted at response boundaries.
        /// </summary>
        public static string BareRecordId(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("Record id is required.", nameof(value));

            var bare = SurrealLink.FromLink(value) ?? string.Empty;
            return bare.Length >= 2
                && ((bare[0] == '`' && bare[bare.Length - 1] == '`')
                    || (bare[0] == '"' && bare[bare.Length - 1] == '"'))
                ? bare.Substring(1, bare.Length - 2)
                : bare;
        }

        /// <summary>Parse a bare or linked Surreal record id into a Guid.</summary>
        public static Guid ParseRecordGuid(string value)
        {
            var bare = BareRecordId(value);
            return Guid.TryParse(bare, out var parsed) ? parsed : FromSurrealId(bare);
        }

        /// <summary>Parse an optional bare or linked Surreal record id.</summary>
        public static Guid? ParseOptionalRecordGuid(string? value)
            => string.IsNullOrWhiteSpace(value) ? null : ParseRecordGuid(value);
    }
}
