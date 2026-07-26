// SPDX-License-Identifier: MIT
// SurrealForge.Vector -- content-hash caching decorator for any encoder.
// This is the WriteTime-mode seam: see AGENTS.md §Caching.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SurrealForge.Vector;

/// <summary>Wraps an <see cref="IVectorEncoder"/> so unchanged text is a cache hit, not a re-encode.</summary>
public sealed class CachedVectorEncoder : IVectorEncoder
{
    private readonly IVectorEncoder _inner;
    private readonly IEmbeddingCache _cache;

    public CachedVectorEncoder(IVectorEncoder inner, IEmbeddingCache cache)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    /// <inheritdoc/>
    public int Dimension => _inner.Dimension;

    /// <inheritdoc/>
    public async ValueTask<float[]> EncodeAsync(string text, CancellationToken ct = default)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));
        var hash = EmbeddingContentHash.Compute(text);
        var cached = await _cache.GetAsync(hash, ct).ConfigureAwait(false);
        if (cached is not null) return cached;

        var vector = await _inner.EncodeAsync(text, ct).ConfigureAwait(false);
        await _cache.SetAsync(hash, vector, ct).ConfigureAwait(false);
        return vector;
    }

    /// <inheritdoc/>
    public async ValueTask<float[][]> EncodeAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        if (texts is null) throw new ArgumentNullException(nameof(texts));

        var vectors = new float[texts.Count][];
        var missTexts = new List<string>();
        var missIndexes = new List<int>();
        var missHashes = new List<string>();

        for (int i = 0; i < texts.Count; i++)
        {
            var hash = EmbeddingContentHash.Compute(texts[i]);
            var cached = await _cache.GetAsync(hash, ct).ConfigureAwait(false);
            if (cached is not null)
            {
                vectors[i] = cached;
            }
            else
            {
                missTexts.Add(texts[i]);
                missIndexes.Add(i);
                missHashes.Add(hash);
            }
        }

        if (missTexts.Count > 0)
        {
            var encoded = await _inner.EncodeAsync(missTexts, ct).ConfigureAwait(false);
            for (int j = 0; j < missIndexes.Count; j++)
            {
                vectors[missIndexes[j]] = encoded[j];
                await _cache.SetAsync(missHashes[j], encoded[j], ct).ConfigureAwait(false);
            }
        }

        return vectors;
    }
}
