// SPDX-License-Identifier: MIT
// SQL-shape tests for VectorSearchAsync: both paths, param binding, no
// interpolated embedding, ORDER BY / LIMIT shape.

using FluentAssertions;
using SurrealForge.Client.Query;
using SurrealForge.Client.Schema;
using SurrealForge.Vector;

namespace SurrealForge.Vector.Tests;

public sealed class VectorSearchQueryShapeTests
{
    [SurrealTable("document")]
    public sealed class SearchDoc
    {
        public string? Title { get; set; }
    }

    private static readonly float[] Vec = { 0.25f, 0.5f, 0.75f };

    private static async Task<SurrealQuery> CaptureAsync(VectorSearchOptions options)
    {
        var fake = new FakeSurrealExecutor();
        await fake.VectorSearchAsync<SearchDoc>("embedding", Vec, options);
        fake.Queries.Should().HaveCount(1);
        return fake.Queries[0];
    }

    [Fact]
    public async Task Indexed_knn_emits_operator_with_k_and_orders_by_dist()
    {
        var q = await CaptureAsync(new VectorSearchOptions { K = 5 });

        q.Sql.Should().Be(
            "SELECT *, vector::distance::knn() AS dist FROM document " +
            "WHERE embedding <|5|> $q ORDER BY dist");
    }

    [Fact]
    public async Task Indexed_knn_with_ef_emits_k_comma_ef()
    {
        var q = await CaptureAsync(new VectorSearchOptions { K = 5, Ef = 40 });

        q.Sql.Should().Contain("embedding <|5,40|> $q");
    }

    [Fact]
    public async Task Embedding_is_always_a_bound_param_never_interpolated()
    {
        var q = await CaptureAsync(new VectorSearchOptions { K = 5 });

        q.Params.Should().ContainKey("q");
        q.Params["q"].Should().BeSameAs(Vec);
        // No float component of the embedding may appear in the SQL text.
        q.Sql.Should().NotContain("0.25").And.NotContain("0.5").And.NotContain("[");
        q.Sql.Should().Contain("$q");
    }

    [Fact]
    public async Task Indexed_knn_combines_extra_filter_into_where_and_merges_params()
    {
        var filter = SurrealQuery.Of("status = $status").WithParam("status", "published");
        var q = await CaptureAsync(new VectorSearchOptions { K = 3, Filter = filter });

        q.Sql.Should().Be(
            "SELECT *, vector::distance::knn() AS dist FROM document " +
            "WHERE embedding <|3|> $q AND (status = $status) ORDER BY dist");
        q.Params.Should().ContainKey("status").WhoseValue.Should().Be("published");
    }

    [Fact]
    public async Task Brute_force_cosine_uses_similarity_desc_with_limit()
    {
        var q = await CaptureAsync(new VectorSearchOptions
        {
            K = 5,
            Strategy = VectorSearchStrategy.BruteForce,
            Metric = VectorMetric.Cosine,
        });

        q.Sql.Should().Be(
            "SELECT *, vector::similarity::cosine(embedding, $q) AS dist FROM document " +
            "ORDER BY dist DESC LIMIT 5");
    }

    [Theory]
    [InlineData(VectorMetric.Euclidean, "vector::distance::euclidean")]
    [InlineData(VectorMetric.Manhattan, "vector::distance::manhattan")]
    public async Task Brute_force_distance_metrics_use_distance_asc_with_limit(
        VectorMetric metric, string expectedFn)
    {
        var q = await CaptureAsync(new VectorSearchOptions
        {
            K = 7,
            Strategy = VectorSearchStrategy.BruteForce,
            Metric = metric,
        });

        q.Sql.Should().Be(
            "SELECT *, " + expectedFn + "(embedding, $q) AS dist FROM document " +
            "ORDER BY dist ASC LIMIT 7");
    }

    [Fact]
    public async Task Brute_force_filter_lands_in_where_before_order_by()
    {
        var filter = SurrealQuery.Of("status = $status").WithParam("status", "published");
        var q = await CaptureAsync(new VectorSearchOptions
        {
            K = 2,
            Strategy = VectorSearchStrategy.BruteForce,
            Filter = filter,
        });

        q.Sql.Should().Be(
            "SELECT *, vector::similarity::cosine(embedding, $q) AS dist FROM document " +
            "WHERE (status = $status) ORDER BY dist DESC LIMIT 2");
    }

    [Fact]
    public async Task Results_map_record_and_dist()
    {
        var fake = new FakeSurrealExecutor
        {
            QueryJson = _ => """
                [
                  {"id":"document:a","title":"Alpha","dist":0.12},
                  {"id":"document:b","title":"Beta","dist":0.48}
                ]
                """,
        };

        var results = await fake.VectorSearchAsync<SearchDoc>("embedding", Vec, new VectorSearchOptions { K = 2 });

        results.Should().HaveCount(2);
        results[0].Record.Title.Should().Be("Alpha");
        results[0].Distance.Should().BeApproximately(0.12, 1e-9);
        results[1].Record.Title.Should().Be("Beta");
        results[1].Distance.Should().BeApproximately(0.48, 1e-9);
    }

    [Fact]
    public async Task Filter_binding_q_is_rejected()
    {
        var filter = SurrealQuery.Of("other = $q").WithParam("q", "clash");
        var act = () => CaptureAsync(new VectorSearchOptions { K = 5, Filter = filter });

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*must not bind $q*");
    }

    [Fact]
    public async Task Invalid_field_name_is_rejected()
    {
        var fake = new FakeSurrealExecutor();
        var act = () => fake.VectorSearchAsync<SearchDoc>("embedding; DROP TABLE x", Vec);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*not a valid SurrealDB column path*");
    }

    [Fact]
    public async Task Non_positive_k_is_rejected()
    {
        var act = () => CaptureAsync(new VectorSearchOptions { K = 0 });

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }
}
