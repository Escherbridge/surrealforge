// SPDX-License-Identifier: UNLICENSED
// SurrealForge.Client.Query -- typed companion to the existing untyped
// SurrealQuery. The shape mirrors the untyped builder one-to-one but every
// fluent step takes a typed expression that ExpressionTranslator converts
// to SurrealQL.
//
// Implementation: the typed builder DELEGATES every clause to the untyped
// SurrealQuery -- no shadow SQL composition, no duplicate immutable-clone
// machinery. The typed shape is a thin Expression-to-string adapter sitting
// on top of the untyped builder.
//
// Conversion: SurrealQuery<T> widens implicitly to SurrealQuery so existing
// executor paths consume typed queries unchanged. The explicit `.AsUntyped()`
// method exposes the underlying untyped builder for cases where the call site
// wants to drop into stringly-typed escape hatches mid-chain.

#nullable enable

using System;
using System.Linq.Expressions;

namespace SurrealForge.Client.Query
{
    /// <summary>
    /// Strongly-typed SurrealQL query builder. Every step delegates to the
    /// untyped <see cref="SurrealQuery"/> after translating its
    /// <see cref="Expression{TDelegate}"/> argument through
    /// <see cref="ExpressionTranslator"/>.
    /// </summary>
    /// <typeparam name="T">A generated POCO implementing <see cref="ISurrealRecord"/>.</typeparam>
    public sealed class SurrealQuery<T> where T : ISurrealRecord, new()
    {
        private readonly SurrealQuery _inner;

        private SurrealQuery(SurrealQuery inner)
        {
            _inner = inner;
        }

        /// <summary>
        /// Begin a new typed query against the SurrealDB table named by
        /// <typeparamref name="T"/>'s <see cref="ISurrealRecord.SchemaName"/>.
        /// Emits <c>SELECT * FROM &lt;schema&gt;</c>.
        /// </summary>
        public static SurrealQuery<T> From()
        {
            var schema = RecordId<T>.SchemaNameOf<T>();
            return new SurrealQuery<T>(SurrealQuery.SelectAll(schema));
        }

        /// <summary>
        /// Begin a typed query from a pre-built untyped query. Lets callers
        /// stack typed fluent steps on top of a hand-authored
        /// <see cref="SurrealQuery.Of"/> body when the typed entry point's
        /// <c>SELECT *</c> isn't appropriate.
        /// </summary>
        public static SurrealQuery<T> FromUntyped(SurrealQuery untyped)
        {
            if (untyped == null) throw new ArgumentNullException(nameof(untyped));
            return new SurrealQuery<T>(untyped);
        }

        /// <summary>
        /// Anchor the query to a single record by id:
        /// <c>SELECT * FROM type::record($_t, $_id)</c>. The id is bound as a
        /// parameter; the table comes from <typeparamref name="T"/>'s schema.
        /// A leading <c>table:</c> prefix on <paramref name="id"/> is stripped
        /// so a link-form or bare id both bind correctly. This is the start of
        /// a graph <see cref="Traverse{TTarget}"/>.
        /// </summary>
        public static SurrealQuery<T> Key(string id)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentException("Key id must not be empty.", nameof(id));
            var table = RecordId<T>.SchemaNameOf<T>();
            int colon = id.IndexOf(':');
            var bareId = colon >= 0 ? id.Substring(colon + 1) : id;
            var inner = SurrealQuery
                .Of("SELECT * FROM type::record($_t, $_id)")
                .WithParam("_t", table)
                .WithParam("_id", bareId);
            return new SurrealQuery<T>(inner);
        }

