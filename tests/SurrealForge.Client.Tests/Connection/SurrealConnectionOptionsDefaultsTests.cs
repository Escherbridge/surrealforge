using System.Threading.Tasks;
using FluentAssertions;
using SurrealForge.Client;
using SurrealForge.Client.Connection;
using SurrealForge.Client.Tests; // FakeHttpHandler

namespace SurrealForge.Client.Tests.Connection;

/// <summary>
/// MEDIUM #M6 regression coverage: <see cref="SurrealConnectionOptions"/>
/// defaults for <see cref="SurrealConnectionOptions.User"/> /
/// <see cref="SurrealConnectionOptions.Password"/> must be empty so the
/// transport sends no Authorization header by default. Previously the
/// defaults were hard-coded <c>"root"/"root"</c>, which silently put dev
/// credentials on the wire even though the doc-comment promised the opposite.
/// </summary>
public sealed class SurrealConnectionOptionsDefaultsTests
{
    private const string OkBody = """[ { "status": "OK", "time": "1µs", "result": [] } ]""";

    [Fact]
    public void Default_options_have_empty_User_and_Password()
    {
        var opts = new SurrealConnectionOptions();

        opts.User.Should().BeEmpty();
        opts.Password.Should().BeEmpty();
    }

    [Fact]
    public async Task HttpSurrealConnection_with_empty_credentials_sends_no_Authorization_header()
    {
        var handler = new FakeHttpHandler();
        handler.EnqueueOk(OkBody);

        var opts = new SurrealConnectionOptions
        {
            Endpoint  = "http://localhost:8442",
            Namespace = "test",
            Database  = "test",
            // User/Password left at their (empty) defaults.
        };

        await using var conn = new HttpSurrealConnection(handler, opts);
        await conn.ExecuteRawAsync("SELECT 1;");

        handler.Requests.Should().HaveCount(1);
        handler.Requests[0].HasAuth.Should().BeFalse(
            "empty User+Password defaults must produce no Authorization header (M6 fix).");
    }

    [Fact]
    public async Task HttpSurrealConnection_with_explicit_credentials_sends_Basic_Authorization_header()
    {
        var handler = new FakeHttpHandler();
        handler.EnqueueOk(OkBody);

        var opts = new SurrealConnectionOptions
        {
            Endpoint  = "http://localhost:8442",
            Namespace = "test",
            Database  = "test",
            User      = "alice",
            Password  = "secret",
        };

        await using var conn = new HttpSurrealConnection(handler, opts);
        await conn.ExecuteRawAsync("SELECT 1;");

        handler.Requests.Should().HaveCount(1);
        handler.Requests[0].HasAuth.Should().BeTrue(
            "explicit credentials must still produce a Basic Authorization header (regression positive).");
    }
}
