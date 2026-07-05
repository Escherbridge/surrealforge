using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using SurrealForge.Client;
using SurrealForge.Client.Connection;
using SurrealForge.Client.Tests; // FakeHttpHandler

namespace SurrealForge.Client.Tests.Connection;

/// <summary>
/// HIGH#2 regression tests: <see cref="HttpSurrealConnection.ExecuteRawAsync"/>
/// only retries on transport failure when the SurrealQL statement is
/// idempotent (SELECT / INFO / LIVE / KILL / BEGIN TRANSACTION). Non-idempotent
/// statements bubble the first transport error to the caller so the bridge
/// value path keeps its exactly-once contract.
/// </summary>
public class HttpSurrealConnectionRetryGateTests
{
    private static SurrealConnectionOptions Opts() => new()
    {
        Endpoint       = "http://localhost:8442",
        Namespace      = "test",
        Database       = "test",
        User           = "root",
        Password       = "root",
        MaxRetries     = 3,                              // allow up to 2 retries
        BaseRetryDelay = TimeSpan.FromMilliseconds(1),   // keep tests fast
        JitterRatio    = 0.0,
    };

    private const string OkBody = """[ { "status": "OK", "time": "1µs", "result": [] } ]""";

    // ─── Positive case: idempotent statement IS retried ──────────────────────

    [Fact]
    public async Task Retries_SELECT_on_transport_failure()
    {
        var handler = new FakeHttpHandler();
        bool firstSent = false;
        handler.Enqueue(_ =>
        {
            firstSent = true;
            throw new HttpRequestException("synthetic socket error");
        });
        handler.EnqueueOk(OkBody); // second attempt succeeds

        await using var conn = new HttpSurrealConnection(handler, Opts());
        var resp = await conn.ExecuteRawAsync("SELECT * FROM wallet;");

        firstSent.Should().BeTrue();
        resp[0].IsOk.Should().BeTrue("the second attempt is the OK reply we scripted");
        handler.Requests.Should().HaveCount(2,
            "SELECT is idempotent — the transport failure on attempt 1 must trigger one retry");
    }

    // ─── Negative cases: non-idempotent statements are NEVER retried ─────────

    [Fact]
    public async Task Does_not_retry_COMMIT_TRANSACTION_on_transport_failure()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue(_ => throw new HttpRequestException("synthetic socket error"));
        handler.EnqueueOk(OkBody); // would-be retry — must NOT fire

        await using var conn = new HttpSurrealConnection(handler, Opts());
        var act = async () => await conn.ExecuteRawAsync("COMMIT TRANSACTION;");

        await act.Should().ThrowAsync<HttpRequestException>(
            "COMMIT is non-idempotent — the transport error must bubble to the caller without a silent retry");
        handler.Requests.Should().HaveCount(1, "no retry must fire for COMMIT");
    }

    [Fact]
    public async Task Does_not_retry_CREATE_on_transport_failure()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue(_ => throw new HttpRequestException("synthetic socket error"));
        handler.EnqueueOk(OkBody);

        await using var conn = new HttpSurrealConnection(handler, Opts());
        var act = async () => await conn.ExecuteRawAsync("CREATE wallet CONTENT { id: 'abc' };");

        await act.Should().ThrowAsync<HttpRequestException>();
        handler.Requests.Should().HaveCount(1, "no retry must fire for CREATE");
    }

    [Fact]
    public async Task Does_not_retry_UPDATE_on_transport_failure()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue(_ => throw new HttpRequestException("synthetic socket error"));
        handler.EnqueueOk(OkBody);

        await using var conn = new HttpSurrealConnection(handler, Opts());
        var act = async () => await conn.ExecuteRawAsync("UPDATE wallet:abc SET amount = 100;");

        await act.Should().ThrowAsync<HttpRequestException>();
        handler.Requests.Should().HaveCount(1, "no retry must fire for UPDATE");
    }

    [Fact]
    public async Task Does_not_retry_DELETE_on_transport_failure()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue(_ => throw new HttpRequestException("synthetic socket error"));
        handler.EnqueueOk(OkBody);

        await using var conn = new HttpSurrealConnection(handler, Opts());
        var act = async () => await conn.ExecuteRawAsync("DELETE wallet:abc;");

        await act.Should().ThrowAsync<HttpRequestException>();
        handler.Requests.Should().HaveCount(1, "no retry must fire for DELETE");
    }

    // ─── IsIdempotentSql token-level assertions ──────────────────────────────

    [Theory]
    [InlineData("SELECT * FROM wallet")]
    [InlineData("select * from wallet")]
    [InlineData("  SELECT 1")]
    [InlineData("-- a comment\nSELECT 1")]
    [InlineData("/* block */ SELECT 1")]
    [InlineData("INFO FOR DB")]
    [InlineData("LIVE SELECT * FROM wallet")]
    [InlineData("KILL 'abc'")]
    [InlineData("BEGIN TRANSACTION;")]
    [InlineData("BEGIN;")]
    public void IsIdempotentSql_returns_true_for_safe_statements(string sql)
    {
        HttpSurrealConnection.IsIdempotentSql(sql).Should().BeTrue();
    }

    [Theory]
    [InlineData("CREATE wallet CONTENT { id: 'a' }")]
    [InlineData("UPDATE wallet:a SET x = 1")]
    [InlineData("DELETE wallet:a")]
    [InlineData("INSERT INTO wallet (id) VALUES ('a')")]
    [InlineData("RELATE wallet:a -> owns -> wallet:b")]
    [InlineData("UPSERT wallet:a SET x = 1")]
    [InlineData("MERGE wallet:a CONTENT {}")]
    [InlineData("COMMIT TRANSACTION;")]
    [InlineData("CANCEL TRANSACTION;")]
    [InlineData("DEFINE TABLE wallet SCHEMAFULL")]
    [InlineData("REMOVE TABLE wallet")]
    [InlineData("USE NS azoa DB test")]
    [InlineData("SELECTOR x")]      // pseudo-keyword — must not be matched as SELECT
    [InlineData("BEGINS")]          // not BEGIN
    [InlineData("")]
    [InlineData("   ")]
    public void IsIdempotentSql_returns_false_for_writes_and_unknown_prefixes(string sql)
    {
        HttpSurrealConnection.IsIdempotentSql(sql).Should().BeFalse();
    }
}
