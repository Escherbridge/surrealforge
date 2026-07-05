using System;
using System.Threading;
using System.Threading.Tasks;

namespace SurrealForge.Client.Connection;

/// <summary>
/// Bounded semaphore over a single shared <see cref="ISurrealConnection"/>.
/// SurrealDB sits behind one HTTP endpoint per process; we don't need a
/// fan-out pool of distinct connections, but we DO need to cap simultaneous
/// in-flight requests so a runaway caller can't exhaust server-side resources.
/// </summary>
/// <remarks>
/// <para>
/// Usage:
/// <code>
/// using var lease = await pool.AcquireAsync(ct);
/// var response = await lease.Connection.ExecuteRawAsync("...", null, ct);
/// </code>
/// </para>
/// <para>
/// The lease releases the semaphore on dispose. <c>MaxConnections</c> from
/// <see cref="SurrealConnectionOptions"/> is the cap; default 32.
/// </para>
/// </remarks>
public sealed class SurrealConnectionPool : IDisposable
{
    private readonly ISurrealConnection _connection;
    private readonly SemaphoreSlim _semaphore;
    private readonly bool _ownsConnection;
    private int _disposed;

    /// <summary>Pool capacity (number of concurrently issued leases).</summary>
    public int MaxConnections { get; }

    /// <summary>Currently issued lease count.</summary>
    public int InUse => MaxConnections - _semaphore.CurrentCount;

    /// <summary>
    /// Construct over an existing connection. The pool will NOT dispose the
    /// connection on its own dispose unless <paramref name="ownsConnection"/>
    /// is true.
    /// </summary>
    public SurrealConnectionPool(
        ISurrealConnection connection,
        SurrealConnectionOptions options,
        bool ownsConnection = false)
    {
        _connection     = connection ?? throw new ArgumentNullException(nameof(connection));
        if (options is null) throw new ArgumentNullException(nameof(options));
        if (options.MaxConnections <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "MaxConnections must be > 0; got " + options.MaxConnections);

        MaxConnections  = options.MaxConnections;
        _semaphore      = new SemaphoreSlim(MaxConnections, MaxConnections);
        _ownsConnection = ownsConnection;
    }

    /// <summary>
    /// Acquire a lease, waiting up to <see cref="SurrealConnectionOptions.RequestTimeout"/>
    /// of the underlying connection (or the cancellation token, whichever
    /// fires first). The returned <see cref="Lease"/> MUST be disposed; failing
    /// to do so permanently leaks one slot of pool capacity.
    /// </summary>
    public async Task<Lease> AcquireAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        await _semaphore.WaitAsync(ct).ConfigureAwait(false);
        return new Lease(this);
    }

    private void Release()
    {
        // SemaphoreSlim.Release throws SemaphoreFullException if called more
        // times than Wait — the Lease guards against double-dispose so we
        // don't need an extra try/catch here.
        _semaphore.Release();
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(SurrealConnectionPool));
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _semaphore.Dispose();
        if (_ownsConnection)
        {
            _connection.Dispose();
        }
    }

    /// <summary>
    /// A single in-flight slot. The <see cref="Connection"/> property exposes
    /// the underlying connection; disposing the lease returns the slot to the
    /// pool. Idempotent: extra disposes are no-ops.
    /// </summary>
    public sealed class Lease : IDisposable
    {
        private SurrealConnectionPool? _pool;

        public ISurrealConnection Connection { get; }

        internal Lease(SurrealConnectionPool pool)
        {
            _pool      = pool;
            Connection = pool._connection;
        }

        public void Dispose()
        {
            var pool = Interlocked.Exchange(ref _pool, null);
            pool?.Release();
        }
    }
}
