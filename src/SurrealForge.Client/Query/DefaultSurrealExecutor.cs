using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SurrealForge.Client.Connection;

namespace SurrealForge.Client.Query
{
    /// <summary>
    /// Default <see cref="ISurrealExecutor"/> implementation backed by a single
    /// <see cref="ISurrealConnection"/>. Stateless apart from the connection
    /// reference, so it is safe to register as a singleton in DI containers
    /// that pre-scope the connection (matching what
    /// <c>SurrealDbServiceCollectionExtensions.AddSurrealForge</c> does).
    ///
    /// <para>
    /// Every entry point validates the supplied <see cref="SurrealQuery"/>
    /// (strict mode) before dispatch — extra-and-missing parameter typos are
    /// caught at the executor boundary rather than producing opaque server
    /// errors. Per-statement results are surfaced via
    /// <see cref="SurrealResponse"/> (HIGH#4 + code-review C5).
    /// </para>
    /// </summary>
    public sealed class DefaultSurrealExecutor : ISurrealExecutor
    {
        private readonly ISurrealConnection _connection;

        public DefaultSurrealExecutor(ISurrealConnection connection)
        {
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        }

        /// <inheritdoc/>
        public async Task<IReadOnlyList<T>> QueryAsync<T>(
            SurrealQuery query, CancellationToken ct = default)
        {
            if (query is null) throw new ArgumentNullException(nameof(query));
            if (query.IsMultiStatement)
                throw new ArgumentException(
                    "QueryAsync requires a single-statement SurrealQuery. " +
                    "Use ExecuteAsync for multi-statement bodies built via SurrealQuery.Combine.",
                    nameof(query));

            var resp = await DispatchAsync(query, ct).ConfigureAwait(false);
            resp.EnsureAllOk();
            return resp[0].GetValues<T>();
        }

        /// <inheritdoc/>
        public async Task<T?> QuerySingleAsync<T>(
            SurrealQuery query, CancellationToken ct = default)
            where T : class
        {
            if (query is null) throw new ArgumentNullException(nameof(query));
            if (query.IsMultiStatement)
                throw new ArgumentException(
                    "QuerySingleAsync requires a single-statement SurrealQuery. " +
                    "Use ExecuteAsync for multi-statement bodies built via SurrealQuery.Combine.",
                    nameof(query));

            var resp = await DispatchAsync(query, ct).ConfigureAwait(false);
            resp.EnsureAllOk();
            var values = resp[0].GetValues<T>();
            if (values.Count == 0) return null;
            if (values.Count > 1)
                throw new InvalidOperationException(
                    "QuerySingleAsync expected zero or one result but the statement returned " +
                    values.Count + " rows. Add a LIMIT 1 clause or narrow the predicate.");
            return values[0];
        }

        /// <inheritdoc/>
        public async Task<SurrealResponse> ExecuteAsync(
            SurrealQuery query, CancellationToken ct = default)
        {
            if (query is null) throw new ArgumentNullException(nameof(query));

            // No IsMultiStatement guard here — ExecuteAsync is the sanctioned
            // surface for Combine(...) bodies. Single statements work too.
            return await DispatchAsync(query, ct).ConfigureAwait(false);
        }

        private async Task<SurrealResponse> DispatchAsync(SurrealQuery query, CancellationToken ct)
        {
            query.Validate(strict: true);
            // Cache the built SQL text so the same string can be stamped on the
            // exception without rebuilding (and semantics of ExecuteRawAsync are unchanged).
            var sqlText = query.Build();
            try
            {
                // SurrealQuery.Params is IReadOnlyDictionary<string, object?>; the
                // HTTP transport accepts anything object-shaped, so we hand it
                // straight through. Parameter binding happens server-side via the
                // ?$name=<json> query-string the connection builds.
                return await _connection.ExecuteRawAsync(sqlText, query.Params, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ex.Data["SurrealStatement"] = sqlText;
                ex.Data["SurrealParams"]    = query.Params;
                throw;
            }
        }
    }
}
