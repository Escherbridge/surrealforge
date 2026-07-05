using System;

namespace SurrealForge.Client;

/// <summary>
/// Configuration for a SurrealDB connection / pool. All fields are
/// intentionally JSON-config friendly (string / int / TimeSpan) so consumers
/// can populate from <c>appsettings.json</c> via
/// <c>services.Configure&lt;SurrealConnectionOptions&gt;(Configuration.GetSection("SurrealDb"))</c>.
/// </summary>
/// <remarks>
/// JSON-config defaults are the source-of-truth per the archaeological-persona
/// finding that the wave-1 compose file mis-set durability via env vars: this
/// options shape lives next to the engine boundary and is the only place
/// connection knobs are read.
/// </remarks>
public sealed class SurrealConnectionOptions
{
    /// <summary>
    /// Base HTTP endpoint, e.g. <c>http://localhost:8442</c>. The client
    /// appends <c>/sql</c>, <c>/health</c>, etc. as needed. Required.
    /// </summary>
    public string Endpoint { get; set; } = "http://localhost:8442";

    /// <summary>Namespace scope (the <c>NS</c> header). Required.</summary>
    public string Namespace { get; set; } = "test";

    /// <summary>Database scope (the <c>DB</c> header). Required.</summary>
    public string Database { get; set; } = "test";

    /// <summary>Basic-auth user. Optional — if both User and Password are
    /// empty, no Authorization header is sent (root anonymous dev mode).
    /// MEDIUM #M6: defaults are intentionally empty so that consumers who do
    /// not configure auth get the documented no-header behaviour rather than
    /// hard-coded root/root credentials being sent on the wire.</summary>
    public string User { get; set; } = string.Empty;

    /// <summary>Basic-auth password. See <see cref="User"/>.</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// Maximum simultaneous in-flight HTTP requests against a single
    /// connection pool. Default 32 matches the spec-stated default and
    /// SurrealDB single-node practical concurrency ceiling.
    /// </summary>
    public int MaxConnections { get; set; } = 32;

    /// <summary>Per-request HTTP timeout. Default 30s.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Maximum number of attempts (NOT retries — the first attempt counts) on
    /// transport failure. Default 2 = at most one retry. Lowered from 5 in
    /// HIGH#2 because:
    ///   * Non-idempotent statements (COMMIT / CREATE / UPDATE / DELETE) are
    ///     never retried regardless of this number — see
    ///     <c>HttpSurrealConnection.IsIdempotentSql</c>.
    ///   * For idempotent statements, hammering a struggling server with
    ///     exponentially-backed-off retries beyond ~2 attempts produces more
    ///     harm than help; the connection-pool layer above already serialises
    ///     concurrent requests.
    /// </summary>
    public int MaxRetries { get; set; } = 2;

    /// <summary>Base backoff before applying jitter + exponential factor. Default 100ms.</summary>
    public TimeSpan BaseRetryDelay { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Jitter ratio (0..1) applied to each backoff. Default 0.5 = ±50%.</summary>
    public double JitterRatio { get; set; } = 0.5;
}
