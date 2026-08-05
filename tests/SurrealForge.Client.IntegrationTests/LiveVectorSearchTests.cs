// SPDX-License-Identifier: MIT
// Live verification of the SurrealForge.Vector query surface — the link unit
// tests cannot cover: the generated KNN / brute-force SQL actually parsing and
// executing on a real SurrealDB, the $q float[] binding surviving the wire,
// and the dist projection round-tripping into VectorSearchResult<T>.
// See src/SurrealForge.Vector/AGENTS.md §Query surface.

using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using FluentAssertions;
using SurrealForge.Client.Connection;
using SurrealForge.Client.Query;
using SurrealForge.Client.Schema;
using SurrealForge.Vector;

namespace SurrealForge.Client.IntegrationTests;

[Collection("LiveSurrealDb")]
public class LiveVectorSearchTests
{
    private readonly LiveSurrealDbCollectionFixture _fx;

    public LiveVectorSearchTests(LiveSurrealDbCollectionFixture fx) => _fx = fx;

    private bool TrySkip()
    {
        if (_fx.SurrealAvailable) return false;
        Console.WriteLine($"[SKIP] LiveSurrealDb unavailable: {_fx.SkipReason}");
        return true;
    }

    private SurrealConnectionOptions MakeOptions(string db) => new()
    {
        Endpoint = _fx.Endpoint,
        Namespace = _fx.Namespace,
        Database = db,
        User = _fx.User,
        Password = _fx.Password,
        MaxRetries = 1,
    };

    // ─── Fixture POCO ───────────────────────────────────────────────────────

    // No `dist` property: the search surface must project it off the row
    // without forcing it onto the consumer's model.
    [SurrealTable("vec_search_doc")]
    public sealed class VecSearchDoc
    {
        public string? Title { get; set; }
        public float[]? Embedding { get; set; }
    }

    /// <summary>The query vector — identical to alpha's embedding.</summary>
    private static readonly float[] QueryVector = { 1.0f, 0.0f, 0.0f, 0.0f };

    /// <summary>
    /// Fresh database seeded with an HNSW-indexed table and four vectors:
    /// alpha == the query, beta near it, gamma/delta orthogonal. Delta carries
    /// twice gamma's magnitude so euclidean/manhattan ranking is strictly
    /// ordered — equal-magnitude orthogonals tie, and a tie makes the
    /// third-place assertion non-deterministic.
    /// </summary>
    private async Task<HttpSurrealConnection> SeedAsync()
    {
        var db = $"vecsearch_{Guid.NewGuid():N}"[..30];
        _fx.EnsureDatabase(db);
        var conn = new HttpSurrealConnection(new HttpClientHandler(), MakeOptions(db));

        var ddl = await conn.ExecuteRawAsync(
            "DEFINE TABLE vec_search_doc SCHEMAFULL;" +
            "DEFINE FIELD title ON vec_search_doc TYPE option<string>;" +
            "DEFINE FIELD embedding ON vec_search_doc TYPE option<array<float>>;" +
            "DEFINE INDEX hnsw_vec_search_doc_embedding ON vec_search_doc " +
            "FIELDS embedding HNSW DIMENSION 4 DIST COSINE TYPE F32 EFC 150 M 12;");
        ddl.EnsureAllOk();

        var seed = await conn.ExecuteRawAsync(
            "CREATE vec_search_doc SET title = 'alpha', embedding = [1.0, 0.0, 0.0, 0.0];" +
            "CREATE vec_search_doc SET title = 'beta',  embedding = [0.9, 0.1, 0.0, 0.0];" +
            "CREATE vec_search_doc SET title = 'gamma', embedding = [0.0, 1.0, 0.0, 0.0];" +
            "CREATE vec_search_doc SET title = 'delta', embedding = [0.0, 0.0, 2.0, 0.0];");
        seed.EnsureAllOk();

        return conn;
    }

    // ─── Indexed KNN path ───────────────────────────────────────────────────

    [Fact]
    public async Task Indexed_knn_returns_nearest_neighbours_in_order_live()
    {
        if (TrySkip()) return;

        await using var conn = await SeedAsync();
        var executor = new DefaultSurrealExecutor(conn);

        var hits = await executor.VectorSearchAsync<VecSearchDoc>(
            "embedding", QueryVector, k: 2);

        hits.Should().HaveCount(2, "the <|K|> operator caps results at K server-side");
        // ORDER BY dist on the KNN path must rank the exact match first.
        hits.Select(h => h.Record.Title).Should().Equal(new[] { "alpha", "beta" });
        hits[0].Distance.Should().BeLessThan(hits[1].Distance,
            "vector::distance::knn() must project a real ascending distance, not NaN");
        hits[0].Record.Embedding.Should().Equal(new[] { 1.0f, 0.0f, 0.0f, 0.0f },
            "SELECT * must still hydrate the record itself alongside the dist projection");
    }

