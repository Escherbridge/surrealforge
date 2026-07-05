using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using SurrealForge.Client;
using SurrealForge.Client.Connection;
using SurrealForge.Client.Query;
using SurrealForge.Client.Transaction;

namespace SurrealForge.Client.Tests.Query;

/// <summary>
/// HIGH#4 — verifies the ISurrealExecutor implementation the DI extension
/// promised. DefaultSurrealExecutor wraps ISurrealConnection.ExecuteRawAsync
/// and folds the response according to the per-method contract:
///   * QueryAsync — first statement's deserialized rows.
///   * QuerySingleAsync — zero-or-one row (throws on multi).
///   * ExecuteAsync — full SurrealResponse (multi-statement aware).
/// </summary>
public class DefaultSurrealExecutorTests
{
    private sealed record Wallet(string Id, int Amount);

    private static SurrealResponse SingleStatement(string status, string resultJson)
    {
        // Plain interpolation (no raw string) — keeps the brace-counting
        // requirements off the table for the single-statement helper.
        var body = "[ { \"status\": \"" + status + "\", \"time\": \"1µs\", \"result\": " + resultJson + " } ]";
        return SurrealResponse.FromJson(body);
    }

    private static SurrealResponse MultiStatement(params (string status, string resultJson)[] slots)
    {
        var parts = new List<string>(slots.Length);
        foreach (var s in slots)
            parts.Add("{ \"status\": \"" + s.status + "\", \"time\": \"1µs\", \"result\": " + s.resultJson + " }");
        return SurrealResponse.FromJson("[" + string.Join(",", parts) + "]");
    }

    // ─── QueryAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task QueryAsync_delegates_to_connection_and_returns_first_statement_values()
    {
        var conn = new Mock<ISurrealConnection>(MockBehavior.Strict);
        conn.Setup(c => c.ExecuteRawAsync(
                It.Is<string>(s => s.Contains("SELECT") && s.Contains("$id")),
                It.IsAny<object>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(SingleStatement("OK", """[ { "Id": "abc", "Amount": 100 } ]"""))
            .Verifiable();

        var executor = new DefaultSurrealExecutor(conn.Object);
        var query    = SurrealQuery.Of("SELECT * FROM wallet WHERE id = $id")
                                   .WithParam("id", "abc");

        var rows = await executor.QueryAsync<Wallet>(query);

        rows.Should().ContainSingle()
                     .Which.Should().BeEquivalentTo(new Wallet("abc", 100));
        conn.Verify();
    }

    [Fact]
    public async Task QueryAsync_rejects_multi_statement_query()
    {
        var conn  = new Mock<ISurrealConnection>(MockBehavior.Strict);
        var multi = SurrealQuery.Combine(
            SurrealQuery.Of("SELECT * FROM wallet"),
            SurrealQuery.Of("SELECT * FROM wallet"));

        var executor = new DefaultSurrealExecutor(conn.Object);
        var act      = async () => await executor.QueryAsync<Wallet>(multi);

        await act.Should().ThrowAsync<ArgumentException>()
                 .WithMessage("*single-statement*");
    }

    // ─── QuerySingleAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task QuerySingleAsync_returns_null_when_zero_rows()
    {
        var conn = new Mock<ISurrealConnection>(MockBehavior.Strict);
        conn.Setup(c => c.ExecuteRawAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SingleStatement("OK", "[]"));

        var executor = new DefaultSurrealExecutor(conn.Object);
        var query    = SurrealQuery.Of("SELECT * FROM wallet WHERE id = $id")
                                   .WithParam("id", "missing");

        var single = await executor.QuerySingleAsync<Wallet>(query);
        single.Should().BeNull("zero-or-one is QuerySingleAsync's contract; zero means null");
    }

    [Fact]
    public async Task QuerySingleAsync_returns_row_when_exactly_one()
    {
        var conn = new Mock<ISurrealConnection>(MockBehavior.Strict);
        conn.Setup(c => c.ExecuteRawAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SingleStatement("OK", """[ { "Id": "abc", "Amount": 42 } ]"""));

        var executor = new DefaultSurrealExecutor(conn.Object);
        var query    = SurrealQuery.Of("SELECT * FROM wallet WHERE id = $id")
                                   .WithParam("id", "abc");

        var single = await executor.QuerySingleAsync<Wallet>(query);
        single.Should().BeEquivalentTo(new Wallet("abc", 42));
    }

    [Fact]
    public async Task QuerySingleAsync_throws_when_more_than_one_row()
    {
        var conn = new Mock<ISurrealConnection>(MockBehavior.Strict);
        conn.Setup(c => c.ExecuteRawAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SingleStatement("OK",
                """[ { "Id": "a", "Amount": 1 }, { "Id": "b", "Amount": 2 } ]"""));

        var executor = new DefaultSurrealExecutor(conn.Object);
        var query    = SurrealQuery.Of("SELECT * FROM wallet");

        var act = async () => await executor.QuerySingleAsync<Wallet>(query);
        await act.Should().ThrowAsync<InvalidOperationException>()
                 .WithMessage("*expected zero or one*");
    }

    [Fact]
    public async Task QuerySingleAsync_propagates_statement_err()
    {
        var conn = new Mock<ISurrealConnection>(MockBehavior.Strict);
        conn.Setup(c => c.ExecuteRawAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SurrealResponse.FromJson(
                """[ { "status": "ERR", "time": "1µs", "result": null, "detail": "synthetic" } ]"""));

        var executor = new DefaultSurrealExecutor(conn.Object);
        var query    = SurrealQuery.Of("SELECT * FROM wallet");

        var act = async () => await executor.QuerySingleAsync<Wallet>(query);
        await act.Should().ThrowAsync<SurrealStatementException>();
    }

    // ─── ExecuteAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_returns_full_multi_statement_response()
    {
        var conn = new Mock<ISurrealConnection>(MockBehavior.Strict);
        conn.Setup(c => c.ExecuteRawAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MultiStatement(
                ("OK", """[ { "Id": "a", "Amount": 1 } ]"""),
                ("OK", "[]"),
                ("OK", "null")));

        var executor = new DefaultSurrealExecutor(conn.Object);
        var combined = SurrealQuery.Combine(
            SurrealQuery.Of("SELECT * FROM wallet"),
            SurrealQuery.Of("SELECT * FROM wallet WHERE id = $id").WithParam("id", "x"),
            SurrealQuery.Of("DELETE wallet:tmp"));

        var resp = await executor.ExecuteAsync(combined);
        resp.Count.Should().Be(3,
            "ExecuteAsync must surface per-statement slots — no silent collapse onto the first result");
        resp[0].IsOk.Should().BeTrue();
        resp[1].IsOk.Should().BeTrue();
        resp[2].IsOk.Should().BeTrue();
    }

    // ─── Validation ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Dispatch_runs_strict_validation_before_send()
    {
        var conn = new Mock<ISurrealConnection>(MockBehavior.Strict);
        // Strict validate should throw BEFORE ExecuteRawAsync is called.
        var executor = new DefaultSurrealExecutor(conn.Object);
        var query    = SurrealQuery.Of("SELECT * FROM wallet WHERE id = $id"); // no params!

        var act = async () => await executor.QueryAsync<Wallet>(query);
        await act.Should().ThrowAsync<SurrealQueryValidationException>();
        conn.VerifyNoOtherCalls();
    }
}
