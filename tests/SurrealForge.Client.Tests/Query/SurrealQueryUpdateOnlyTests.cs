using System;
using System.Text.Json;
using FluentAssertions;
using SurrealForge.Client;
using SurrealForge.Client.Query;
using Xunit;

namespace SurrealForge.Client.Tests.Query;

/// <summary>
/// G2 conditional-state-transition primitive — verifies the
/// <see cref="SurrealQuery.UpdateOnly"/> shape AND the read-side
/// <see cref="SurrealStatementResultExtensions.EnsureSingleAffected{T}"/>
/// single-row enforcement. Closes code-review C5 use-case.
/// </summary>
public sealed class SurrealQueryUpdateOnlyTests
{
    // ─── Emit shape ──────────────────────────────────────────────────────────

    [Fact]
    public void UpdateOnly_emits_parameterized_thing_address_with_where_and_set()
    {
        var q = SurrealQuery.UpdateOnly("operation_log", "abc123")
                            .Where("status", "pending")
                            .Set("status", "complete");

        q.Sql.Should().Be(
            "UPDATE type::record($_t, $_id) " +
            "SET status = $_s_status WHERE status = $_w_status " +
            "RETURN AFTER");

        q.Params.Should().Contain(new System.Collections.Generic.KeyValuePair<string, object?>("_t", "operation_log"));
        q.Params.Should().Contain(new System.Collections.Generic.KeyValuePair<string, object?>("_id", "abc123"));
        q.Params.Should().Contain(new System.Collections.Generic.KeyValuePair<string, object?>("_w_status", "pending"));
        q.Params.Should().Contain(new System.Collections.Generic.KeyValuePair<string, object?>("_s_status", "complete"));
    }

    [Fact]
    public void UpdateOnly_validates_table_name_through_identifier_policy()
    {
        var act = () => SurrealQuery.UpdateOnly("INVALID", "x")
                                    .Where("status", "p")
                                    .Set("status", "c");

        act.Should().Throw<ArgumentException>().WithMessage("*table*");
    }

    [Fact]
    public void UpdateOnly_rejects_reserved_table_word()
    {
        var act = () => SurrealQuery.UpdateOnly("select", "x")
                                    .Where("status", "p")
                                    .Set("status", "c");

        act.Should().Throw<SurrealIdentifierException>().WithMessage("*reserved word*");
    }

    [Fact]
    public void UpdateOnly_rejects_empty_id()
    {
        var act = () => SurrealQuery.UpdateOnly("operation_log", "  ");
        act.Should().Throw<ArgumentException>().WithMessage("*Record id*");
    }

    [Fact]
    public void UpdateOnly_set_without_where_throws()
    {
        // .Where is a required step in the builder chain. .Set without it is
        // a builder misuse — the G2 contract requires a predicate.
        var act = () => new System.Action(() =>
        {
            var builder = SurrealQuery.UpdateOnly("operation_log", "abc");
            builder.Set("status", "complete");
        }).Invoke();

        act.Should().Throw<InvalidOperationException>().WithMessage("*Where*");
    }

    [Fact]
    public void UpdateOnly_validates_where_field_name()
    {
        var act = () => SurrealQuery.UpdateOnly("operation_log", "x")
                                    .Where("status; DROP", "pending")
                                    .Set("status", "complete");

        act.Should().Throw<ArgumentException>().WithMessage("*Field path*");
    }

    [Fact]
    public void UpdateOnly_validates_set_field_name()
    {
        var act = () => SurrealQuery.UpdateOnly("operation_log", "x")
                                    .Where("status", "pending")
                                    .Set("status; DROP", "complete");

        act.Should().Throw<ArgumentException>().WithMessage("*Field path*");
    }

    [Fact]
    public void UpdateOnly_validates_param_contract()
    {
        // Strict validation: every $param in the SQL must have a binding,
        // and no extras. The builder produces both halves of the contract.
        var q = SurrealQuery.UpdateOnly("operation_log", "abc")
                            .Where("status", "pending")
                            .Set("status", "complete");

        var act = () => q.Validate(strict: true);
        act.Should().NotThrow();
    }

    // ─── Read-side single-row enforcement (EnsureSingleAffected<T>) ──────────

    private static SurrealStatementResult OkStatement(string resultJson) =>
        new SurrealStatementResult
        {
            Status = "OK",
            Result = JsonDocument.Parse(resultJson).RootElement.Clone(),
            Time = "0µs",
        };

    private static SurrealStatementResult ErrStatement(string detail) =>
        new SurrealStatementResult
        {
            Status = "ERR",
            Detail = detail,
            Time = "0µs",
        };

    private sealed record Row(string Id, string Status);

    [Fact]
    public void EnsureSingleAffected_succeeds_when_exactly_one_row_returned()
    {
        var stmt = OkStatement("[{\"id\":\"operation_log:abc\",\"status\":\"complete\"}]");

        var row = stmt.EnsureSingleAffected<Row>();

        row.Should().NotBeNull();
        row.Status.Should().Be("complete");
    }

    [Fact]
    public void EnsureSingleAffected_throws_when_zero_rows_affected()
    {
        var stmt = OkStatement("[]");

        var act = () => stmt.EnsureSingleAffected<Row>();

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*zero*");
    }

    [Fact]
    public void EnsureSingleAffected_throws_when_more_than_one_row_affected()
    {
        var stmt = OkStatement(
            "[{\"id\":\"operation_log:a\",\"status\":\"complete\"}," +
            "{\"id\":\"operation_log:b\",\"status\":\"complete\"}]");

        var act = () => stmt.EnsureSingleAffected<Row>();

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*2*");
    }

    [Fact]
    public void EnsureSingleAffected_throws_when_statement_failed()
    {
        var stmt = ErrStatement("constraint violation");

        var act = () => stmt.EnsureSingleAffected<Row>();

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*failed*constraint violation*");
    }

    // ─── AffectedCount extension ─────────────────────────────────────────────

    [Fact]
    public void AffectedCount_returns_array_length_for_array_payload()
    {
        var stmt = OkStatement("[{\"x\":1},{\"x\":2},{\"x\":3}]");
        stmt.AffectedCount().Should().Be(3);
    }

    [Fact]
    public void AffectedCount_returns_one_for_object_payload()
    {
        var stmt = OkStatement("{\"x\":1}");
        stmt.AffectedCount().Should().Be(1);
    }

    [Fact]
    public void AffectedCount_returns_zero_for_null_payload()
    {
        var stmt = OkStatement("null");
        stmt.AffectedCount().Should().Be(0);
    }

    [Fact]
    public void AffectedCount_returns_zero_for_failed_statement()
    {
        var stmt = ErrStatement("oops");
        stmt.AffectedCount().Should().Be(0);
    }
}