    [Fact]
    public async Task Indexed_knn_with_ef_beam_width_executes_live()
    {
        if (TrySkip()) return;

        await using var conn = await SeedAsync();
        var executor = new DefaultSurrealExecutor(conn);

        var hits = await executor.VectorSearchAsync<VecSearchDoc>(
            "embedding", QueryVector,
            new VectorSearchOptions { K = 3, Ef = 40 });

        hits.Should().HaveCount(3, "<|K,EF|> must parse and honour K on a live server");
        hits[0].Record.Title.Should().Be("alpha");
    }

    [Fact]
    public async Task Indexed_knn_with_extra_filter_predicate_executes_live()
    {
        if (TrySkip()) return;

        await using var conn = await SeedAsync();
        var executor = new DefaultSurrealExecutor(conn);

        // The filter fragment is AND-ed into the same WHERE as the KNN
        // operator — the shape most likely to be rejected by the parser.
        var hits = await executor.VectorSearchAsync<VecSearchDoc>(
            "embedding", QueryVector,
            new VectorSearchOptions
            {
                K = 4,
                Filter = SurrealQuery.Of("title = $t").WithParam("t", "beta"),
            });

        hits.Should().ContainSingle(
            "the merged predicate must actually filter server-side, not be ignored");
        hits[0].Record.Title.Should().Be("beta");
    }

    // ─── Brute-force path ───────────────────────────────────────────────────

    [Fact]
    public async Task Bruteforce_cosine_ranks_similarity_descending_live()
    {
        if (TrySkip()) return;

        await using var conn = await SeedAsync();
        var executor = new DefaultSurrealExecutor(conn);

        var hits = await executor.VectorSearchAsync<VecSearchDoc>(
            "embedding", QueryVector,
            new VectorSearchOptions
            {
                K = 2,
                Metric = VectorMetric.Cosine,
                Strategy = VectorSearchStrategy.BruteForce,
            });

        hits.Should().HaveCount(2, "the brute-force path must honour its LIMIT k");
        hits.Select(h => h.Record.Title).Should().Equal("alpha", "beta");
        hits[0].Distance.Should().BeApproximately(1.0, 1e-6,
            "cosine similarity against an identical vector is 1.0 — proves ORDER BY DESC is right");
        hits[0].Distance.Should().BeGreaterThan(hits[1].Distance,
            "similarity ranks high-is-close");
    }

    [Fact]
    public async Task Bruteforce_euclidean_ranks_distance_ascending_live()
    {
        if (TrySkip()) return;

        await using var conn = await SeedAsync();
        var executor = new DefaultSurrealExecutor(conn);

        var hits = await executor.VectorSearchAsync<VecSearchDoc>(
            "embedding", QueryVector,
            new VectorSearchOptions
            {
                K = 3,
                Metric = VectorMetric.Euclidean,
                Strategy = VectorSearchStrategy.BruteForce,
            });

        hits.Select(h => h.Record.Title).Should().Equal("alpha", "beta", "gamma");
        hits[0].Distance.Should().BeApproximately(0.0, 1e-6,
            "euclidean distance to an identical vector is 0 — proves ORDER BY ASC is right");
        hits[0].Distance.Should().BeLessThan(hits[1].Distance);
    }

    [Fact]
    public async Task Bruteforce_manhattan_executes_live()
    {
        if (TrySkip()) return;

        await using var conn = await SeedAsync();
        var executor = new DefaultSurrealExecutor(conn);

        var hits = await executor.VectorSearchAsync<VecSearchDoc>(
            "embedding", QueryVector,
            new VectorSearchOptions
            {
                K = 1,
                Metric = VectorMetric.Manhattan,
                Strategy = VectorSearchStrategy.BruteForce,
            });

        hits.Should().ContainSingle();
        hits[0].Record.Title.Should().Be("alpha");
        hits[0].Distance.Should().BeApproximately(0.0, 1e-6);
    }

    [Fact]
    public async Task Bruteforce_with_filter_predicate_executes_live()
    {
        if (TrySkip()) return;

        await using var conn = await SeedAsync();
        var executor = new DefaultSurrealExecutor(conn);

        var hits = await executor.VectorSearchAsync<VecSearchDoc>(
            "embedding", QueryVector,
            new VectorSearchOptions
            {
                K = 4,
                Strategy = VectorSearchStrategy.BruteForce,
                Filter = SurrealQuery.Of("title = $t").WithParam("t", "gamma"),
            });

        hits.Should().ContainSingle();
        hits[0].Record.Title.Should().Be("gamma");
    }
}
