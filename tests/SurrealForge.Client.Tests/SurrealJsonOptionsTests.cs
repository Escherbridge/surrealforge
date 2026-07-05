using System;
using System.Text.Json;
using FluentAssertions;
using SurrealForge.Client.Json;

namespace SurrealForge.Client.Tests;

/// <summary>
/// C6 fix surface: enums round-trip as STRINGS, never as ints. The wave-1
/// SCHEMAFULL tables use TYPE string ASSERT $value INSIDE [...] for status
/// columns; emitting integers there silently violates every assertion.
/// </summary>
public class SurrealJsonOptionsTests
{
    public enum BridgeStatus { Pending, Confirmed, Reconciled, Failed }

    private sealed class BridgeRow
    {
        public string Id { get; set; } = string.Empty;
        public BridgeStatus Status { get; set; }
        public DateTime ObservedAt { get; set; }
        public decimal Amount { get; set; }
        public TimeSpan Latency { get; set; }
    }

    [Fact]
    public void Enum_RoundTrip_IsStringOnWire()
    {
        var row = new BridgeRow
        {
            Id     = "bridge_tx:1",
            Status = BridgeStatus.Confirmed,
            // M1: DateTime fields must be UTC-kind explicitly; the converter
            // refuses Kind=Unspecified to prevent silent TZ drift.
            ObservedAt = new DateTime(2026, 5, 21, 0, 0, 0, DateTimeKind.Utc),
        };

        var json = JsonSerializer.Serialize(row, SurrealJsonOptions.Default);

        // String on wire — NOT a number. CamelCase naming policy on the enum.
        json.Should().Contain("\"status\":\"confirmed\"");
        json.Should().NotMatch("*\"status\":1*");

        // And reading the same wire gives us the enum back.
        var parsed = JsonSerializer.Deserialize<BridgeRow>(json, SurrealJsonOptions.Default);
        parsed!.Status.Should().Be(BridgeStatus.Confirmed);
    }

    [Fact]
    public void Decimal_RoundTrip_IsStringOnWire_PreservesPrecision()
    {
        // 28-digit precision — would lose bits if it ever went through a double.
        const decimal exact = 12345678901234567890.123456789m;
        var json = JsonSerializer.Serialize(exact, SurrealJsonOptions.Default);
        json.Should().Be("\"12345678901234567890.123456789\"");

        var roundtrip = JsonSerializer.Deserialize<decimal>(json, SurrealJsonOptions.Default);
        roundtrip.Should().Be(exact);
    }

    [Fact]
    public void DateTime_RoundTrip_NormalisesToUtcZ()
    {
        var local = new DateTime(2026, 5, 21, 12, 30, 45, DateTimeKind.Utc);
        var json = JsonSerializer.Serialize(local, SurrealJsonOptions.Default);

        json.Should().EndWith("Z\"");
        var roundtrip = JsonSerializer.Deserialize<DateTime>(json, SurrealJsonOptions.Default);
        roundtrip.Kind.Should().Be(DateTimeKind.Utc);
        roundtrip.Should().BeCloseTo(local, TimeSpan.FromTicks(1));
    }

    [Fact]
    public void TimeSpan_RoundTrip_AsSurrealDurationLiteral()
    {
        var span = TimeSpan.FromMinutes(90) + TimeSpan.FromMilliseconds(500);
        var json = JsonSerializer.Serialize(span, SurrealJsonOptions.Default);

        json.Should().Be("\"1h30m500ms\"");
        var rt = JsonSerializer.Deserialize<TimeSpan>(json, SurrealJsonOptions.Default);
        rt.Should().Be(span);
    }

    [Fact]
    public void TimeSpan_CompositeLiteral_ParsesCorrectly()
    {
        var rt = JsonSerializer.Deserialize<TimeSpan>("\"1h30m\"", SurrealJsonOptions.Default);
        rt.Should().Be(TimeSpan.FromHours(1) + TimeSpan.FromMinutes(30));
    }

    [Fact]
    public void RecordId_RoundTrip_AsTableColonId()
    {
        var rid = new RecordId("wallet", "abc-123");
        var json = JsonSerializer.Serialize(rid, SurrealJsonOptions.Default);

        json.Should().Be("\"wallet:abc-123\"");
        var rt = JsonSerializer.Deserialize<RecordId>(json, SurrealJsonOptions.Default);
        rt.Should().Be(rid);
    }

    [Fact]
    public void RecordId_AcceptsObjectForm_FromWebSocketCborFallback()
    {
        // Some response shapes emit {tb,id}; the converter tolerates both.
        var rt = JsonSerializer.Deserialize<RecordId>("""{"tb":"wallet","id":"abc"}""", SurrealJsonOptions.Default);
        rt.Should().Be(new RecordId("wallet", "abc"));
    }
}
