using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using SurrealForge.Client;
using SurrealForge.Client.Idempotency;
using SurrealForge.Client.Query;
using Xunit;

namespace SurrealForge.Client.Tests.Idempotency;

public class SurrealIdempotencyLedgerOptionsTests
{
    private const string EncodedPrefix = "__sf_idem_b64__";

    private static SurrealResponse Empty()
        => SurrealResponse.FromJson("[ { \"status\": \"OK\", \"time\": \"1µs\", \"result\": [] } ]");

    private static SurrealResponse Row(string storedKey)
    {
        var row =
            "{ \"id\": \"abc\", \"key\": \"" + storedKey + "\", \"operation_type\": \"op\", " +
            "\"state\": \"InProgress\", \"created_at\": \"2026-01-01T00:00:00Z\", " +
            "\"updated_at\": \"2026-01-01T00:00:00Z\" }";
        return SurrealResponse.FromJson(
            "[ { \"status\": \"OK\", \"time\": \"1µs\", \"result\": [ " + row + " ] } ]");
    }

    private static string ExpectedEncoded(string key)
        => EncodedPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(key))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [Fact]
    public void Options_validate_rejects_empty_prefix_when_encoding_enabled()
    {
        var opts = new IdempotencyLedgerOptions { EncodeColonKeys = true, EncodedKeyPrefix = "" };
        Action act = () => opts.Validate();
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task Claim_stores_colon_key_base64url_encoded_when_enabled()
    {
        var captured = new List<SurrealQuery>();
        var exec = new Mock<ISurrealExecutor>(MockBehavior.Strict);
        exec.SetupSequence(e => e.ExecuteAsync(Capture.In(captured), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Empty())                                    // fast-path fetch: not found
            .ReturnsAsync(Row(ExpectedEncoded("op_bridge:redeem:1"))); // INSERT-wins

        var ledger = new SurrealIdempotencyLedger(exec.Object,
            new IdempotencyLedgerOptions { EncodeColonKeys = true });

        var claim = await ledger.TryClaimAsync("op_bridge:redeem:1", "op", CancellationToken.None);

        // Won, and the domain record carries the ORIGINAL (decoded) key.
        claim.Won.Should().BeTrue();
        claim.Record.Key.Should().Be("op_bridge:redeem:1");

        // The INSERT content stored the ENCODED key.
        var insert = captured[1];
        var content = insert.Params["_content"].Should().BeAssignableTo<IDictionary<string, object?>>().Subject;
        content["key"].Should().Be(ExpectedEncoded("op_bridge:redeem:1"));
    }

    [Fact]
    public async Task Claim_stores_colonless_key_verbatim_even_when_encoding_enabled()
    {
        var captured = new List<SurrealQuery>();
        var exec = new Mock<ISurrealExecutor>(MockBehavior.Strict);
        exec.SetupSequence(e => e.ExecuteAsync(Capture.In(captured), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Empty())
            .ReturnsAsync(Row("plainkey"));

        var ledger = new SurrealIdempotencyLedger(exec.Object,
            new IdempotencyLedgerOptions { EncodeColonKeys = true });

        await ledger.TryClaimAsync("plainkey", "op", CancellationToken.None);

        var content = (IDictionary<string, object?>)captured[1].Params["_content"]!;
        content["key"].Should().Be("plainkey");
    }

    [Fact]
    public async Task Claim_stores_colon_key_verbatim_when_encoding_disabled()
    {
        var captured = new List<SurrealQuery>();
        var exec = new Mock<ISurrealExecutor>(MockBehavior.Strict);
        exec.SetupSequence(e => e.ExecuteAsync(Capture.In(captured), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Empty())
            .ReturnsAsync(Row("op:1"));

        var ledger = new SurrealIdempotencyLedger(exec.Object); // defaults: no encoding

        await ledger.TryClaimAsync("op:1", "op", CancellationToken.None);

        var content = (IDictionary<string, object?>)captured[1].Params["_content"]!;
        content["key"].Should().Be("op:1");
    }

    [Fact]
    public async Task Get_restores_original_key_from_encoded_storage()
    {
        var exec = new Mock<ISurrealExecutor>(MockBehavior.Strict);
        exec.Setup(e => e.ExecuteAsync(It.IsAny<SurrealQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Row(ExpectedEncoded("op_bridge:redeem:1")));

        var ledger = new SurrealIdempotencyLedger(exec.Object,
            new IdempotencyLedgerOptions { EncodeColonKeys = true });

        var record = await ledger.GetAsync("op_bridge:redeem:1", CancellationToken.None);

        record.Should().NotBeNull();
        record!.Key.Should().Be("op_bridge:redeem:1");
    }
}
