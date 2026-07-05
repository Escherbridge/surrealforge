using System;
using System.Threading;
using System.Threading.Tasks;

namespace SurrealForge.Client.Transaction;

/// <summary>
/// Explicit SurrealDB transaction handle. Constructed via
/// <see cref="Connection.ISurrealConnection.BeginTransactionAsync"/>. Statements
/// executed on the connection while the handle is open are buffered, then
/// flushed as one <c>BEGIN; ...; COMMIT;</c> request by <see cref="CommitAsync"/>
/// (SurrealDB 3.x scopes a transaction to a single HTTP request). Callers MUST
/// <c>await using</c> the handle: disposing without committing discards the
/// buffer — nothing reached the server — so there is no path that leaves a
/// transaction open after the using block exits.
/// </summary>
public interface ISurrealTransaction : IAsyncDisposable
{
    /// <summary>True iff <see cref="CommitAsync"/> has completed without error.</summary>
    bool IsCommitted { get; }

    /// <summary>True iff <see cref="DisposeAsync"/> has been entered.</summary>
    bool IsDisposed { get; }

    /// <summary>
    /// Flush the buffered statements as one <c>BEGIN; ...; COMMIT;</c> request.
    /// Idempotent: subsequent calls are no-ops. Throws if the connection faults
    /// or the server rejects the transaction (the buffered writes do not apply).
    /// </summary>
    Task CommitAsync(CancellationToken ct = default);
}
