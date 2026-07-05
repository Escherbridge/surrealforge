using System.Threading.Tasks;
using FluentAssertions;
using SurrealForge.Client;
using SurrealForge.Client.Connection;
using SurrealForge.Client.Tests; // FakeHttpHandler

namespace SurrealForge.Client.Tests.Transaction;

/// <summary>
/// Regression for HIGH#1 under the 3.x buffered-flush model: when the single
/// <c>BEGIN; ...; COMMIT;</c> flush returns an error, the failure must bubble
/// to the caller and <c>IsCommitted</c> must stay <c>false</c>. There is no
/// separate CANCEL round-trip — the buffered statements were flushed in one
/// request that the server auto-aborts on error, so dispose sends nothing.
/// </summary>
public class SurrealTransactionFailedCommitTests
{
    private static SurrealConnectionOptions Opts() => new()
    {
        Endpoint   = "http://localhost:8442",
        MaxRetries = 1,
    };

    // /rpc envelope whose COMMIT slot is ERR (BEGIN ok, statement ok, COMMIT err).
    private const string FlushErrBody =
        """{ "id": "1", "result": [ { "status": "OK", "result": null }, { "status": "OK", "result": null }, { "status": "ERR", "result": null, "detail": "synthetic commit failure" } ] }""";

    [Fact]
    public async Task CommitAsync_FlushReturnsErr_PreservesIsCommittedFalse_AndDisposeSendsNothingMore()
    {
        var handler = new FakeHttpHandler();
        handler.EnqueueOk(FlushErrBody); // the single combined flush, COMMIT slot ERR

        await using var conn = new HttpSurrealConnection(handler, Opts());

        var txn = await conn.BeginTransactionAsync();
        await conn.ExecuteRawAsync("UPDATE wallet:abc SET amount = 100;");

        var commit = async () => await txn.CommitAsync();
        await commit.Should().ThrowAsync<SurrealStatementException>(
            "a flush whose COMMIT slot is ERR must bubble to the caller");

        txn.IsCommitted.Should().BeFalse(
            "the flush failed server-side so IsCommitted must NOT flip to true");

        await txn.DisposeAsync();

        // Exactly one request — the combined flush. No separate CANCEL: the
        // failed BEGIN..COMMIT is auto-aborted by the server in that same call.
        handler.Requests.Should().HaveCount(1);
        var body = handler.Requests[0].Body;
        body.Should().Contain("BEGIN TRANSACTION;");
        body.Should().Contain("UPDATE wallet:abc");
        body.Should().Contain("COMMIT TRANSACTION;");
    }
}
