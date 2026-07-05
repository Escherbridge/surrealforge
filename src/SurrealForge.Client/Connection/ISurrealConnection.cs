using System;
using System.Threading;
using System.Threading.Tasks;
using SurrealForge.Client.Transaction;

namespace SurrealForge.Client.Connection;

/// <summary>
/// The engine boundary. One implementation per transport (HTTP today,
/// WebSocket in sub-wave 1.5b). Consumers depend on this interface, never on
/// the concrete transport; this is the seam that the spec's G4 SDK-pin
/// originally protected, now owned by us instead of bought from upstream.
/// </summary>
public interface ISurrealConnection : IAsyncDisposable, IDisposable
{
    /// <summary>
    /// Switch namespace + database context for subsequent calls on this
    /// connection instance. Implementations must perform this via the
    /// SurrealDB <c>USE NS &lt;ns&gt; DB &lt;db&gt;</c> statement (HTTP) or
    /// the <c>use</c> RPC method (WebSocket).
    /// </summary>
    Task UseAsync(string ns, string db, CancellationToken ct = default);

    /// <summary>
    /// Execute a raw SurrealQL statement (or semicolon-separated group of
    /// statements) and return the multi-statement response. Parameters are
    /// bound by name via the SurrealDB <c>$param</c> syntax — see
    /// <see href="https://surrealdb.com/docs/surrealdb/integration/http">/sql
    /// docs</see> for the <c>vars</c> argument shape.
    /// </summary>
    /// <param name="sql">SurrealQL to execute. Must not be null/empty.</param>
    /// <param name="parameters">
    /// Optional name → value map. Values are serialized via
    /// <see cref="SurrealForge.Client.Json.SurrealJsonOptions.Default"/>.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task<SurrealResponse> ExecuteRawAsync(
        string sql,
        object? parameters = null,
        CancellationToken ct = default);

    /// <summary>
    /// Begin a SurrealDB transaction. The returned handle MUST be
    /// <c>await using</c>d. Statements executed while the handle is open are
    /// buffered and flushed as a single <c>BEGIN; ...; COMMIT;</c> request on
    /// <c>CommitAsync</c> — the only transaction shape SurrealDB 3.x's stateless
    /// HTTP transport supports. Disposing without committing discards the buffer
    /// (nothing was sent), so no server-side transaction can leak.
    /// </summary>
    Task<ISurrealTransaction> BeginTransactionAsync(CancellationToken ct = default);
}
