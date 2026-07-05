using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SurrealForge.Client.Query
{
    /// <summary>
    /// Executes <see cref="SurrealQuery"/> instances against a SurrealDB
    /// instance.
    ///
    /// There is intentionally NO overload that accepts a raw <c>string</c>.
    /// All queries must go through <see cref="SurrealQuery.Of"/> (or one of
    /// the typed factories) so the parameterization contract is enforced at
    /// every call site.
    ///
    /// Implementations must:
    /// <list type="bullet">
    ///   <item>Call <see cref="SurrealQuery.Validate"/> (strict) before
    ///         dispatch.</item>
    ///   <item>Never construct or accept interpolated SQL strings.</item>
    ///   <item>For <see cref="ExecuteAsync"/>: surface per-statement results
    ///         via <see cref="SurrealResponse"/> instead of collapsing onto
    ///         the first statement (closes code-review C5).</item>
    /// </list>
    ///
    /// <para>
    /// The three methods differ in how they fold the response:
    /// <list type="bullet">
    ///   <item><see cref="QueryAsync{T}"/> — single-statement; returns the
    ///         first statement's deserialized values as a list.</item>
    ///   <item><see cref="QuerySingleAsync{T}"/> — single-statement; asserts
    ///         the result is zero-or-one row and returns the single row or
    ///         <c>null</c>.</item>
    ///   <item><see cref="ExecuteAsync"/> — single OR multi-statement;
    ///         returns the full <see cref="SurrealResponse"/> so callers can
    ///         address each statement's
    ///         <see cref="SurrealStatementResultExtensions.AffectedCount"/>,
    ///         <see cref="SurrealStatementResult.GetValues{T}"/>, or
    ///         <see cref="SurrealStatementResultExtensions.EnsureSingleAffected{T}"/>
    ///         independently. The legacy <c>int Count</c> return shape is
    ///         intentionally removed — it could not distinguish per-statement
    ///         counts from the aggregate (closes code-review C5).</item>
    /// </list>
    /// </para>
    /// </summary>
    public interface ISurrealExecutor
    {
        /// <summary>
        /// Executes a single-statement SELECT-style query and returns all
        /// result rows deserialized as <typeparamref name="T"/>.
        /// </summary>
        Task<IReadOnlyList<T>> QueryAsync<T>(SurrealQuery query, CancellationToken ct = default);

        /// <summary>
        /// Executes a single-statement SELECT-style query and returns the
        /// single result row, or <c>null</c> when no rows match.  Throws if
        /// the statement returns more than one row.
        /// </summary>
        Task<T?> QuerySingleAsync<T>(SurrealQuery query, CancellationToken ct = default)
            where T : class;

        /// <summary>
        /// Executes a statement (single OR multi) and returns the full
        /// <see cref="SurrealResponse"/> so each statement's result is
        /// individually addressable.  This is the only method that supports
        /// queries built via <see cref="SurrealQuery.Combine"/>.
        ///
        /// Callers asserting an exact affected-row count use
        /// <see cref="SurrealStatementResultExtensions.EnsureSingleAffected{T}"/> on the
        /// per-statement result rather than relying on an aggregate count.
        /// </summary>
        Task<SurrealResponse> ExecuteAsync(SurrealQuery query, CancellationToken ct = default);
    }
}
