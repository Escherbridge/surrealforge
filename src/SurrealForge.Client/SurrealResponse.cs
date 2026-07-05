using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.Json;
using SurrealForge.Client.Json;

namespace SurrealForge.Client;

/// <summary>
/// A SurrealDB HTTP <c>/sql</c> response: an ordered list of
/// <see cref="SurrealStatementResult"/>, one per submitted statement.
///
/// Implements <see cref="IReadOnlyList{T}"/> so callers can index/enumerate
/// statements directly. The constructor takes the JSON body produced by
/// <c>POST /sql</c>; <see cref="EnsureAllOk"/> is the explicit way to assert
/// every statement succeeded — there is intentionally no "auto-throw on first
/// ERR" path so callers retain access to the trailing OK statements for
/// debugging.
/// </summary>
public sealed class SurrealResponse : IReadOnlyList<SurrealStatementResult>
{
    private readonly IReadOnlyList<SurrealStatementResult> _statements;

    /// <summary>Statement-by-statement response slots, in submission order.</summary>
    public IReadOnlyList<SurrealStatementResult> Statements => _statements;

    public SurrealResponse(IReadOnlyList<SurrealStatementResult> statements)
    {
        _statements = statements ?? throw new ArgumentNullException(nameof(statements));
    }

    /// <summary>
    /// A single-OK response returned synchronously when a statement is buffered
    /// into an open transaction rather than executed immediately (SurrealDB 3.x
    /// transactions are flushed as one request on commit). The real per-statement
    /// results arrive in the commit round-trip; this ack only keeps the caller's
    /// control flow (e.g. <c>EnsureAllOk()</c>) intact while buffering.
    /// </summary>
    public static SurrealResponse BufferedAck() =>
        new(new[] { new SurrealStatementResult { Status = "OK" } });

    /// <summary>
    /// Parse a SurrealDB HTTP <c>/sql</c> JSON body into a strongly-typed
    /// response. The body MUST be a JSON array (the SurrealDB contract).
    /// </summary>
    public static SurrealResponse FromJson(string json, JsonSerializerOptions? options = null)
    {
        if (json is null) throw new ArgumentNullException(nameof(json));
        var opts = options ?? SurrealJsonOptions.Default;
        using var doc = JsonDocument.Parse(json);
        return FromJsonDocument(doc.RootElement, opts);
    }

    // LOW #L5: FromStream was unused dead code (HttpSurrealConnection reads
    // the response body to a string before parsing — there was no streaming
    // call site anywhere in the package or its integration tests). Removed
    // to shrink the API surface; if streaming becomes a requirement later,
    // restore the method together with the corresponding
    // HttpCompletionOption.ResponseHeadersRead path in the transport.

    private static SurrealResponse FromJsonDocument(JsonElement root, JsonSerializerOptions opts)
    {
        if (root.ValueKind != JsonValueKind.Array)
        {
            throw new SurrealProtocolException(
                $"Expected SurrealDB /sql response to be a JSON array; got {root.ValueKind}.");
        }

        var list = new List<SurrealStatementResult>(root.GetArrayLength());
        foreach (var el in root.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object)
            {
                throw new SurrealProtocolException(
                    $"Expected each statement slot to be a JSON object; got {el.ValueKind}.");
            }

            // Clone Result so it outlives the parent JsonDocument we dispose below.
            JsonElement resultElement = default;
            if (el.TryGetProperty("result", out var rawResult))
            {
                resultElement = rawResult.Clone();
            }

            list.Add(new SurrealStatementResult
            {
                Status = el.TryGetProperty("status", out var s) ? (s.GetString() ?? string.Empty) : string.Empty,
                Detail = el.TryGetProperty("detail", out var d) ? d.GetString() : null,
                Time   = el.TryGetProperty("time",   out var t) ? (t.GetString() ?? string.Empty) : string.Empty,
                Result = resultElement,
            });
        }

        return new SurrealResponse(list);
    }

    /// <summary>
    /// Throw <see cref="SurrealStatementException"/> if ANY statement
    /// returned <c>status != "OK"</c>. The exception names the first failing
    /// statement (1-based index) and includes its <c>detail</c>.
    /// </summary>
    public void EnsureAllOk()
    {
        for (int i = 0; i < _statements.Count; i++)
        {
            if (!_statements[i].IsOk)
            {
                throw new SurrealStatementException(
                    index: i,
                    detail: ExtractErrorText(_statements[i]),
                    statementCount: _statements.Count);
            }
        }
    }

    // SurrealDB error text moves between detail / result-string /
    // result.{message,error,detail,description} across versions.
    public static string? ExtractErrorText(SurrealStatementResult s)
    {
        if (!string.IsNullOrWhiteSpace(s.Detail)) return s.Detail;

        switch (s.Result.ValueKind)
        {
            case JsonValueKind.String:
            {
                var msg = s.Result.GetString();
                if (!string.IsNullOrWhiteSpace(msg)) return msg;
                break;
            }
            case JsonValueKind.Object:
            {
                foreach (var key in new[] { "message", "error", "detail", "description" })
                {
                    if (s.Result.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String)
                    {
                        var msg = p.GetString();
                        if (!string.IsNullOrWhiteSpace(msg)) return msg;
                    }
                }
                var raw = s.Result.GetRawText();
                if (!string.IsNullOrWhiteSpace(raw))
                    return raw.Length > 512 ? raw.Substring(0, 512) + "…(truncated)" : raw;
                break;
            }
        }

        return null;
    }

    /// <summary>
    /// Convenience: project the statement at <paramref name="index"/> as
    /// <c>IReadOnlyList&lt;T&gt;</c>. Throws if the statement is not OK.
    /// </summary>
    public IReadOnlyList<T> GetValues<T>(int index, JsonSerializerOptions? options = null)
    {
        if (index < 0 || index >= _statements.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index),
                $"Statement index {index} out of range (count {_statements.Count}).");
        }
        var s = _statements[index];
        if (!s.IsOk)
        {
            throw new SurrealStatementException(index, ExtractErrorText(s), _statements.Count);
        }
        return s.GetValues<T>(options);
    }

    // IReadOnlyList<T> plumbing
    public SurrealStatementResult this[int index] => _statements[index];
    public int Count => _statements.Count;
    public IEnumerator<SurrealStatementResult> GetEnumerator() => _statements.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>
/// Thrown when the SurrealDB HTTP response does not conform to the documented
/// JSON-array shape (e.g. server returned an object, or a non-JSON body).
/// </summary>
public sealed class SurrealProtocolException : Exception
{
    public SurrealProtocolException(string message) : base(message) { }
    public SurrealProtocolException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Thrown by <see cref="SurrealResponse.EnsureAllOk"/> (or the indexed
/// <c>GetValues</c>) when a statement returned <c>status = "ERR"</c>.
/// Carries the 0-based statement index, the count of submitted statements,
/// and the server-supplied <c>detail</c>.
/// </summary>
public sealed class SurrealStatementException : Exception
{
    public int StatementIndex { get; }
    public int StatementCount { get; }
    public string? Detail { get; }

    public SurrealStatementException(int index, string? detail, int statementCount)
        : base(BuildMessage(index, detail, statementCount))
    {
        StatementIndex = index;
        StatementCount = statementCount;
        Detail         = detail;
    }

    private static string BuildMessage(int index, string? detail, int count) =>
        $"SurrealDB statement {index + 1}/{count} returned ERR: " +
        (string.IsNullOrEmpty(detail) ? "(no detail)" : detail);
}
