using System;
using System.Text.Json;
using FluentAssertions;
using SurrealForge.Client.Json;

namespace SurrealForge.Client.Tests.Json;

/// <summary>
/// MEDIUM #M2 regression coverage: <see cref="SurrealTimeSpanJsonConverter"/>
/// must reject negative <see cref="TimeSpan"/> inputs rather than emit a
/// "-..." literal that SurrealQL's duration grammar refuses (and which would
/// overflow on <see cref="TimeSpan.MinValue"/> via the previous unary-minus
/// path).
/// </summary>
public sealed class SurrealTimeSpanJsonConverterTests
{
    private static readonly JsonSerializerOptions Options = SurrealJsonOptions.Default;

    [Fact]
    public void Negative_TimeSpan_write_throws()
    {
        var negative = TimeSpan.FromMinutes(-5);

        var act = () => JsonSerializer.Serialize(negative, Options);

        // The converter throws ArgumentOutOfRangeException; STJ surfaces that
        // exception directly when it bubbles out of a JsonConverter.Write.
        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithMessage("*SurrealDB duration literals do not accept negative values*");
    }

    [Fact]
    public void MinValue_TimeSpan_write_throws_with_descriptive_message()
    {
        // Regression for the overflow defect: previously the converter did
        // `if (negative) ts = -ts;` which throws OverflowException for
        // TimeSpan.MinValue because -TimeSpan.MinValue is unrepresentable.
        var act = () => JsonSerializer.Serialize(TimeSpan.MinValue, Options);

        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithMessage("*do not accept negative*");
    }

    [Fact]
    public void Zero_TimeSpan_writes_as_0ns()
    {
        var json = JsonSerializer.Serialize(TimeSpan.Zero, Options);

        json.Should().Be("\"0ns\"");
        var roundTrip = JsonSerializer.Deserialize<TimeSpan>(json, Options);
        roundTrip.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void Positive_complex_TimeSpan_round_trips()
    {
        var span = TimeSpan.FromHours(1)
                 + TimeSpan.FromMinutes(30)
                 + TimeSpan.FromMilliseconds(500);

        var json = JsonSerializer.Serialize(span, Options);
        var roundTrip = JsonSerializer.Deserialize<TimeSpan>(json, Options);

        roundTrip.Should().Be(span);
    }
}
