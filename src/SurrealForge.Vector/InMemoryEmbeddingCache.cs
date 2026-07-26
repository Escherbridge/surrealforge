// SPDX-License-Identifier: MIT
// SurrealForge.Vector -- default process-local cache. See AGENTS.md §Caching.

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace SurrealForge.Vector;

/// <summary>Process-local <see cref="IEmbeddingCache"/>. Returned arrays are shared — callers must not mutate them.</summary>
public sealed class InMemoryEmbeddingCache : IEmbeddingCache
{
    private readonly ConcurrentDictionary<string, float[]> _entries =
        new ConcurrentDictionary<string, float[]>(StringComparer.Ordinal);

    /// <inheritdoc/>
    public ValueTask<float[]?> GetAsync(string contentHash, CancellationToken ct = default)
    {
        if (contentHash is null) throw new ArgumentNullException(nameof(contentHash));
        _entries.TryGetValue(contentHash, out var embedding);
        return new ValueTask<float[]?>(embedding);
    }

    /// <inheritdoc/>
    public ValueTask SetAsync(string contentHash, float[] embedding, CancellationToken ct = default)
    {
        if (contentHash is null) throw new ArgumentNullException(nameof(contentHash));
        if (embedding is null) throw new ArgumentNullException(nameof(embedding));
        _entries[contentHash] = embedding;
        return default;
    }

    /// <summary>Number of cached entries (diagnostics/tests).</summary>
    public int Count => _entries.Count;
}
