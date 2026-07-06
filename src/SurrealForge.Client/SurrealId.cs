// SPDX-License-Identifier: UNLICENSED
// SurrealForge.Client -- canonical Guid <-> record-id-hex conversions.
//
// Stores conventionally key SurrealDB records by a Guid rendered as 32-char
// lowercase hex (no dashes) -- the form SurrealLink.ToLink expects as `id`.
// Promoted from the identical private forks consumers kept re-writing per
// store (upstreamed from AZOA.WebAPI/Helpers/SurrealId.cs, 2026-07-06).

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
    }
}
