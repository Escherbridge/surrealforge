using System;
using System.Linq;
using FluentAssertions;
using SurrealForge.Client.Query;
using Xunit;

namespace SurrealForge.Client.Tests.Query;

/// <summary>
/// Phase 3 fluent clause builders — Where / OrderBy / Limit / Start / Return /
/// Fetch. Each is verified to:
/// 1. Append the correct SQL fragment.
/// 2. Return a NEW SurrealQuery (the receiver is never mutated).
/// 3. Compose cleanly with the others (chained builders).
/// </summary>
public sealed class SurrealQueryClauseBuilderTests
{
    // ─── Where ────────────────────────────────────────────────────────────────

    [Fact]
    public void Where_appends_clause_when_none_present()
    {
        var q = SurrealQuery.Of("SELECT * FROM wallet")
                            .Where("owner = $owner", new { owner = "avatar:1" });

        q.Sql.Should().Be("SELECT * FROM wallet WHERE owner = $owner");
        q.Params.Should().ContainKey("owner").WhoseValue.Should().Be("avatar:1");
    }

    [Fact]
    public void Where_appends_AND_when_existing_where_present()
    {
        var q = SurrealQuery.Of("SELECT * FROM wallet WHERE chain = $chain")
                            .WithParam("chain", "algorand")
                            .Where("owner = $owner", new { owner = "avatar:1" });

        q.Sql.Should().Be("SELECT * FROM wallet WHERE chain = $chain AND owner = $owner");
        q.Params.Should().HaveCount(2);
    }

    [Fact]
    public void Where_does_not_mutate_receiver()
    {
        var original = SurrealQuery.Of("SELECT * FROM wallet");
        var derived  = original.Where("id = $id", new { id = "x" });

        original.Sql.Should().Be("SELECT * FROM wallet");
        original.Params.Should().BeEmpty();
        derived.Sql.Should().Contain("WHERE id = $id");
    }

    [Fact]
    public void Where_rejects_empty_predicate()
    {
        var act = () => SurrealQuery.Of("SELECT * FROM wallet").Where("  ");
        act.Should().Throw<ArgumentException>().WithMessage("*WHERE predicate*");
    }

    // ─── OrderBy ──────────────────────────────────────────────────────────────

    [Fact]
    public void OrderBy_appends_ASC_by_default()
    {
        var q = SurrealQuery.Of("SELECT * FROM wallet").OrderBy("created_at");

        q.Sql.Should().Be("SELECT * FROM wallet ORDER BY created_at ASC");
    }

    [Fact]
    public void OrderBy_appends_DESC_when_requested()
    {
        var q = SurrealQuery.Of("SELECT * FROM wallet").OrderBy("created_at", OrderDirection.Desc);

        q.Sql.Should().Be("SELECT * FROM wallet ORDER BY created_at DESC");
    }

    [Fact]
    public void OrderBy_rejects_field_with_whitespace()
    {
        // Smuggling a clause through the field argument would alter ordering
        // and potentially semantics; the field path is identifier-validated.
        var act = () => SurrealQuery.Of("SELECT * FROM wallet").OrderBy("created_at; DROP TABLE x");

        act.Should().Throw<ArgumentException>().WithMessage("*Field path*");
    }

    [Fact]
    public void OrderBy_accepts_dotted_path()
    {
        var q = SurrealQuery.Of("SELECT * FROM wallet").OrderBy("metadata.tier");

        q.Sql.Should().Be("SELECT * FROM wallet ORDER BY metadata.tier ASC");
    }

    // ─── Limit / Start ────────────────────────────────────────────────────────

    [Fact]
    public void Limit_appends_clause()
    {
        var q = SurrealQuery.Of("SELECT * FROM wallet").Limit(25);
        q.Sql.Should().Be("SELECT * FROM wallet LIMIT 25");
    }

