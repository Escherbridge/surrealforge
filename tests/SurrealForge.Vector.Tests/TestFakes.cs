// SPDX-License-Identifier: MIT
// Shared fakes: a scripted ISurrealExecutor that captures every SurrealQuery,
// and a deterministic IVectorEncoder that records batch shapes.

using System.Text.Json;
using SurrealForge.Client;
using SurrealForge.Client.Json;
using SurrealForge.Client.Query;
using SurrealForge.Vector;

namespace SurrealForge.Vector.Tests;

/// <summary>Captures queries; QueryAsync rows come from a scriptable JSON-array delegate.</summary>
internal sealed class FakeSurrealExecutor : ISurrealExecutor
{
    public List<SurrealQuery> Queries { get; } = new();
    public List<SurrealQuery> Executes { get; } = new();

    /// <summary>Returns the JSON array body for a QueryAsync call. Default: empty.</summary>
    public Func<SurrealQuery, string> QueryJson { get; set; } = _ => "[]";

    public Task<IReadOnlyList<T>> QueryAsync<T>(SurrealQuery query, CancellationToken ct = default)
    {
        query.Validate(strict: true);
        Queries.Add(query);
        using var doc = JsonDocument.Parse(QueryJson(query));
        var list = new List<T>();
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var v = el.Deserialize<T>(SurrealJsonOptions.Default);
            if (v is not null) list.Add(v);
        }
        return Task.FromResult<IReadOnlyList<T>>(list);
    }

    public Task<T?> QuerySingleAsync<T>(SurrealQuery query, CancellationToken ct = default) where T : class
        => throw new NotSupportedException("Not used by these tests.");

    public Task<SurrealResponse> ExecuteAsync(SurrealQuery query, CancellationToken ct = default)
    {
        query.Validate(strict: true);
        Executes.Add(query);
        return Task.FromResult(SurrealResponse.BufferedAck());
    }
}

/// <summary>Deterministic encoder: vector = [length, first-char, batch-ordinal]; records every batch.</summary>
internal sealed class FakeVectorEncoder : IVectorEncoder
{
    public List<IReadOnlyList<string>> Batches { get; } = new();
    public int SingleCalls { get; private set; }

    public int Dimension => 3;

    public ValueTask<float[]> EncodeAsync(string text, CancellationToken ct = default)
    {
        SingleCalls++;
        return new ValueTask<float[]>(Encode(text));
    }

    public ValueTask<float[][]> EncodeAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        Batches.Add(new List<string>(texts));
        var result = new float[texts.Count][];
        for (int i = 0; i < texts.Count; i++) result[i] = Encode(texts[i]);
        return new ValueTask<float[][]>(result);
    }

    internal static float[] Encode(string text)
        => new float[] { text.Length, text.Length == 0 ? 0 : text[0], 1f };
}
