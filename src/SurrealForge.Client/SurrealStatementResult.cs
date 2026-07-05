using System;
using System.Collections.Generic;
using System.Text.Json;
using SurrealForge.Client.Json;

namespace SurrealForge.Client;

/// <summary>
/// One statement's slot inside a SurrealDB HTTP <c>/sql</c> response.
///
/// SurrealDB's <c>POST /sql</c> endpoint always returns a JSON array — even
/// for a single statement — where each element carries <c>status</c>,
/// <c>result</c>, <c>time</c>, and (on error) <c>detail</c>. The wave-1
/// <see href="https://surrealdb.com/docs/surrealdb/integration/http">HTTP contract</see>
/// is preserved here verbatim. See code-review C5: collapsing this list into
/// a single <c>Result</c> field is the root cause of the multi-statement
/// silent-swallow bug that broke the G2 conditional-state-transition flow,
/// so this type intentionally preserves the per-statement shape.
/// </summary>
public sealed class SurrealStatementResult
{
    /// <summary>Status of this individual statement: <c>"OK"</c> or <c>"ERR"</c>.</summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>Server-supplied error message when <see cref="Status"/> is <c>"ERR"</c>; <c>null</c> on success.</summary>
    public string? Detail { get; init; }

    /// <summary>Server-supplied timing string (e.g. <c>"123.4µs"</c>).</summary>
    public string Time { get; init; } = string.Empty;

    /// <summary>
    /// Raw <see cref="JsonElement"/> of the statement's <c>result</c> field.
    /// Use <see cref="GetValues{T}"/> for a typed projection, or inspect the
    /// element directly when working with heterogeneous shapes.
    /// </summary>
    public JsonElement Result { get; init; }

    /// <summary>True iff <see cref="Status"/> equals <c>"OK"</c> (case-insensitive).</summary>
    public bool IsOk =>
        string.Equals(Status, "OK", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The server error message for a failed statement, regardless of where the
    /// transport placed it. SurrealDB moves the text between the top-level
    /// <see cref="Detail"/> field and the <c>result</c> slot (a bare string, or
    /// an object with <c>message</c>/<c>error</c>/<c>detail</c>/<c>description</c>)
    /// across versions — notably 3.x puts a failed statement's message in
    /// <c>result</c>, leaving <see cref="Detail"/> null. Callers doing graceful
    /// per-statement handling (e.g. treating a UNIQUE violation as a race-loss)
    /// MUST read this instead of <see cref="Detail"/>, which is often empty.
    /// Returns <c>null</c> when the statement succeeded or carried no message.
    /// </summary>
    public string? ErrorText => SurrealResponse.ExtractErrorText(this);

    /// <summary>
    /// Materialise this statement's <c>result</c> as <c>IReadOnlyList&lt;T&gt;</c>.
    /// SurrealDB statement results are normally a JSON array; when the server
    /// returns a single object (e.g. <c>RETURN $value</c>) it is wrapped here
    /// into a one-element list. <c>null</c> /  <c>undefined</c> results are
    /// returned as an empty list.
    /// </summary>
    /// <param name="options">
    /// Override the default <see cref="SurrealJsonOptions.Default"/>. Pass a
    /// custom instance only if you have a converter outside the wave-1 set.
    /// </param>
    public IReadOnlyList<T> GetValues<T>(JsonSerializerOptions? options = null)
    {
        var opts = options ?? SurrealJsonOptions.Default;

        // No payload at all -> empty list (NOT throw — callers may legitimately
        // run a statement that returns nothing, e.g. DEFINE FIELD).
        if (Result.ValueKind == JsonValueKind.Null ||
            Result.ValueKind == JsonValueKind.Undefined)
        {
            return Array.Empty<T>();
        }

        if (Result.ValueKind == JsonValueKind.Array)
        {
            var list = new List<T>(Result.GetArrayLength());
            foreach (var el in Result.EnumerateArray())
            {
                var v = el.Deserialize<T>(opts);
                if (v is not null) list.Add(v);
            }
            return list;
        }

        // Single object / value — wrap to keep the caller's iteration uniform.
        var single = Result.Deserialize<T>(opts);
        return single is null ? Array.Empty<T>() : new[] { single };
    }
}