    [Fact]
    public void Limit_rejects_negative_value()
    {
        var act = () => SurrealQuery.Of("SELECT * FROM wallet").Limit(-1);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Start_appends_clause()
    {
        var q = SurrealQuery.Of("SELECT * FROM wallet").Start(100);
        q.Sql.Should().Be("SELECT * FROM wallet START 100");
    }

    [Fact]
    public void Start_rejects_negative_value()
    {
        var act = () => SurrealQuery.Of("SELECT * FROM wallet").Start(-1);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ─── Return ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(ReturnClause.Before, "BEFORE")]
    [InlineData(ReturnClause.After,  "AFTER")]
    [InlineData(ReturnClause.Diff,   "DIFF")]
    [InlineData(ReturnClause.None,   "NONE")]
    public void Return_emits_correct_token_for_each_enum_value(ReturnClause clause, string token)
    {
        var q = SurrealQuery.Of("UPDATE wallet SET balance = $b")
                            .WithParam("b", 100)
                            .Return(clause);

        q.Sql.Should().EndWith("RETURN " + token);
    }

    [Theory]
    [InlineData("before", "BEFORE")]
    [InlineData("AFTER",  "AFTER")]
    [InlineData("diff",   "DIFF")]
    [InlineData("None",   "NONE")]
    public void Return_string_overload_is_case_insensitive(string input, string expectedToken)
    {
        var q = SurrealQuery.Of("UPDATE wallet SET balance = $b")
                            .WithParam("b", 100)
                            .Return(input);

        q.Sql.Should().EndWith("RETURN " + expectedToken);
    }

    [Fact]
    public void Return_string_overload_rejects_arbitrary_token()
    {
        var act = () => SurrealQuery.Of("UPDATE wallet SET balance = $b")
                                    .WithParam("b", 100)
                                    .Return("EVERYTHING");

        act.Should().Throw<ArgumentException>().WithMessage("*BEFORE, AFTER, DIFF, NONE*");
    }

    // ─── Fetch ────────────────────────────────────────────────────────────────

    [Fact]
    public void Fetch_appends_path()
    {
        var q = SurrealQuery.Of("SELECT * FROM wallet").Fetch("owner");
        q.Sql.Should().Be("SELECT * FROM wallet FETCH owner");
    }

    [Fact]
    public void Fetch_accepts_graph_arrow_path()
    {
        var q = SurrealQuery.Of("SELECT * FROM wallet").Fetch("owner->avatar->profile");
        q.Sql.Should().Be("SELECT * FROM wallet FETCH owner->avatar->profile");
    }

    [Fact]
    public void Fetch_rejects_whitespace_injection()
    {
        var act = () => SurrealQuery.Of("SELECT * FROM wallet").Fetch("owner; DROP");
        act.Should().Throw<ArgumentException>().WithMessage("*Field path*");
    }

    // ─── Composition — chained builders ──────────────────────────────────────

    [Fact]
    public void Builders_compose_in_chain()
    {
        var q = SurrealQuery.Of("SELECT * FROM wallet")
                            .Where("chain = $chain", new { chain = "algorand" })
                            .Where("owner = $owner", new { owner = "avatar:1" })
                            .OrderBy("created_at", OrderDirection.Desc)
                            .Limit(50)
                            .Start(10)
                            .Fetch("owner");

        q.Sql.Should().Be(
            "SELECT * FROM wallet " +
            "WHERE chain = $chain " +
            "AND owner = $owner " +
            "ORDER BY created_at DESC " +
            "LIMIT 50 " +
            "START 10 " +
            "FETCH owner");
        q.Params.Should().HaveCount(2);
        q.Params.Should().ContainKey("chain").And.ContainKey("owner");
    }

    [Fact]
    public void Builders_each_return_new_instance_no_mutation()
    {
        var a = SurrealQuery.Of("SELECT * FROM wallet");
        var b = a.Where("id = $id", new { id = "x" });
        var c = b.OrderBy("created_at");
        var d = c.Limit(10);

        // Each variable holds its own immutable snapshot.
        a.Sql.Should().Be("SELECT * FROM wallet");
        b.Sql.Should().Be("SELECT * FROM wallet WHERE id = $id");
        c.Sql.Should().Be("SELECT * FROM wallet WHERE id = $id ORDER BY created_at ASC");
        d.Sql.Should().Be("SELECT * FROM wallet WHERE id = $id ORDER BY created_at ASC LIMIT 10");

        // And distinct object identity (every builder allocates a new query).
        ReferenceEquals(a, b).Should().BeFalse();
        ReferenceEquals(b, c).Should().BeFalse();
        ReferenceEquals(c, d).Should().BeFalse();
    }

    // ─── Build / ToString ─────────────────────────────────────────────────────

    [Fact]
    public void Build_returns_the_full_sql_body()
    {
        var q = SurrealQuery.Of("SELECT * FROM wallet").Limit(5);
        q.Build().Should().Be(q.Sql);
    }
}
