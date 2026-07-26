// SPDX-License-Identifier: MIT

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Text;

namespace SurrealForge.Client.Query
{
    /// <summary>
    /// Builds a strongly typed, single-record conditional update. Predicates
    /// and assignment targets are expression translated; values remain bound
    /// parameters and use the same coercion rules as <see cref="SurrealWriter"/>.
    /// </summary>
    public sealed class TypedUpdateOnlyBuilder<T> where T : ISurrealRecord, new()
    {
        private readonly string _id;
        private readonly IReadOnlyList<PredicatePart> _predicates;
        private readonly IReadOnlyList<AssignmentPart> _assignments;

        internal TypedUpdateOnlyBuilder(string id)
            : this(SurrealWriter.MutationIdFor<T>(id), Array.Empty<PredicatePart>(), Array.Empty<AssignmentPart>())
        {
        }

        internal TypedUpdateOnlyBuilder(RecordId<T> id)
            : this(id.Id, Array.Empty<PredicatePart>(), Array.Empty<AssignmentPart>())
        {
        }

        private TypedUpdateOnlyBuilder(
            string id,
            IReadOnlyList<PredicatePart> predicates,
            IReadOnlyList<AssignmentPart> assignments)
        {
            _id = id;
            _predicates = predicates;
            _assignments = assignments;
        }

        /// <summary>
        /// Add a typed predicate. Multiple calls are combined with <c>AND</c>;
        /// use a compound expression for grouped <c>OR</c> conditions.
        /// </summary>
        public TypedUpdateOnlyBuilder<T> Where(Expression<Func<T, bool>> predicate)
        {
            if (predicate == null) throw new ArgumentNullException(nameof(predicate));
            var renamed = TypedPredicateCompiler<T>.Compile(
                predicate,
                "_w" + _predicates.Count + "_");
            var predicates = new List<PredicatePart>(_predicates)
            {
                new PredicatePart(renamed.Sql, renamed.Parameters),
            };
            return new TypedUpdateOnlyBuilder<T>(_id, predicates, _assignments);
        }

        /// <summary>
        /// Assign a value to a typed field. The generic value parameter ties the
        /// selected property and assigned value together at compile time.
        /// </summary>
        public TypedUpdateOnlyBuilder<T> Set<TValue>(
            Expression<Func<T, TValue>> selector,
            TValue value)
        {
            if (value is null)
                throw new ArgumentNullException(
                    nameof(value),
                    "Set does not accept null; use Unset for an optional field.");
            var field = SurrealWriter.FieldFor(selector);
            EnsureNotAssigned(field.Column);
            var parameterName = "_s" + _assignments.Count + "_" + ParameterSafe(field.Column);
            var assignments = new List<AssignmentPart>(_assignments)
            {
                new AssignmentPart(
                    field.Column,
                    SurrealWriter.BoundValueExpression(field, parameterName),
                    parameterName,
                    SurrealWriter.NormalizeBoundValue(value)),
            };
            return new TypedUpdateOnlyBuilder<T>(_id, _predicates, assignments);
        }

        /// <summary>
        /// Remove an optional field by assigning SurrealDB's <c>NONE</c>
        /// sentinel. This is explicit so a C# null is never ambiguously emitted
        /// as JSON <c>null</c>.
        /// </summary>
        public TypedUpdateOnlyBuilder<T> Unset<TValue>(Expression<Func<T, TValue>> selector)
        {
            var field = SurrealWriter.FieldFor(selector);
            EnsureNotAssigned(field.Column);
            if (!field.IsOptional)
                throw new InvalidOperationException(
                    $"The required field '{field.Column}' cannot be unset.");
            var assignments = new List<AssignmentPart>(_assignments)
            {
                new AssignmentPart(field.Column, "NONE", null, null),
            };
            return new TypedUpdateOnlyBuilder<T>(_id, _predicates, assignments);
        }

        /// <summary>
        /// Emit <c>UPDATE ONLY type::record($_t, $_id) SET ... WHERE ...
        /// RETURN AFTER</c>. Building without a predicate or assignment throws.
        /// </summary>
        public SurrealQuery Build()
        {
            if (_predicates.Count == 0)
                throw new InvalidOperationException(
                    "A conditional update requires at least one Where predicate.");
            if (_assignments.Count == 0)
                throw new InvalidOperationException(
                    "A conditional update requires at least one Set or Unset assignment.");

            var sql = new StringBuilder("UPDATE ONLY type::record($_t, $_id) SET ");
            for (var i = 0; i < _assignments.Count; i++)
            {
                if (i > 0) sql.Append(", ");
                sql.Append(_assignments[i].Column)
                    .Append(" = ")
                    .Append(_assignments[i].ValueExpression);
            }
            sql.Append(" WHERE ");
            for (var i = 0; i < _predicates.Count; i++)
            {
                if (i > 0) sql.Append(" AND ");
                sql.Append('(').Append(_predicates[i].Sql).Append(')');
            }
            sql.Append(" RETURN AFTER");

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
            foreach (var assignment in _assignments)
            {
                if (assignment.ParameterName != null)
                    parameters.Add(assignment.ParameterName, assignment.Value);
            }

            return SurrealQuery.FromBuilder(sql.ToString(), parameters);
        }

        private void EnsureNotAssigned(string column)
        {
            foreach (var assignment in _assignments)
            {
                if (string.Equals(assignment.Column, column, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        $"The field '{column}' already has an assignment in this update.");
            }
        }

        private static string ParameterSafe(string value)
        {
            var chars = value.ToCharArray();
            for (var i = 0; i < chars.Length; i++)
            {
                if (!char.IsLetterOrDigit(chars[i]) && chars[i] != '_') chars[i] = '_';
            }
            return new string(chars);
        }

        private sealed class PredicatePart
        {
            public PredicatePart(string sql, IReadOnlyDictionary<string, object?> parameters)
            {
                Sql = sql;
                Parameters = parameters;
            }

            public string Sql { get; }
            public IReadOnlyDictionary<string, object?> Parameters { get; }
        }

        private sealed class AssignmentPart
        {
            public AssignmentPart(
                string column,
                string valueExpression,
                string? parameterName,
                object? value)
            {
                Column = column;
                ValueExpression = valueExpression;
                ParameterName = parameterName;
                Value = value;
            }

            public string Column { get; }
            public string ValueExpression { get; }
            public string? ParameterName { get; }
            public object? Value { get; }
        }
    }
}
