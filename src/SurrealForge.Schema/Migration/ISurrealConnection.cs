// SPDX-License-Identifier: UNLICENSED
// SurrealForge.Schema -- Minimal connection abstraction for the migration
// runner.
//
// Why a local interface? Phase 4 ships before A1/A2's full
// `SurrealForge.Client` API freezes (A1 is still bootstrapping
// `HttpSurrealConnection`). To avoid a circular hard dep, the schema package
// declares the *narrow* connection surface it needs (one `ExecuteAsync(sql)`
// method) and the CLI bridges SurrealForge.Client.ISurrealConnection
// to this interface in a thin adapter (`HttpConnectionAdapter` in
// Cli/HttpConnectionAdapter.cs).
//
// This also keeps the migration runner unit-testable with a Moq mock that
// implements only ExecuteAsync (no need to stand up the whole HTTP transport).

using System.Threading;
using System.Threading.Tasks;

namespace SurrealForge.Schema.Migration
{
    /// <summary>
    /// Narrow contract the migration runner depends on. One method, one
    /// purpose: send a raw SurQL string and get a server response.
    ///
    /// <para>Implementations:
    ///   - Adapter over <c>SurrealForge.Client.HttpSurrealConnection</c> (production).
    ///   - In-memory fake / Moq mock (tests).
    /// </para>
    /// </summary>
    public interface ISurrealConnection
    {
        /// <summary>
        /// Send a raw SurQL string. Should throw on transport / auth failure;
        /// statement-level errors are reported via <see cref="SurrealExecutionResult.Status"/>.
        /// </summary>
        Task<SurrealExecutionResult> ExecuteAsync(string surql, CancellationToken ct = default);

        /// <summary>Send SurQL without Surreal-NS/-DB scope headers (for namespace bootstrap).</summary>
        Task<SurrealExecutionResult> ExecuteUnscopedAsync(string surql, CancellationToken ct = default);
    }

    /// <summary>
    /// Per-call execution result. Carries enough information for the migration
    /// runner to decide success / abort, plus a free-form payload for diagnostics.
    /// </summary>
    public sealed class SurrealExecutionResult
    {
        /// <summary>"OK" on success; otherwise the server-reported status string.</summary>
        public string Status { get; }

        /// <summary>Optional error / detail string (populated on non-OK).</summary>
        public string? Detail { get; }

        /// <summary>Raw response body (for logging / dry-run diff).</summary>
        public string? RawBody { get; }

        public SurrealExecutionResult(string status, string? detail = null, string? rawBody = null)
        {
            Status = status ?? "OK";
            Detail = detail;
            RawBody = rawBody;
        }

        /// <summary>True iff Status == "OK".</summary>
        public bool IsOk => string.Equals(Status, "OK", System.StringComparison.OrdinalIgnoreCase);

        public static SurrealExecutionResult Ok(string? rawBody = null)
            => new SurrealExecutionResult("OK", null, rawBody);

        public static SurrealExecutionResult Error(string detail, string? rawBody = null)
            => new SurrealExecutionResult("ERR", detail, rawBody);
    }
}