        /// <summary>
        /// Emit a typed graph traversal from this query's anchored record(s).
        /// The <paramref name="path"/> lambda builds an arrow path off the
        /// modeled <c>[RelateEdge]</c> POCOs
        /// (<c>r =&gt; r.Out&lt;ForkedFrom&gt;().To&lt;QuestRun&gt;()</c> →
        /// <c>-&gt;forked_from-&gt;quest_run</c>); the result is a
        /// <see cref="SurrealQuery{TTarget}"/> deserializing the traversed
        /// target records:
        /// <c>SELECT VALUE &lt;path&gt; FROM &lt;anchor&gt;</c>.
        /// Anchor this with <see cref="Key"/> (single record) or
        /// <see cref="Where"/> (a filtered set) first.
        /// </summary>
        public SurrealQuery<TTarget> Traverse<TTarget>(Func<GraphRoot<T>, GraphPath> path)
            where TTarget : ISurrealRecord, new()
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            var arrow = path(new GraphRoot<T>());

            // Pull the anchoring FROM <record> off the inner query and re-author
            // as `SELECT VALUE <arrow>.* FROM ONLY <anchor>`:
            //   * VALUE      -> unwrap each row to the traversed value directly
            //   * <arrow>.*  -> dereference the edge target to its full record
            //                   body (without `.*` SurrealDB yields bare record
            //                   ids, which won't deserialize into TTarget)
            //   * FROM ONLY  -> the anchor is a single record (Key), so the
            //                   result is the flat target array, not an array
            //                   of per-row arrays.
            var sql = _inner.Sql;
            const string selectStar = "SELECT * FROM ";
            if (!sql.StartsWith(selectStar, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "Traverse() must anchor on a single record via Key(id) before traversing.");
            var anchor = sql.Substring(selectStar.Length);
            var newSql = "SELECT VALUE " + arrow.Path + ".* FROM ONLY " + anchor;
            var rebuilt = SurrealQuery.Of(newSql).WithParams(_inner.Params);
            return SurrealQuery<TTarget>.FromUntyped(rebuilt);
        }

        /// <summary>
        /// Relationship-based computation (decision D6): emit
        /// <c>SELECT VALUE count(&lt;arrow&gt;) FROM ONLY &lt;anchor&gt;</c> — the
        /// number of records reachable from the anchored record over the graph
        /// path (e.g. <c>Graph.Count of -&gt;member-&gt;</c>). Returns an untyped
        /// query whose single scalar result is the count; dispatch with
        /// <see cref="ISurrealExecutor.QueryAsync{T}"/> as <c>long</c>. Anchor
        /// with <see cref="Key"/> first.
        /// </summary>
        public SurrealQuery CountVia(Func<GraphRoot<T>, GraphPath> path)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            var arrow = path(new GraphRoot<T>());
            var sql = _inner.Sql;
            const string selectStar = "SELECT * FROM ";
            if (!sql.StartsWith(selectStar, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "CountVia() must anchor on a single record via Key(id) before counting.");
            var anchor = sql.Substring(selectStar.Length);
            var newSql = "SELECT VALUE count(" + arrow.Path + ") FROM ONLY " + anchor;
            return SurrealQuery.Of(newSql).WithParams(_inner.Params);
        }

        /// <summary>
        /// Add a WHERE clause (or chain AND) derived from the supplied
        /// expression. Field references on <typeparamref name="T"/> resolve
        /// via <see cref="System.Text.Json.Serialization.JsonPropertyNameAttribute"/>
        /// on the property; constants and closure-captured values bind to
        /// auto-named parameters whose key matches the column name.
        /// </summary>
        public SurrealQuery<T> Where(Expression<Func<T, bool>> predicate)
        {
            if (predicate == null) throw new ArgumentNullException(nameof(predicate));
            var translated = ExpressionTranslator.Translate(predicate);
            return new SurrealQuery<T>(_inner.Where(translated.Sql, translated.Parameters));
        }

        /// <summary>
        /// Append an <c>ORDER BY field ASC</c> clause for the supplied member.
        /// </summary>
        public SurrealQuery<T> OrderBy(Expression<Func<T, object>> selector)
        {
            var col = ExpressionTranslator.TranslateMemberPath(selector);
            return new SurrealQuery<T>(_inner.OrderBy(col, OrderDirection.Asc));
        }

