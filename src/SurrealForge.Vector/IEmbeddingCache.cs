// SPDX-License-Identifier: MIT
// SurrealForge.Vector -- content-hash-keyed embedding cache. See AGENTS.md §Caching.

using System;
using System.Threading;
using System.Threading.Tasks;
using SurrealForge.Client.Idempotency;

namespace SurrealForge.Vector;

/// <summary>Embedding cache keyed by content hash so unchanged text is never re-encoded.</summary>
public interface IEmbeddingCache
{
    /// <summary>Look up a cached embedding by content hash; null on miss.</summary>
    ValueTask<float[]?> GetAsync(string contentHash, CancellationToken ct = default);

    /// <summary>Store an embedding under its content hash.</summary>
    ValueTask SetAsync(string contentHash, float[] embedding, CancellationToken ct = default);
}

/// <summary>Canonical content-hash for embedding cache keys (reuses the Client idempotency helper).</summary>
public static class EmbeddingContentHash
{
    /// <summary>SHA-256 lowercase hex of the text, via <see cref="IdempotencyReplay.ContentHash"/>.</summary>
    public static string Compute(string text)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));
        return IdempotencyReplay.ContentHash(text);
    }
}
