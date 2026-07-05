using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SurrealForge.Client.Connection;

namespace SurrealForge.Client.Transaction;

/// <summary>
/// Default <see cref="ISurrealTransaction"/> implementation. The constructor
/// is internal — callers obtain instances exclusively through
/// <see cref="ISurrealConnection.BeginTransactionAsync"/>.
/// </summary>
/// <remarks>
/// SurrealDB 3.x scopes a transaction to a single HTTP request: BEGIN and
/// COMMIT issued in separate stateless requests are invalid ("Cannot COMMIT
/// without starting a transaction"). This handle therefore <em>buffers</em>
/// the statements executed while it is open and flushes them as one
/// <c>BEGIN; ...; COMMIT;</c> request on <see cref="CommitAsync"/>. Disposing
/// without committing simply discards the buffer — nothing was sent to the
/// server, so there is no server-side transaction to cancel.
/// </remarks>
public sealed class SurrealTransaction : ISurrealTransaction
{
    private readonly HttpSurrealConnection _connection;
    private readonly List<BufferedStatement> _statements = new();
    private int _committed; // 0 = not committed, 1 = COMMIT round-trip completed successfully
    private int _disposed;  // 0 = live,          1 = dispose entered

    /// <inheritdoc/>
    public bool IsCommitted => Volatile.Read(ref _committed) == 1;

    /// <inheritdoc/>
    public bool IsDisposed  => Volatile.Read(ref _disposed)  == 1;

    internal SurrealTransaction(HttpSurrealConnection connection)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
    }

    /// <summary>
    /// Open a buffering transaction on the connection. No request is sent here;
    /// statements accumulate until <see cref="CommitAsync"/> flushes them in a
    /// single round-trip (the only transaction shape SurrealDB 3.x's stateless
    /// HTTP transport supports).
    /// </summary>
    public static Task<SurrealTransaction> StartAsync(HttpSurrealConnection connection, CancellationToken ct)
    {
        if (connection is null) throw new ArgumentNullException(nameof(connection));
        var txn = new SurrealTransaction(connection);
        connection.EnlistTransaction(txn);
        return Task.FromResult(txn);
    }

    /// <summary>
    /// Append a statement to the transaction buffer. Called by the connection
    /// when <see cref="HttpSurrealConnection.ExecuteRawAsync"/> runs while this
    /// transaction is enlisted. The statement is NOT sent to the server yet; it
    /// is flushed (with all siblings) on commit. Returns an empty OK response so
    /// the caller's control flow is preserved — the real per-statement results
    /// are returned by the single commit round-trip.
    /// </summary>
    internal SurrealResponse Buffer(string sql, object? parameters)
    {
        if (Volatile.Read(ref _committed) == 1)
            throw new InvalidOperationException("Cannot execute on a committed transaction.");
        if (Volatile.Read(ref _disposed) == 1)
            throw new InvalidOperationException("Cannot execute on a disposed transaction.");
        _statements.Add(new BufferedStatement(sql, parameters));
        return SurrealResponse.BufferedAck();
    }

    /// <inheritdoc/>
    public async Task CommitAsync(CancellationToken ct = default)
    {
        if (Volatile.Read(ref _committed) == 1) return;
        if (Volatile.Read(ref _disposed) == 1)
            throw new InvalidOperationException("Cannot commit a disposed transaction.");

        // Detach first so the flush itself is NOT re-buffered into this txn.
        _connection.DelistTransaction(this);

        if (_statements.Count == 0)
        {
            Volatile.Write(ref _committed, 1);
            return;
        }

        // Flatten buffered (sql, params) into one BEGIN; ...; COMMIT; request.
        // Each statement's parameters are merged into a single vars object with
        // a per-statement prefix so name collisions across statements cannot
        // clobber one another.
        var sb = new StringBuilder("BEGIN TRANSACTION;\n");
        var mergedParams = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (int i = 0; i < _statements.Count; i++)
        {
            var stmt = RewriteParams(_statements[i], i, mergedParams).TrimEnd();
            sb.Append(stmt);
            // Each statement must be semicolon-terminated or the next one (and
            // the trailing COMMIT) parses as a continuation of it.
            if (!stmt.EndsWith(";", StringComparison.Ordinal)) sb.Append(';');
            sb.Append('\n');
        }
        sb.Append("COMMIT TRANSACTION;");

        var resp = await _connection
            .ExecuteRawAsync(sb.ToString(), mergedParams.Count == 0 ? null : mergedParams, ct)
            .ConfigureAwait(false);
        resp.EnsureAllOk();
        Volatile.Write(ref _committed, 1);
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0) return default;
        // Dispose-without-commit: nothing was sent to the server (statements
        // were only buffered locally), so there is no server-side transaction
        // to cancel. Just detach and drop the buffer.
        _connection.DelistTransaction(this);
        _statements.Clear();
        return default;
    }

    /// <summary>
    /// Prefix each statement's parameter names so multiple buffered statements
    /// with the same param name don't collide in the merged vars object, and
    /// copy the values into <paramref name="merged"/>.
    /// </summary>
    private static string RewriteParams(BufferedStatement stmt, int index, Dictionary<string, object?> merged)
    {
        if (stmt.Parameters is null) return stmt.Sql;

        var sql = stmt.Sql;
        foreach (var (name, value) in EnumerateParams(stmt.Parameters))
        {
            var scoped = $"_t{index}_{name}";
            merged[scoped] = value;
            sql = ReplaceParamToken(sql, name, scoped);
        }
        return sql;
    }

    private static IEnumerable<(string Name, object? Value)> EnumerateParams(object parameters)
    {
        if (parameters is IDictionary<string, object?> dict)
        {
            foreach (var kvp in dict) yield return (kvp.Key, kvp.Value);
            yield break;
        }
        // Anonymous / POCO: reflect public readable properties.
        foreach (var p in parameters.GetType().GetProperties())
        {
            if (p.CanRead) yield return (p.Name, p.GetValue(parameters));
        }
    }

    /// <summary>Replace whole-token <c>$name</c> occurrences with <c>$scoped</c>.</summary>
    private static string ReplaceParamToken(string sql, string name, string scoped)
    {
        var needle = "$" + name;
        var sb = new StringBuilder(sql.Length);
        int i = 0;
        while (i < sql.Length)
        {
            int hit = sql.IndexOf(needle, i, StringComparison.Ordinal);
            if (hit < 0) { sb.Append(sql, i, sql.Length - i); break; }
            int after = hit + needle.Length;
            bool boundary = after >= sql.Length || (!char.IsLetterOrDigit(sql[after]) && sql[after] != '_');
            sb.Append(sql, i, hit - i);
            sb.Append(boundary ? "$" + scoped : needle);
            i = after;
        }
        return sb.ToString();
    }

    private readonly record struct BufferedStatement(string Sql, object? Parameters);
}
