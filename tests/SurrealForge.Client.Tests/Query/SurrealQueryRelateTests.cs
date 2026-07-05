using System;
using FluentAssertions;
using SurrealForge.Client.Query;
using Xunit;

namespace SurrealForge.Client.Tests.Query;

/// <summary>
/// Graph helper — RELATE statement emission. Verifies the
/// <see cref="SurrealQuery.Relate"/> shape, identifier validation on the
/// edge table, and that the content payload is bound as a parameter (not
/// interpolated).
/// </summary>
public sealed class SurrealQueryRelateTests
{
    [Fact]
    public void Relate_emits_parameterized_endpoints_and_content_binding()
    {
        var from = SurrealRecordId.Create("avatar", "alice");
        var to   = SurrealRecordId.Create("avatar", "bob");

        var q = SurrealQuery.Relate(from, "follows", to)
                            .WithContent(new { weight = 5, created_at = "2026-05-21" });

        q.Sql.Should().Be(
            "RELATE type::record($_from_t, $_from_id) -> follows -> " +
            "type::record($_to_t, $_to_id) CONTENT $_content");

        q.Params.Should().ContainKey("_from_t").WhoseValue.Should().Be("avatar");
        q.Params.Should().ContainKey("_from_id").WhoseValue.Should().Be("alice");
        q.Params.Should().ContainKey("_to_t").WhoseValue.Should().Be("avatar");
        q.Params.Should().ContainKey("_to_id").WhoseValue.Should().Be("bob");
        q.Params.Should().ContainKey("_content");
        q.Params["_content"].Should().NotBeNull();
    }

    [Fact]
    public void Relate_validates_edge_table_name()
    {
        var from = SurrealRecordId.Create("avatar", "alice");
        var to   = SurrealRecordId.Create("avatar", "bob");

        var act = () => SurrealQuery.Relate(from, "INVALID-EDGE", to);

        act.Should().Throw<ArgumentException>().WithMessage("*table*");
    }

    [Fact]
    public void Relate_rejects_reserved_edge_table()
    {
        var from = SurrealRecordId.Create("avatar", "alice");
        var to   = SurrealRecordId.Create("avatar", "bob");

        var act = () => SurrealQuery.Relate(from, "select", to);

        act.Should().Throw<SurrealIdentifierException>().WithMessage("*reserved word*");
    }

    [Fact]
    public void Relate_rejects_null_content()
    {
        var from = SurrealRecordId.Create("avatar", "alice");
        var to   = SurrealRecordId.Create("avatar", "bob");

        var act = () => SurrealQuery.Relate(from, "follows", to).WithContent(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Relate_query_passes_strict_validation()
    {
        // All five params ($_from_t/_from_id/_to_t/_to_id/_content) are
        // referenced in the SQL — strict mode should be satisfied.
        var from = SurrealRecordId.Create("avatar", "alice");
        var to   = SurrealRecordId.Create("avatar", "bob");

        var q = SurrealQuery.Relate(from, "follows", to).WithContent(new { weight = 1 });

        var act = () => q.Validate(strict: true);
        act.Should().NotThrow();
    }

    // ─── SurrealRecordId ─────────────────────────────────────────────────────

    [Fact]
    public void SurrealRecordId_renders_as_table_colon_id()
    {
        var rid = SurrealRecordId.Create("wallet", "abc");
        rid.ToString().Should().Be("wallet:abc");
        rid.Table.Should().Be("wallet");
        rid.Id.Should().Be("abc");
    }

    [Fact]
    public void SurrealRecordId_rejects_invalid_parts()
    {
        var act1 = () => SurrealRecordId.Create("INVALID", "abc");
        var act2 = () => SurrealRecordId.Create("wallet", "");
        var act3 = () => SurrealRecordId.Create("select", "abc");

        act1.Should().Throw<ArgumentException>();
        act2.Should().Throw<ArgumentException>();
        act3.Should().Throw<SurrealIdentifierException>().WithMessage("*reserved word*");
    }

    [Fact]
    public void SurrealRecordId_equality_is_structural()
    {
        var a = SurrealRecordId.Create("wallet", "abc");
        var b = SurrealRecordId.Create("wallet", "abc");
        var c = SurrealRecordId.Create("wallet", "xyz");

        a.Equals(b).Should().BeTrue();
        (a == b).Should().BeTrue();
        a.Equals(c).Should().BeFalse();
        (a != c).Should().BeTrue();
        a.GetHashCode().Should().Be(b.GetHashCode());
    }
}
