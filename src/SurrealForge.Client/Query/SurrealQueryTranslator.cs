// SPDX-License-Identifier: UNLICENSED
// SurrealForge.Client.Query -- folds a deferred IQueryable expression tree
// (Where/OrderBy/ThenBy/OrderByDescending/Skip/Take/Select chain) into ONE
// eager SurrealQuery<T>. The leaf of the chain is the SurrealQueryable<T>
// source constant (SELECT * FROM <T>); each enclosing Queryable.* call appends
// its clause to the running SurrealQuery<T>.
//
// Only the standard operators the underlying ExpressionTranslator can emit are
// recognized. Anything else throws NotSupportedException with the fall-back
// recipe -- never a silent client-side evaluation.

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;

namespace SurrealForge.Client.Query
{
    internal static class SurrealQueryTranslator
    {
        /// <summary>
        /// Fold the expression tree rooted at <paramref name="expression"/>
        /// into a single <see cref="SurrealQuery{T}"/>.
        /// </summary>
        public static SurrealQuery<T> Fold<T>(Expression expression)
            where T : ISurrealRecord, new()
        {
            // Unwind the method-call chain outermost->innermost so we can apply
            // clauses in source order (innermost first).
            var calls = new Stack<MethodCallExpression>();
            var node = expression;
            while (node is MethodCallExpression mce)
            {
                calls.Push(mce);
                node = mce.Arguments[0];
            }

            // The chain bottoms out at the source constant (the root
            // SurrealQueryable<T>). Start from its SELECT * builder.
            var query = SurrealQuery<T>.From();

            while (calls.Count > 0)
            {
                var call = calls.Pop();
                query = Apply(query, call);
            }
            return query;
        }

        private static SurrealQuery<T> Apply<T>(SurrealQuery<T> query, MethodCallExpression call)
            where T : ISurrealRecord, new()
        {
            // Only Queryable/Enumerable standard operators are routed; the
            // method name + arity drive the dispatch.
            switch (call.Method.Name)
            {
                case "Where":
                    return query.Where(UnwrapPredicate<T>(call.Arguments[1]));

                case "OrderBy":
                    return query.OrderBy(UnwrapSelector<T>(call.Arguments[1]));
                case "OrderByDescending":
                    return query.OrderByDescending(UnwrapSelector<T>(call.Arguments[1]));
                case "ThenBy":
                    return query.ThenBy(UnwrapSelector<T>(call.Arguments[1]));
                case "ThenByDescending":
                    // SurrealQuery<T> spells each sort key as its own ORDER BY;
                    // descending secondary keys reuse OrderByDescending's emit.
                    return query.OrderByDescending(UnwrapSelector<T>(call.Arguments[1]));

                case "Take":
                    return query.Limit(EvalInt(call.Arguments[1]));
                case "Skip":
                    return query.Start(EvalInt(call.Arguments[1]));

                case "Select":          // LINQ Queryable.Select (same-T column subset)
                case "SelectFields":    // SurrealQueryableExtensions.SelectFields
                    return ApplySelect(query, call);

                default:
                    throw NotSupported(call.Method.Name, call);
            }
        }

        private static SurrealQuery<T> ApplySelect<T>(SurrealQuery<T> query, MethodCallExpression call)
            where T : ISurrealRecord, new()
        {
            // Select(x => new { x.A, x.B }) / Select(x => x.A). The typed
            // builder's Select rewrites SELECT * into the projected field list.
            var lambda = UnwrapLambda(call.Arguments[1]);
            // SurrealQuery<T>.Select is generic on the projection result type;
            // dispatch through the typed builder which re-uses
            // ExpressionTranslator.TranslateProjection.
            var resultType = lambda.ReturnType;
            var selectMethod = typeof(SurrealQuery<T>)
                .GetMethod(nameof(SurrealQuery<T>.Select))!
                .MakeGenericMethod(resultType);
            return (SurrealQuery<T>)selectMethod.Invoke(query, new object[] { lambda })!;
        }

        // ─── operand unwrapping ─────────────────────────────────────────────

        private static Expression<Func<T, bool>> UnwrapPredicate<T>(Expression arg)
        {
            var lambda = UnwrapLambda(arg);
            if (lambda is Expression<Func<T, bool>> typed) return typed;
            throw NotSupported("Where", arg);
        }

        private static Expression<Func<T, object>> UnwrapSelector<T>(Expression arg)
        {
            var lambda = UnwrapLambda(arg);
            // OrderBy selectors arrive as Expression<Func<T,TKey>>; the typed
            // builder takes Expression<Func<T,object>>. Re-wrap the body with a
            // Convert(...) to object so the member-path translator (which
            // already strips Convert) sees the same column.
            if (lambda is Expression<Func<T, object>> already) return already;
            var param = lambda.Parameters[0];
            var body = lambda.Body.Type.IsValueType
                ? (Expression)Expression.Convert(lambda.Body, typeof(object))
                : lambda.Body;
            return Expression.Lambda<Func<T, object>>(body, param);
        }

        private static LambdaExpression UnwrapLambda(Expression arg)
        {
            // Quote-wrapped lambdas (the common Queryable.* shape) unwrap once.
            while (arg is UnaryExpression u && u.NodeType == ExpressionType.Quote)
                arg = u.Operand;
            if (arg is LambdaExpression lambda) return lambda;
            throw NotSupported("lambda operand", arg);
        }

        private static int EvalInt(Expression arg)
        {
            if (arg is ConstantExpression { Value: int n }) return n;
            // Closure-captured count.
            var value = Expression.Lambda(arg).Compile().DynamicInvoke();
            if (value is int boxed) return boxed;
            throw NotSupported("Take/Skip count", arg);
        }

        private static NotSupportedException NotSupported(string context, Expression node)
            => new NotSupportedException(
                "The deferred SurrealQueryable does not support this operator (" + context + "). " +
                "Fall back to SurrealQuery.Of(...) / SurrealQuery<T>.From() for this query. " +
                "Node: " + node.NodeType + " (" + node.GetType().Name + ").");
    }
}
