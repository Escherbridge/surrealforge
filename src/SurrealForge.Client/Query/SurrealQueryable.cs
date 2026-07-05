// SPDX-License-Identifier: UNLICENSED
// SurrealForge.Client.Query -- deferred IQueryable<T> surface over the
// eager SurrealQuery<T> builder (surreal-linq-graph-query Phase 2).
//
// The eager SurrealQuery<T> (Phase 1) already translates each clause through
// ExpressionTranslator. This layer adds LINQ deferral on top: Where / OrderBy
// / ThenBy / OrderByDescending / Skip / Take / Select accumulate as an
// expression tree and NO round-trip happens until a materializer
// (ToListAsync / FirstOrDefaultAsync / CountAsync / ...) folds the whole tree
// into ONE SurrealQuery<T> and dispatches it via the ISurrealExecutor.
//
// Deliberately NOT a general-purpose IQueryProvider: only the standard query
// operators the translator can emit are recognized. Anything else throws
// NotSupportedException with the same fall-back recipe as the translator --
// callers drop to SurrealQuery.Of(...) for the unsupported shape. No silent
// client-side evaluation.

#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace SurrealForge.Client.Query
{
    /// <summary>
    /// A deferred, composable <see cref="IQueryable{T}"/> over a SurrealDB
    /// table. Materialize via the async extension methods in
    /// <see cref="SurrealQueryableExtensions"/>
    /// (<c>ToListAsync</c>/<c>FirstOrDefaultAsync</c>/<c>CountAsync</c>/…).
    /// </summary>
    /// <typeparam name="T">A POCO implementing <see cref="ISurrealRecord"/>.</typeparam>
    public sealed class SurrealQueryable<T> : IQueryable<T>, IOrderedQueryable<T>
        where T : ISurrealRecord, new()
    {
        internal SurrealQueryable(SurrealQueryProvider provider, Expression expression)
        {
            Provider = provider ?? throw new ArgumentNullException(nameof(provider));
            Expression = expression ?? throw new ArgumentNullException(nameof(expression));
        }

        /// <summary>Root constructor: a bare <c>SELECT * FROM &lt;T&gt;</c> source.</summary>
        public SurrealQueryable(SurrealQueryProvider provider)
        {
            Provider = provider ?? throw new ArgumentNullException(nameof(provider));
            Expression = Expression.Constant(this);
        }

        internal SurrealQueryProvider SurrealProvider => (SurrealQueryProvider)Provider;

        public Type ElementType => typeof(T);
        public Expression Expression { get; }
        public IQueryProvider Provider { get; }

        /// <summary>
        /// Fold the accumulated expression tree into a single
        /// <see cref="SurrealQuery{T}"/>. Exposed so the async materializers
        /// (which carry the <c>ISurrealRecord,new()</c> constraint) can build
        /// + dispatch without going through the non-generic provider contract.
        /// </summary>
        internal SurrealQuery<T> BuildQuery()
            => SurrealQueryTranslator.Fold<T>(Expression);

        /// <summary>
        /// Synchronous enumeration materializes the whole query in one blocking
        /// round-trip. Prefer the async materializers; this exists so the type
        /// satisfies <see cref="IEnumerable{T}"/>.
        /// </summary>
        public IEnumerator<T> GetEnumerator()
        {
            var query = BuildQuery();
            var rows = SurrealProvider.Executor.QueryAsync<T>(query).GetAwaiter().GetResult();
            return rows.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>
    /// The deferred query provider. Wires a <see cref="SurrealQueryable{T}"/>
    /// to an <see cref="ISurrealExecutor"/> and rebuilds queryables as the
    /// standard operators chain. Actual SurrealQL translation is deferred to
    /// <see cref="SurrealQueryTranslator"/> at materialization.
    /// </summary>
    public sealed class SurrealQueryProvider : IQueryProvider
    {
        private readonly ISurrealExecutor _executor;

        public SurrealQueryProvider(ISurrealExecutor executor)
        {
            _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        }

        internal ISurrealExecutor Executor => _executor;

        public IQueryable CreateQuery(Expression expression)
        {
            var elementType = GetElementType(expression.Type);
            var queryableType = typeof(SurrealQueryable<>).MakeGenericType(elementType);
            return (IQueryable)Activator.CreateInstance(
                queryableType,
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                args: new object[] { this, expression },
                culture: null)!;
        }

        public IQueryable<TElement> CreateQuery<TElement>(Expression expression)
            => (IQueryable<TElement>)CreateQuery(expression);

        public object? Execute(Expression expression) => Execute<object>(expression);

        public TResult Execute<TResult>(Expression expression)
        {
            // IQueryProvider-contract synchronous fallback. The async
            // materializers are the supported path; this throws to steer
            // callers there rather than silently blocking on an arbitrary
            // result shape.
            throw new NotSupportedException(
                "Synchronous IQueryProvider.Execute is not supported by SurrealQueryProvider. " +
                "Use the async materializers (ToListAsync / FirstOrDefaultAsync / CountAsync / AnyAsync) " +
                "or enumerate the SurrealQueryable<T> directly.");
        }

        private static Type GetElementType(Type seqType)
        {
            if (seqType.IsGenericType)
            {
                foreach (var arg in seqType.GetGenericArguments())
                    return arg;
            }
            return seqType;
        }
    }
}
