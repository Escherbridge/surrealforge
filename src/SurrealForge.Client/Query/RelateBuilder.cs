using System;
using System.Collections.Generic;

namespace SurrealForge.Client.Query
{
    /// <summary>
    /// Mid-state builder for the graph <c>RELATE</c> primitive.  Returned by
    /// <see cref="SurrealQuery.Relate(SurrealRecordId, string, SurrealRecordId)"/>.
    /// The caller chains <see cref="WithContent"/> to attach the edge payload
    /// and produce the final <see cref="SurrealQuery"/>.
    ///
    /// The emitted statement uses <c>type::record($_from_t, $_from_id) -> edge
    /// -> type::record($_to_t, $_to_id) CONTENT $_content;</c> so neither the
    /// record IDs nor the content payload are interpolated.
    ///
    /// NOTE: A1's Phase 2 ships <c>SurrealJsonOptions.Default</c> which the
    /// HTTP transport uses to serialize content payloads with the right
    /// converter set (enums-as-string, RecordId round-trip, etc.).  Until
    /// that lands we attach the raw payload object to the parameter bag and
    /// let the transport (or test harness) serialize at dispatch time.  This
    /// keeps the query builder transport-agnostic and avoids a hard
    /// dependency on A1's not-yet-shipped JSON options.
    /// </summary>
    public sealed class RelateBuilder
    {
        private readonly SurrealRecordId _from;
        private readonly string _edge;
        private readonly SurrealRecordId _to;

        internal RelateBuilder(SurrealRecordId from, string edge, SurrealRecordId to)
        {
            _from = from;
            _edge = edge;
            _to = to;
        }

        /// <summary>
        /// Attaches the edge's content payload (any object — typically an
        /// anonymous object with edge attributes such as <c>weight</c>,
        /// <c>created_at</c>, etc.) and returns the finalized
        /// <see cref="SurrealQuery"/>.
        ///
        /// The payload is bound as a parameter, not interpolated; the
        /// transport serializes it via the configured JSON options at
        /// dispatch time.
        /// </summary>
        public SurrealQuery WithContent(object content)
        {
            if (content is null) throw new ArgumentNullException(nameof(content));

            // SurrealDB v2.x rename: `type::thing(...)` is deprecated and the
            // parser rejects it; the canonical name is `type::record(...)`.
            var sql =
                "RELATE type::record($_from_t, $_from_id) -> " + _edge +
                " -> type::record($_to_t, $_to_id) CONTENT $_content";

            var paramBag = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["_from_t"] = _from.Table,
                ["_from_id"] = _from.Id,
                ["_to_t"] = _to.Table,
                ["_to_id"] = _to.Id,
                ["_content"] = content,
            };

            return SurrealQuery.FromBuilder(sql, paramBag);
        }
    }
}
