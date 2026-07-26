// SPDX-License-Identifier: MIT
// Scripted-executor tests for the backfill runner: checkpoint advance, batch
// encode shapes, content-hash skip, ad-hoc id sets, dynamic placeholder.

using System.Text.Json;
using FluentAssertions;
using SurrealForge.Client.Query;
using SurrealForge.Client.Schema;
using SurrealForge.Vector;

namespace SurrealForge.Vector.Tests;

public sealed class EmbeddingBackfillRunnerTests
{
    public sealed class BackfillDoc
    {
        public string Body { get; set; } = string.Empty;
    }

    private static EmbeddingJobDefinition Definition() =>
        new(typeof(BackfillDoc), "document", "body", "embedding", null, EmbeddingMode.Batched);

    private static string Row(string id, string? text, string? hash)
    {
        var payload = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["source_text"] = text,
            ["stored_hash"] = hash,
        };
        return JsonSerializer.Serialize(payload);
    }

    private static bool IsCheckpointRead(SurrealQuery q) =>
        q.Sql.StartsWith("SELECT * FROM type::record", StringComparison.Ordinal);

    [Fact]
    public async Task Incremental_pages_encode_in_batches_skip_matching_hashes_and_advance_the_checkpoint()
    {
        var def = Definition();
        var config = new IncrementalBackfillConfig { BatchSize = 2 };

        var betaHash = EmbeddingContentHash.Compute("beta");
        var pages = new Queue<string>(new[]
        {
            // Full page → producer requests another one.
            "[" + Row("document:a", "alpha", null) + "," + Row("document:b", "beta", betaHash) + "]",
            // Short page → caught up.
            "[" + Row("document:c", "gamma", "stale-hash") + "]",
        });

        var fake = new FakeSurrealExecutor();
        fake.QueryJson = q => IsCheckpointRead(q) || pages.Count == 0 ? "[]" : pages.Dequeue();
        var encoder = new FakeVectorEncoder();
        var runner = new EmbeddingBackfillRunner(fake, encoder, new InMemoryEmbeddingCache());

        var report = await runner.RunIncrementalAsync(def, config);

        // Report: b skipped via content-hash, a + c embedded in one batch.
        report.RowsScanned.Should().Be(3);
        report.RowsSkipped.Should().Be(1);
        report.RowsEmbedded.Should().Be(2);
        report.BatchesFlushed.Should().Be(1);

        // Batch EncodeAsync got the batch, not per-row calls.
        encoder.SingleCalls.Should().Be(0);
        encoder.Batches.Should().HaveCount(1);
        encoder.Batches[0].Should().Equal("alpha", "gamma");

        // Query sequence: checkpoint read, page 1 (no cursor), page 2 (cursor = last id of page 1).
        fake.Queries.Should().HaveCount(3);
        fake.Queries[1].Sql.Should().Be(
            "SELECT id, body AS source_text, embedding_hash AS stored_hash FROM document " +
            "ORDER BY id LIMIT 2");
        fake.Queries[2].Sql.Should().Contain("WHERE id > type::record($_tbl, $_cursor)");
        fake.Queries[2].Params["_cursor"].Should().Be("b");

        // Writes: one Combine'd UPDATE batch, then the checkpoint UPSERT.
        fake.Executes.Should().HaveCount(2);
        var update = fake.Executes[0];
        update.IsMultiStatement.Should().BeTrue();
        update.Sql.Should().Contain("UPDATE type::record($_tbl, $_r0) SET embedding = $_v0, embedding_hash = $_h0");
        update.Sql.Should().Contain("UPDATE type::record($_tbl, $_r1) SET embedding = $_v1, embedding_hash = $_h1");
        update.Params["_r0"].Should().Be("a");
        update.Params["_r1"].Should().Be("c");
        update.Params["_v0"].Should().BeEquivalentTo(FakeVectorEncoder.Encode("alpha"));
        update.Params["_h0"].Should().Be(EmbeddingContentHash.Compute("alpha"));
        // Embeddings are bound, never interpolated.
        update.Sql.Should().NotContain("[");

        var checkpoint = fake.Executes[1];
        checkpoint.Sql.Should().Be("UPSERT type::record($_t, $_id) SET cursor = $_cursor");
        checkpoint.Params["_t"].Should().Be("surrealforge_embedding_checkpoint");
        checkpoint.Params["_id"].Should().Be("document_embedding");
        checkpoint.Params["_cursor"].Should().Be("c");
    }

    [Fact]
    public async Task Incremental_resumes_from_a_persisted_cursor()
    {
        var def = Definition();
        var fake = new FakeSurrealExecutor();
        fake.QueryJson = q => IsCheckpointRead(q) ? "[{\"cursor\":\"x\"}]" : "[]";
        var runner = new EmbeddingBackfillRunner(fake, new FakeVectorEncoder(), new InMemoryEmbeddingCache());

        var report = await runner.RunIncrementalAsync(def, new IncrementalBackfillConfig { BatchSize = 2 });

        report.RowsScanned.Should().Be(0);
        fake.Queries.Should().HaveCount(2);
        fake.Queries[1].Sql.Should().Contain("WHERE id > type::record($_tbl, $_cursor)");
        fake.Queries[1].Params["_cursor"].Should().Be("x");
        fake.Executes.Should().BeEmpty("nothing was embedded, so the checkpoint must not move");
    }

    [Fact]
    public async Task Incremental_reuses_cached_embeddings_and_encodes_only_misses()
    {
        var def = Definition();
        var cache = new InMemoryEmbeddingCache();
        var cachedVector = new float[] { 5f, 5f, 5f };
        await cache.SetAsync(EmbeddingContentHash.Compute("alpha"), cachedVector);

        var pages = new Queue<string>(new[]
        {
            "[" + Row("document:a", "alpha", null) + "," + Row("document:b", "beta", null) + "]",
        });
        var fake = new FakeSurrealExecutor();
        fake.QueryJson = q => IsCheckpointRead(q) || pages.Count == 0 ? "[]" : pages.Dequeue();
        var encoder = new FakeVectorEncoder();
        var runner = new EmbeddingBackfillRunner(fake, encoder, cache);

        var report = await runner.RunIncrementalAsync(def, new IncrementalBackfillConfig { BatchSize = 2 });

        report.RowsEmbedded.Should().Be(2);
        encoder.Batches.Should().HaveCount(1);
        encoder.Batches[0].Should().Equal("beta");

        var update = fake.Executes[0];
        update.Params["_v0"].Should().BeSameAs(cachedVector, "the cache hit must be written, not re-encoded");
    }

    [Fact]
    public async Task AdHoc_id_set_fetches_via_bound_record_params_and_honors_reembed()
    {
        var def = Definition();
        var alphaHash = EmbeddingContentHash.Compute("alpha");
        var fake = new FakeSurrealExecutor();
        fake.QueryJson = q => IsCheckpointRead(q)
            ? "[]"
            : "[" + Row("document:a", "alpha", alphaHash) + "," + Row("document:b", "beta", null) + "]";
        var encoder = new FakeVectorEncoder();
        var runner = new EmbeddingBackfillRunner(fake, encoder, new InMemoryEmbeddingCache());

        var config = new AdHocBackfillConfig
        {
            BatchSize = 10,
            Ids = new[] { "document:a", "b" },
            Reembed = true,
        };
        var report = await runner.RunAdHocAsync(def, config);

        // Reembed forces the hash-matching row through anyway.
        report.RowsEmbedded.Should().Be(2);
        report.RowsSkipped.Should().Be(0);

        fake.Queries.Should().HaveCount(1);
        fake.Queries[0].Sql.Should().Be(
            "SELECT id, body AS source_text, embedding_hash AS stored_hash FROM " +
            "type::record($_tbl, $_r0), type::record($_tbl, $_r1)");
        fake.Queries[0].Params["_tbl"].Should().Be("document");
        fake.Queries[0].Params["_r0"].Should().Be("a", "a table:key id binds as its bare key");
        fake.Queries[0].Params["_r1"].Should().Be("b");

        fake.Executes.Should().HaveCount(1, "ad-hoc jobs are run-once and keep no checkpoint");
    }

    [Fact]
    public async Task AdHoc_without_reembed_skips_hash_matches()
    {
        var def = Definition();
        var alphaHash = EmbeddingContentHash.Compute("alpha");
        var fake = new FakeSurrealExecutor();
        fake.QueryJson = _ => "[" + Row("document:a", "alpha", alphaHash) + "]";
        var runner = new EmbeddingBackfillRunner(fake, new FakeVectorEncoder(), new InMemoryEmbeddingCache());

        var report = await runner.RunAdHocAsync(def, new AdHocBackfillConfig
        {
            BatchSize = 10,
            Ids = new[] { "document:a" },
        });

        report.RowsSkipped.Should().Be(1);
        report.RowsEmbedded.Should().Be(0);
        fake.Executes.Should().BeEmpty();
    }

    [Fact]
    public async Task Empty_text_rows_are_skipped()
    {
        var def = Definition();
        var fake = new FakeSurrealExecutor();
        fake.QueryJson = q => IsCheckpointRead(q)
            ? "[]"
            : "[" + Row("document:a", null, null) + "," + Row("document:b", "", null) + "]";
        var runner = new EmbeddingBackfillRunner(fake, new FakeVectorEncoder(), new InMemoryEmbeddingCache());

        var report = await runner.RunIncrementalAsync(def, new IncrementalBackfillConfig { BatchSize = 5 });

        report.RowsScanned.Should().Be(2);
        report.RowsSkipped.Should().Be(2);
        report.RowsEmbedded.Should().Be(0);
    }

    [Fact]
    public async Task Dynamic_without_a_live_source_throws_the_documented_placeholder()
    {
        var runner = new EmbeddingBackfillRunner(
            new FakeSurrealExecutor(), new FakeVectorEncoder(), new InMemoryEmbeddingCache());

        var act = () => runner.RunDynamicAsync(Definition(), new DynamicBackfillConfig());

        await act.Should().ThrowAsync<NotSupportedException>().WithMessage("*Phase 2*");
    }

    [Fact]
    public async Task Dynamic_with_a_feed_batches_changes_through_the_encoder()
    {
        var def = Definition();
        var fake = new FakeSurrealExecutor();
        var encoder = new FakeVectorEncoder();
        var runner = new EmbeddingBackfillRunner(fake, encoder, new InMemoryEmbeddingCache());

        static async IAsyncEnumerable<EmbeddingSourceChange> Feed(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            yield return new EmbeddingSourceChange("document:a", "alpha");
            yield return new EmbeddingSourceChange("document:b", "beta");
            yield return new EmbeddingSourceChange("document:c", "gamma");
            await Task.CompletedTask;
        }

        var report = await runner.RunDynamicAsync(def, new DynamicBackfillConfig
        {
            BatchSize = 2,
            LiveSource = Feed,
        });

        report.RowsEmbedded.Should().Be(3);
        encoder.Batches.Sum(b => b.Count).Should().Be(3);
        encoder.Batches.Should().OnlyContain(b => b.Count <= 2);
        fake.Executes.Should().NotBeEmpty();
        fake.Executes.Sum(e => e.IsMultiStatement ? 2 : 1).Should().Be(3);
    }
}
