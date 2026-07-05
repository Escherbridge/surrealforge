using System;
using System.Collections.Generic;

namespace SurrealForge.Client.Query
{
    /// <summary>
    /// Mid-state builder for the G2 conditional-state-transition primitive.
    ///
    /// Returned by <see cref="SurrealQuery.UpdateOnly(string, string)"/>; the
    /// caller chains <see cref="Where(string, object)"/> then
    /// <see cref="Set(string, object)"/> to produce the final
    /// <see cref="SurrealQuery"/>.
    ///
    /// The emitted statement uses <c>type::record($_t, $_id)</c> so the record
    /// reference is itself parameterized — neither the table name nor the id
    /// is interpolated into the SQL body.
    ///
    /// <para>
    /// Single-row enforcement is the caller's responsibility on the read side:
    /// after execution, invoke
    /// <see cref="SurrealStatementResultExtensions.EnsureSingleAffected{T}"/>
    /// on the returned result. The builder cannot enforce this server-side (Surreal
    /// has no <c>UPDATE … LIMIT 1</c>), but the <c>type::record(table, id)</c>
    /// addressing combined with the explicit WHERE makes a multi-affected
    /// outcome a bug in the schema, not the query.
    /// </para>
    /// </summary>
    public sealed class UpdateOnlyBuilder
    {
        private readonly string _table;
        private readonly string _id;
        private string? _whereField;
        private object? _whereValue;

        internal UpdateOnlyBuilder(string table, string id)
        {
            _table = table;
            _id = id;
        }

        /// <summary>
        /// Specifies the conditional predicate.  Required — calling
        /// <see cref="Set"/> without first calling Where throws.
        /// </summary>
        public UpdateOnlyBuilder Where(string field, object? value)
        {
            SurrealQuery.ValidateFieldPath(field, nameof(field));
            _whereField = field;
            _whereValue = value;
            return this;
        }

        /// <summary>
        /// Finalizes the builder and returns the immutable
        /// <see cref="SurrealQuery"/>.  The emitted SQL is:
        /// <code>
        /// UPDATE type::record($_t, $_id)
        ///   WHERE {whereField} = $_w_{whereField}
        ///   SET {setField} = $_s_{setField}
        ///   RETURN AFTER;
        /// </code>
        /// </summary>
        public SurrealQuery Set(string field, object? value)
        {
            if (_whereField is null)
                throw new InvalidOperationException(
                    "UpdateOnly requires a .Where(...) clause before .Set(...). " +
                    "The conditional-state-transition primitive is unsafe without one — " +
                    "use SurrealQuery.Of(\"UPDATE ...\") if you genuinely want an " +
                    "unconditional update (and accept that it bypasses the G2 contract).");
            SurrealQuery.ValidateFieldPath(field, nameof(field));

            // Note: emitted body has NO trailing semicolon — SurrealQuery.Of
            // would reject it. Combine() adds terminators when composing.
            //
            // 2026-06: SurrealDB v2.x deprecated `type::thing(table, id)` in
            // favour of `type::record(table, id)`. The runtime is the
            // user-managed `surreal` 2.x container; the parser rejects
            // type::thing with a "did you maybe mean `type::record`" hint.
            // Same change applied across RelateBuilder + every store-level
            // raw SurrealQL helper.
            // SurrealDB 3.x requires SET before WHERE (UPDATE … SET … WHERE …);
            // the legacy WHERE…SET order fails with "Unexpected token `SET`".
            var sql =
                "UPDATE type::record($_t, $_id) " +
                "SET " + field + " = $_s_" + field + " " +
                "WHERE " + _whereField + " = $_w_" + _whereField + " " +
                "RETURN AFTER";

            var paramBag = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["_t"] = _table,
                ["_id"] = _id,
                ["_w_" + _whereField] = _whereValue,
                ["_s_" + field] = value,
            };

            return SurrealQuery.FromBuilder(sql, paramBag);
        }
    }
}
