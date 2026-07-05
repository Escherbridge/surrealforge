using System.Threading.Tasks;
using FluentAssertions;
using SurrealForge.Client;
using SurrealForge.Client.Connection;

namespace SurrealForge.Client.Tests;

/// <summary>
/// Transport-shape tests for HTTP /rpc. The handler is faked — we assert the
/// outgoing request shape (NS/DB headers, basic-auth, RPC envelope body) and
/// parse a scripted /rpc response. No live SurrealDB instance required.
/// </summary>
public class HttpSurrealConnectionTests
{
    private const string RpcOkEmpty =
        """{"id":"x","result":[ { "status": "OK", "time": "1µs", "result": [] } ]}""";

    private static SurrealConnectionOptions DefaultOptions() => new()
    {
        Endpoint       = "http://localhost:8442",
        Namespace      = "test",
        Database       = "test",
        User           = "root",
        Password       = "root",
        MaxConnections = 4,
        MaxRetries     = 1, // Tests don't exercise the retry path explicitly.
    };

    [Fact]
    public async Task ExecuteRawAsync_SendsNsDbHeadersAndBasicAuth()
    {
        var handler = new FakeHttpHandler();
        handler.EnqueueOk(RpcOkEmpty);

        await using var conn = new HttpSurrealConnection(handler, DefaultOptions());
        var resp = await conn.ExecuteRawAsync("INFO FOR DB;");

        resp.Count.Should().Be(1);
        resp[0].IsOk.Should().BeTrue();

        handler.Requests.Should().HaveCount(1);
        var req = handler.Requests[0];
        req.Method.Should().Be("POST");
        req.Uri.Should().EndWith("/rpc");
        req.NsHeader.Should().Be("test");
        req.DbHeader.Should().Be("test");
        req.HasAuth.Should().BeTrue("basic auth header must be present when User is set");
        req.Body.Should().Contain("\"method\":\"query\"");
        req.Body.Should().Contain("INFO FOR DB;");
    }

    [Fact]
    public async Task UseAsync_SwitchesNsDbForSubsequentRequests()
    {
        var handler = new FakeHttpHandler();
        handler.EnqueueOk(RpcOkEmpty);

        await using var conn = new HttpSurrealConnection(handler, DefaultOptions());
        await conn.UseAsync("test", "prod");
        await conn.ExecuteRawAsync("INFO FOR DB;");

        handler.Requests[0].NsHeader.Should().Be("test");
        handler.Requests[0].DbHeader.Should().Be("prod");
    }

    [Fact]
    public async Task ExecuteRawAsync_NonSuccessHttp_ThrowsProtocolException()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue(_ => Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError)
        {
            Content = new System.Net.Http.StringContent("oops"),
        }));

        await using var conn = new HttpSurrealConnection(handler, DefaultOptions());
        var act = async () => await conn.ExecuteRawAsync("SELECT 1;");

        await act.Should().ThrowAsync<SurrealProtocolException>();
    }
}
