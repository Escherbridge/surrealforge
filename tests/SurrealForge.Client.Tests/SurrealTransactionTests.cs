using System.Threading.Tasks;
using FluentAssertions;
using SurrealForge.Client.Connection;

namespace SurrealForge.Client.Tests;

/// <summary>
/// Transaction contract on SurrealDB 3.x: statements executed while a txn is
/// open are buffered and flushed as ONE <c>BEGIN; ...; COMMIT;</c> request on
/// commit; dispose-without-commit discards the buffer and sends nothing (so no
/// server-side transaction can leak).
/// </summary>
public class SurrealTransactionTests
{
    private static SurrealConnectionOptions Opts() => new()
    {
        Endpoint   = "http://localhost:8442",
        MaxRetries = 1,
    };

    // /rpc envelope: a 3-slot result array (BEGIN, the user statement, COMMIT).
    private const string TxnOkBody =
        """{ "id": "1", "result": [ { "status": "OK", "result": null }, { "status": "OK", "result": null }, { "status": "OK", "result": null } ] }""";

    [Fact]
    public async Task CommitPath_FlushesBeginStatementCommit_InOneRequest()
    {
        var handler = new FakeHttpHandler();
        handler.EnqueueOk(TxnOkBody); // single combined flush

        await using var conn = new HttpSurrealConnection(handler, Opts());

        await using (var txn = await conn.BeginTransactionAsync())
        {
            await conn.ExecuteRawAsync("UPDATE wallet:abc SET amount = 100;");
            await txn.CommitAsync();
            txn.IsCommitted.Should().BeTrue();
        }

        // One request carrying the whole transaction — not three round-trips.
        handler.Requests.Should().HaveCount(1);
        var body = handler.Requests[0].Body;
        body.Should().Contain("BEGIN TRANSACTION;");
        body.Should().Contain("UPDATE wallet:abc");
        body.Should().Contain("COMMIT TRANSACTION;");
    }

    [Fact]
    public async Task DisposeWithoutCommit_SendsNothing()
    {
        var handler = new FakeHttpHandler();
        // No statements enqueued — the buffer is discarded on dispose, so the
        // connection never sends a request.

        await using var conn = new HttpSurrealConnection(handler, Opts());

        await using (var txn = await conn.BeginTransactionAsync())
        {
            await conn.ExecuteRawAsync("CREATE wallet:abc CONTENT { amount: 1 };");
            txn.IsCommitted.Should().BeFalse();
            // No CommitAsync — dispose must discard the buffer, sending nothing.
        }

        handler.Requests.Should().BeEmpty(
            "buffered statements are only flushed on commit; an abandoned txn touches the server zero times");
    }

    [Fact]
    public async Task CommitAsync_Idempotent_SecondCallIsNoOp()
    {
        var handler = new FakeHttpHandler();
        handler.EnqueueOk(TxnOkBody); // single flush on first commit

        await using var conn = new HttpSurrealConnection(handler, Opts());
        var txn = await conn.BeginTransactionAsync();
        await conn.ExecuteRawAsync("UPDATE wallet:abc SET amount = 1;");
        await txn.CommitAsync();
        await txn.CommitAsync(); // no-op — must NOT send a second flush

        await txn.DisposeAsync();

        handler.Requests.Should().HaveCount(1,
            "second CommitAsync and dispose-after-commit must not emit further requests");
    }
}