        /// <summary>
        /// Append an <c>ORDER BY field DESC</c> clause for the supplied member.
        /// </summary>
        public SurrealQuery<T> OrderByDescending(Expression<Func<T, object>> selector)
        {
            var col = ExpressionTranslator.TranslateMemberPath(selector);
            return new SurrealQuery<T>(_inner.OrderBy(col, OrderDirection.Desc));
        }

        /// <summary>
        /// Append a secondary ORDER BY clause. SurrealQL syntax appends
        /// successive comma-separated sort keys, but the underlying
        /// untyped builder spells each one as a separate <c>ORDER BY</c>;
        /// SurrealDB accepts either form.
        /// </summary>
        public SurrealQuery<T> ThenBy(Expression<Func<T, object>> selector)
        {
            var col = ExpressionTranslator.TranslateMemberPath(selector);
            return new SurrealQuery<T>(_inner.OrderBy(col, OrderDirection.Asc));
        }

        /// <summary>Append a <c>LIMIT n</c> clause.</summary>
        public SurrealQuery<T> Limit(int n) => new SurrealQuery<T>(_inner.Limit(n));

        /// <summary>Append a <c>START n</c> clause.</summary>
        public SurrealQuery<T> Start(int n) => new SurrealQuery<T>(_inner.Start(n));

        /// <summary>
        /// Append a <c>FETCH path</c> clause for graph traversal. The path
        /// resolves from the typed member selector via
        /// <see cref="System.Text.Json.Serialization.JsonPropertyNameAttribute"/>.
        /// </summary>
        public SurrealQuery<T> Fetch(Expression<Func<T, object>> selector)
        {
            var col = ExpressionTranslator.TranslateMemberPath(selector);
            return new SurrealQuery<T>(_inner.Fetch(col));
        }

        /// <summary>
        /// Project the query to a different shape, rewriting the inner
        /// SurrealQL from <c>SELECT *</c> to an explicit field list derived
        /// from an anonymous-object initializer
        /// (<c>x =&gt; new { x.Id, x.Status }</c>).
        /// </summary>
        public SurrealQuery<T> Select<TResult>(Expression<Func<T, TResult>> projection)
        {
            var fields = ExpressionTranslator.TranslateProjection(projection);
            var schema = RecordId<T>.SchemaNameOf<T>();
            var sql = _inner.Sql;
            // Rewrite the leading SELECT * with the projected field list. The
            // untyped builder's immutable shape forces a re-author through Of()
            // when the SELECT clause itself changes; chain any pre-existing
            // params / clauses by re-applying them.
            const string SelectStar = "SELECT * FROM ";
            if (sql.StartsWith(SelectStar, StringComparison.Ordinal))
            {
                var rest = sql.Substring(SelectStar.Length);
                var newSql = "SELECT " + fields + " FROM " + rest;
                var rebuilt = SurrealQuery.Of(newSql).WithParams(_inner.Params);
                return new SurrealQuery<T>(rebuilt);
            }
            throw new InvalidOperationException(
                "SurrealQuery<T>.Select() can only project a base SELECT * query. " +
                "Compose projections on the typed builder before adding WHERE / ORDER BY clauses.");
        }

        /// <summary>
        /// Expose the underlying untyped <see cref="SurrealQuery"/> so callers
        /// can drop into stringly-typed escape hatches when the expression
        /// translator rejects an unsupported predicate. The escape-hatch path
        /// is intentionally explicit -- no silent feature fallthrough.
        /// </summary>
        public SurrealQuery AsUntyped() => _inner;

        /// <summary>
        /// Implicit widening to the untyped <see cref="SurrealQuery"/>. The
        /// executor layer accepts untyped queries only; the typed wrapper is
        /// a compile-time pin that vanishes at the I/O boundary.
        /// </summary>
        public static implicit operator SurrealQuery(SurrealQuery<T> typed) => typed._inner;

        public override string ToString() => _inner.ToString();
    }
}
