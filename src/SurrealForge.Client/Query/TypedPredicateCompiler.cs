// SPDX-License-Identifier: MIT

#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;

namespace SurrealForge.Client.Query
{
    internal static class TypedPredicateCompiler<T> where T : ISurrealRecord, new()
    {
        internal static ExpressionTranslator.TranslationResult Compile(
            Expression<Func<T, bool>> predicate,
            string prefix)
        {
            PersistedMemberValidator.Validate(predicate);
            var translation = ExpressionTranslator.Translate(predicate);
            var sql = translation.Sql;
            var parameters = new Dictionary<string, object?>(System.StringComparer.Ordinal);
            foreach (var parameter in translation.Parameters.OrderByDescending(p => p.Key.Length))
            {
                var renamed = prefix + ParameterSafe(parameter.Key);
                var field = SurrealWriter.FieldForPredicateParameter<T>(parameter.Key);
                var isCollection = parameter.Value is IEnumerable && parameter.Value is not string;
                var valueExpression = field is null || isCollection
                    ? "$" + renamed
                    : SurrealWriter.BoundValueExpression(field, renamed);
                sql = sql.Replace("$" + parameter.Key, valueExpression);
                parameters.Add(
                    renamed,
                    SurrealWriter.NormalizePredicateBoundValue(field, parameter.Value));
            }
            return new ExpressionTranslator.TranslationResult(sql, parameters);
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

        private sealed class PersistedMemberValidator : ExpressionVisitor
        {
            private readonly ParameterExpression _parameter;

            private PersistedMemberValidator(ParameterExpression parameter)
            {
                _parameter = parameter;
            }

            internal static void Validate(Expression<Func<T, bool>> predicate)
                => new PersistedMemberValidator(predicate.Parameters[0]).Visit(predicate.Body);

            protected override Expression VisitMember(MemberExpression node)
            {
                if (IsDirectRecordMember(node))
                    SurrealWriter.FieldForPredicateMember<T>(node.Member);
                return base.VisitMember(node);
            }

            private bool IsDirectRecordMember(MemberExpression node)
            {
                Expression? expression = node.Expression;
                while (expression is UnaryExpression unary
                    && (unary.NodeType == ExpressionType.Convert || unary.NodeType == ExpressionType.ConvertChecked))
                {
                    expression = unary.Operand;
                }
                return expression == _parameter;
            }
        }
    }
}
