using System;
using System.Text.Json;
using FluentAssertions;
using SurrealForge.Client;
using SurrealForge.Client.Query;
using Xunit;

namespace SurrealForge.Client.Tests.Query;

/// <summary>
/// Multi-statement composition — closes code-review C5 design root.
/// Verifies that the SurrealQuery.Combine builder produces a single
/// statement-joined SQL body and that SurrealResponse fans out per-statement
/// results so no result is silently swallowed.
/// </summary>
public sealed class SurrealQueryCombineTests
{
    // ─── Combine — emit shape ────────────────────────────────────────────────

    [Fact]
    public void Combine_joins_statements_with_semicolon_separators()
    {
        var q1 = SurrealQuery.Of("SELECT * FROM wallet")
                              .WithParams(new System.Collections.Generic.Dictionary<string, object?>());
        var q2 = SurrealQuery.Of("SELECT * FROM avatar");
        var q3 = SurrealQuery.Of("SELECT * FROM bridge_tx");

        var combined = SurrealQuery.Combine(q1, q2, q3);

        combined.Sql.Should().Be(
            "SELECT * FROM wallet; SELECT * FROM avatar; SELECT * FROM bridge_tx;");
        combined.IsMultiStatement.Should().BeTrue();
    }

    [Fact]
    public void Combine_merges_parameter_bags()
    {
        var q1 = SurrealQuery.Of("SELECT * FROM wallet WHERE owner = $a").WithParam("a", "x");
        var q2 = SurrealQuery.Of("SELECT * FROM avatar WHERE id = $b").WithParam("b", "y");

        var combined = SurrealQuery.Combine(q1, q2);

        combined.Params.Should().HaveCount(2)
                .And.ContainKey("a")
                .And.ContainKey("b");
    }

    [Fact]
    public void Combine_requires_at_least_two_queries()
    {
        var q1 = SurrealQuery.Of("SELECT * FROM wallet");

        var actNone = () => SurrealQuery.Combine();
        var actOne  = () => SurrealQuery.Combine(q1);

        actNone.Should().Throw<ArgumentException>().WithMessage("*at least two*");
        actOne.Should().Throw<ArgumentException>().WithMessage("*at least two*");
    }

    [Fact]
    public void Combine_rejects_null_query_in_list()
    {
        var q1 = SurrealQuery.Of("SELECT * FROM wallet");
        var act = () => SurrealQuery.Combine(q1, null!);

        act.Should().Throw<ArgumentException>().WithMessage("*null*");
    }

    [Fact]
    public void Combine_rejects_already_combined_query_no_nesting()
    {
        var q1 = SurrealQuery.Of("SELECT * FROM wallet");
        var q2 = SurrealQuery.Of("SELECT * FROM avatar");
        var q3 = SurrealQuery.Of("SELECT * FROM bridge_tx");

        var first  = SurrealQuery.Combine(q1, q2);
        var actNest = () => SurrealQuery.Combine(first, q3);

        actNest.Should().Throw<ArgumentException>().WithMessage("*nest*");
    }

    // ─── SurrealResponse fan-out — 3-statement Combine ───────────────────────

    [Fact]
    public void SurrealResponse_indexes_each_statement_independently()
    {
        // Simulate the HTTP transport's parsed response shape for a 3-stmt
        // Combine. Each statement carries its own status + result, and the
        // consumer can address each by index without one swallowing another.
        var json =
            "[" +
            "{\"status\":\"OK\",\"time\":\"1µs\",\"result\":[{\"id\":\"wallet:1\"}]}," +
            "{\"status\":\"OK\",\"time\":\"2µs\",\"result\":[{\"id\":\"avatar:2\"},{\"id\":\"avatar:3\"}]}," +
            "{\"status\":\"OK\",\"time\":\"3µs\",\"result\":[]}" +
            "]";

        var response = SurrealResponse.FromJson(json);

        response.Should().HaveCount(3);
        response[0].IsOk.Should().BeTrue();
        response[1].IsOk.Should().BeTrue();
        response[2].IsOk.Should().BeTrue();

        response[0].AffectedCount().Should().Be(1);
        response[1].AffectedCount().Should().Be(2);
        response[2].AffectedCount().Should().Be(0);
    }

    [Fact]
    public void SurrealResponse_EnsureAllOk_throws_when_any_statement_fails()
    {
        var json =
            "[" +
            "{\"status\":\"OK\",\"result\":[]}," +
            "{\"status\":\"ERR\",\"detail\":\"constraint violation on wallet\"}" +
            "]";

        var response = SurrealResponse.FromJson(json);

        var act = () => response.EnsureAllOk();
        act.Should().Throw<Exception>().WithMessage("*constraint violation*");
    }

    [Fact]
    public void SurrealResponse_GetValues_returns_typed_rows_per_statement()
    {
        var json =
            "[" +
            "{\"status\":\"OK\",\"result\":[{\"id\":\"wallet:1\",\"owner\":\"alice\"}]}," +
            "{\"status\":\"OK\",\"result\":[{\"id\":\"wallet:2\",\"owner\":\"bob\"}," +
                                             "{\"id\":\"wallet:3\",\"owner\":\"carol\"}]}" +
            "]";

        var response = SurrealResponse.FromJson(json);

        var s0 = response.GetValues<WalletRow>(0);
        var s1 = response.GetValues<WalletRow>(1);

        s0.Should().HaveCount(1);
        s0[0].Owner.Should().Be("alice");

        s1.Should().HaveCount(2);
        s1[0].Owner.Should().Be("bob");
        s1[1].Owner.Should().Be("carol");
    }

    private sealed record WalletRow(string Id, string Owner);
}
