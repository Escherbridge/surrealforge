using FluentAssertions;
using SurrealForge.Client;

namespace SurrealForge.Client.Tests;

/// <summary>
/// Closes the code-review C5 root: every statement keeps its own status /
/// detail / result. Single-statement OK, multi-statement OK+ERR mixed, and
/// ERR-with-detail all round-trip with the shape the HTTP /sql contract
/// promises.
/// </summary>
public class SurrealResponseTests
{
    [Fact]
    public void FromJson_SingleStatementOk_ParsesValuesAndStatus()
    {
        const string body = """
        [
          { "status": "OK", "time": "12.3µs", "result": [ {"id":"wallet:abc", "amount": "100"} ] }
        ]
        """;
        var resp = SurrealResponse.FromJson(body);

        resp.Count.Should().Be(1);
        resp[0].IsOk.Should().BeTrue();
        resp[0].Status.Should().Be("OK");
        resp[0].Detail.Should().BeNull();
        resp[0].Time.Should().Be("12.3µs");

        // Per-statement projection still works.
        var values = resp.GetValues<TestRow>(0);
        values.Should().HaveCount(1);
        values[0].Id.Should().Be("wallet:abc");
        values[0].Amount.Should().Be("100");
    }

    [Fact]
    public void FromJson_MultiStatement_PreservesPerStatementResults()
    {
        // The bug C5 fixed: GetValues<T>(0) used to collapse every statement
        // into one shape. Here we assert each slot retains its own result.
        const string body = """
        [
          { "status": "OK",  "time": "1µs",  "result": [ {"id":"a"} ] },
          { "status": "ERR", "time": "2µs",  "detail": "constraint failed", "result": null },
          { "status": "OK",  "time": "3µs",  "result": [ {"id":"b"}, {"id":"c"} ] }
        ]
        """;
        var resp = SurrealResponse.FromJson(body);

        resp.Count.Should().Be(3);
        resp[0].IsOk.Should().BeTrue();
        resp[1].IsOk.Should().BeFalse();
        resp[1].Detail.Should().Be("constraint failed");
        resp[2].IsOk.Should().BeTrue();

        resp[0].GetValues<TestRow>().Should().HaveCount(1);
        resp[2].GetValues<TestRow>().Should().HaveCount(2);
    }

    [Fact]
    public void EnsureAllOk_MixedResults_ThrowsWithFailingIndex()
    {
        const string body = """
        [
          { "status": "OK",  "time": "1µs", "result": [] },
          { "status": "ERR", "time": "2µs", "detail": "table 'foo' does not exist", "result": null }
        ]
        """;
        var resp = SurrealResponse.FromJson(body);

        Action act = () => resp.EnsureAllOk();

        // 1-based index in the message for human readability.
        act.Should().Throw<SurrealStatementException>()
            .Where(e => e.StatementIndex == 1
                     && e.StatementCount == 2
                     && e.Detail == "table 'foo' does not exist");
    }

    [Fact]
    public void FromJson_NonArrayBody_ThrowsProtocolException()
    {
        // SurrealDB always returns a top-level array, even for one statement.
        Action act = () => SurrealResponse.FromJson("""{"status":"OK"}""");
        act.Should().Throw<SurrealProtocolException>();
    }

    [Fact]
    public void GetValues_NullResult_ReturnsEmpty()
    {
        // DEFINE FIELD-style statements return result:null, NOT an empty array.
        const string body = """[ { "status": "OK", "time": "1µs", "result": null } ]""";
        var resp = SurrealResponse.FromJson(body);

        resp[0].GetValues<TestRow>().Should().BeEmpty();
    }

    private sealed class TestRow
    {
        public string Id     { get; set; } = string.Empty;
        public string Amount { get; set; } = string.Empty;
    }
}
