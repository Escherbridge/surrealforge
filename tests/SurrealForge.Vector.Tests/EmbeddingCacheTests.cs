// SPDX-License-Identifier: MIT
// Cache hit/miss + content-hash reuse (including the CachedVectorEncoder decorator).

using FluentAssertions;
using SurrealForge.Client.Idempotency;
using SurrealForge.Vector;

namespace SurrealForge.Vector.Tests;

public sealed class EmbeddingCacheTests
{
    [Fact]
    public async Task Miss_returns_null_then_hit_returns_the_stored_vector()
    {
        var cache = new InMemoryEmbeddingCache();
        var hash = EmbeddingContentHash.Compute("some text");

        (await cache.GetAsync(hash)).Should().BeNull();

        var vector = new float[] { 1f, 2f, 3f };
        await cache.SetAsync(hash, vector);

        (await cache.GetAsync(hash)).Should().BeSameAs(vector);
        cache.Count.Should().Be(1);
    }

    [Fact]
    public void Content_hash_reuses_the_client_idempotency_helper()
    {
        EmbeddingContentHash.Compute("alpha").Should().Be(IdempotencyReplay.ContentHash("alpha"));
    }

    [Fact]
    public void Content_hash_is_deterministic_and_content_sensitive()
    {
        EmbeddingContentHash.Compute("alpha").Should().Be(EmbeddingContentHash.Compute("alpha"));
        EmbeddingContentHash.Compute("alpha").Should().NotBe(EmbeddingContentHash.Compute("alpha "));
    }

    [Fact]
    public async Task Cached_encoder_skips_the_inner_encoder_on_unchanged_text()
    {
        var inner = new FakeVectorEncoder();
        var cache = new InMemoryEmbeddingCache();
        var encoder = new CachedVectorEncoder(inner, cache);

        var first = await encoder.EncodeAsync("hello");
        var second = await encoder.EncodeAsync("hello");

        second.Should().BeSameAs(first);
        inner.SingleCalls.Should().Be(1);
    }

    [Fact]
    public async Task Cached_encoder_batch_encodes_only_the_misses()
    {
        var inner = new FakeVectorEncoder();
        var cache = new InMemoryEmbeddingCache();
        var encoder = new CachedVectorEncoder(inner, cache);

        var seeded = new float[] { 9f, 9f, 9f };
        await cache.SetAsync(EmbeddingContentHash.Compute("known"), seeded);

        var vectors = await encoder.EncodeAsync(new[] { "known", "fresh" });

        vectors[0].Should().BeSameAs(seeded);
        vectors[1].Should().Equal(FakeVectorEncoder.Encode("fresh"));
        inner.Batches.Should().HaveCount(1);
        inner.Batches[0].Should().Equal("fresh");
    }
}
