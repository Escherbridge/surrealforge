// SPDX-License-Identifier: MIT

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Text;

namespace SurrealForge.Client.Query
{
    /// <summary>
    /// Builds a strongly typed conditional delete addressed to one record.
    /// A predicate is mandatory and the deleted row is returned for reliable
    /// affected-count inspection.
    /// </summary>
    public sealed class TypedDeleteOnlyBuilder<T> where T : ISurrealRecord, new()
    {
        private readonly string _id;
        private readonly IReadOnlyList<ExpressionTranslator.TranslationResult> _predicates;

        internal TypedDeleteOnlyBuilder(string id)
            : this(SurrealWriter.MutationIdFor<T>(id),
                Array.Empty<ExpressionTranslator.TranslationResult>())
        {
        }

        internal TypedDeleteOnlyBuilder(RecordId<T> id)
            : this(id.Id, Array.Empty<ExpressionTranslator.TranslationResult>())
        {
        }

        private TypedDeleteOnlyBuilder(
            string id,
            IReadOnlyList<ExpressionTranslator.TranslationResult> predicates)
        {
            _id = id;
            _predicates = predicates;
        }

        /// <summary>Add a typed predicate; multiple calls combine with <c>AND</c>.</summary>
        public TypedDeleteOnlyBuilder<T> Where(Expression<Func<T, bool>> predicate)
        {
            if (predicate == null) throw new ArgumentNullException(nameof(predicate));
            var predicates = new List<ExpressionTranslator.TranslationResult>(_predicates)
            {
                TypedPredicateCompiler<T>.Compile(
                    predicate,
                    "_w" + _predicates.Count + "_"),
            };
            return new TypedDeleteOnlyBuilder<T>(_id, predicates);
        }

        /// <summary>Emit the predicate-required conditional delete.</summary>
        public SurrealQuery Build()
        {
            if (_predicates.Count == 0)
                throw new InvalidOperationException(
                    "A conditional delete requires at least one Where predicate.");

            var sql = new StringBuilder("DELETE ONLY type::record($_t, $_id) WHERE ");
            for (var i = 0; i < _predicates.Count; i++)
            {
                if (i > 0) sql.Append(" AND ");
                sql.Append('(').Append(_predicates[i].Sql).Append(')');
            }
            sql.Append(" RETURN BEFORE");

            var parameters = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["_t"] = RecordId<T>.SchemaNameOf<T>(),
                ["_id"] = _id,
            };
            foreach (var predicate in _predicates)
            {
                foreach (var parameter in predicate.Parameters)
                    parameters.Add(parameter.Key, parameter.Value);
            }
            return SurrealQuery.FromBuilder(sql.ToString(), parameters);
        }
    }
}
