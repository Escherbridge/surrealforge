using System;
using System.Text.Json;
using FluentAssertions;
using SurrealForge.Client.Json;

namespace SurrealForge.Client.Tests.Json;

/// <summary>
/// MEDIUM #M1 regression coverage: <see cref="SurrealDateTimeJsonConverter"/>
/// must REFUSE to silently relabel <see cref="DateTimeKind.Unspecified"/> as
/// UTC. Silent relabelling was producing offset-sized drift that corrupted
/// the lexicographic ordering SurrealDB <c>&lt;datetime&gt;</c> ASSERTs rely
/// on.
/// </summary>
public sealed class SurrealDateTimeJsonConverterTests
{
    private static readonly JsonSerializerOptions Options = SurrealJsonOptions.Default;

    [Fact]
    public void Unspecified_DateTime_write_throws_JsonException()
    {
        var unspecified = new DateTime(2026, 5, 21, 12, 30, 45, DateTimeKind.Unspecified);

        var act = () => JsonSerializer.Serialize(unspecified, Options);

        act.Should()
            .Throw<JsonException>()
            .WithMessage("*Kind=Unspecified*UTC*");
    }

    [Fact]
    public void Utc_DateTime_writes_with_Z_suffix()
    {
        var utc = new DateTime(2026, 5, 21, 12, 30, 45, DateTimeKind.Utc);

        var json = JsonSerializer.Serialize(utc, Options);

        json.Should().EndWith("Z\"");
        json.Should().Contain("2026-05-21T12:30:45");
    }

    [Fact]
    public void Local_DateTime_writes_after_conversion_to_UTC()
    {
        // Pin a known offset so the test is deterministic regardless of the
        // host TZ: construct a UTC instant, then ask DateTime to re-express
        // it as Local. The wire form must end in Z and round-trip to the
        // same instant.
        var utcInstant = new DateTime(2026, 5, 21, 12, 30, 45, DateTimeKind.Utc);
        var local = utcInstant.ToLocalTime();
        local.Kind.Should().Be(DateTimeKind.Local, "test pre-condition");

        var json = JsonSerializer.Serialize(local, Options);

        json.Should().EndWith("Z\"");
        var roundTrip = JsonSerializer.Deserialize<DateTime>(json, Options);
        roundTrip.Kind.Should().Be(DateTimeKind.Utc);
        roundTrip.Should().BeCloseTo(utcInstant, TimeSpan.FromTicks(1));
    }
}
