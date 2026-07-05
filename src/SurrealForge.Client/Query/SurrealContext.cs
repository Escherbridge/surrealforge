// SPDX-License-Identifier: UNLICENSED
// SurrealForge.Client.Query -- DbContext-equivalent over SurrealDB
// (surreal-linq-graph-query Phase 3). Exposes typed deferred query sets
// (Set<T>() -> SurrealQueryable<T>) plus a lightweight unit-of-work:
// Add/Update/Remove/Attach register intent on the change tracker, and
// SaveChangesAsync flushes every pending write as ONE BEGIN..COMMIT
// transaction (decision D3) via the connection's buffering transaction.
//
// Change tracking is intentionally minimal (D4): an identity map keyed on
// [Id] + explicit state, NOT EF-style proxy/relationship fixup. The single
// round-trip transaction is the natural unit-of-work boundary SurrealDB's
// stateless HTTP transport already forces.

#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using SurrealForge.Client.Connection;

namespace SurrealForge.Client.Query
{
    /// <summary>
    /// A SurrealDB unit-of-work + typed query root. Construct with an
    /// <see cref="ISurrealConnection"/>; derive query sets via
    /// <see cref="Set{T}"/> and stage writes via
    /// <see cref="Add{T}"/>/<see cref="Update{T}"/>/<see cref="Remove{T}"/>,
    /// then <see cref="SaveChangesAsync"/>.
    /// </summary>
    public class SurrealContext
    {
        private readonly ISurrealConnection _connection;
        private readonly SurrealQueryProvider _provider;

        public SurrealContext(ISurrealConnection connection)
        {
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
            Executor = new DefaultSurrealExecutor(connection);
            _provider = new SurrealQueryProvider(Executor);
            ChangeTracker = new SurrealChangeTracker();
        }

        /// <summary>Constructor for tests/seams that already hold an executor.</summary>
        public SurrealContext(ISurrealConnection connection, ISurrealExecutor executor)
        {
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
            Executor = executor ?? throw new ArgumentNullException(nameof(executor));
            _provider = new SurrealQueryProvider(Executor);
            ChangeTracker = new SurrealChangeTracker();
        }

        /// <summary>The change tracker backing the unit-of-work.</summary>
        public SurrealChangeTracker ChangeTracker { get; }

        internal ISurrealExecutor Executor { get; }

        /// <summary>
        /// A deferred, composable query set for table <typeparamref name="T"/>
        /// (<c>SELECT * FROM &lt;table&gt;</c> root). Compose with LINQ +
        /// materialize with the async terminal operators.
        /// </summary>
        public SurrealQueryable<T> Set<T>() where T : ISurrealRecord, new()
            => new SurrealQueryable<T>(_provider);

        public SurrealEntityEntry Add<T>(T entity) where T : ISurrealRecord, new()
            => ChangeTracker.Add(entity);

        public SurrealEntityEntry Update<T>(T entity) where T : ISurrealRecord, new()
            => ChangeTracker.Update(entity);

        public SurrealEntityEntry Remove<T>(T entity) where T : ISurrealRecord, new()
            => ChangeTracker.Remove(entity);

        public SurrealEntityEntry Attach<T>(T entity) where T : ISurrealRecord, new()
            => ChangeTracker.Attach(entity);

        /// <summary>
        /// Flush all pending Added/Modified/Deleted entries as ONE
        /// BEGIN..COMMIT transaction. Returns the number of statements applied.
        /// On commit failure the buffered writes do not apply (all-or-nothing);
        /// the tracker is left intact so the caller can retry. On success the
        /// tracker is cleared.
        /// </summary>
        public async Task<int> SaveChangesAsync(CancellationToken ct = default)
        {
            var pending = new System.Collections.Generic.List<SurrealEntityEntry>();
            foreach (var e in ChangeTracker.Entries)
            {
                if (e.State != SurrealEntityState.Unchanged) pending.Add(e);
            }
            if (pending.Count == 0) return 0;

            await using var txn = await _connection.BeginTransactionAsync(ct).ConfigureAwait(false);

            // While the transaction is enlisted on the connection, each
            // ExecuteAsync is buffered (not sent) and flushed on commit.
            foreach (var entry in pending)
            {
                var stmt = BuildStatement(entry);
                await Executor.ExecuteAsync(stmt, ct).ConfigureAwait(false);
            }

            await txn.CommitAsync(ct).ConfigureAwait(false);
            ChangeTracker.Clear();
            return pending.Count;
        }

        private static SurrealQuery BuildStatement(SurrealEntityEntry entry)
        {
            var bareId = BareId(entry.Id);
            switch (entry.State)
            {
                case SurrealEntityState.Added:
                    // Coercion-safe SET-based CREATE (SurrealWriter), not
                    // CONTENT $body — see SurrealWriter for the 3.x rationale.
                    return entry.CreateStatement();
                case SurrealEntityState.Modified:
                    return entry.UpsertStatement();
                case SurrealEntityState.Deleted:
                    return SurrealQuery
                        .Of("DELETE type::record($_t, $_id)")
                        .WithParam("_t", entry.Table)
                        .WithParam("_id", bareId);
                default:
                    throw new InvalidOperationException(
                        $"Unexpected entry state {entry.State} in SaveChanges flush.");
            }
        }

        /// <summary>
        /// Strip a leading <c>table:</c> prefix so the id binds to
        /// <c>type::record($_t, $_id)</c> as a bare key (the stores' convention).
        /// </summary>
        private static string BareId(string id)
        {
            int colon = id.IndexOf(':');
            return colon >= 0 ? id.Substring(colon + 1) : id;
        }
    }
}
