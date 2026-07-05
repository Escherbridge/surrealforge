using System;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SurrealForge.Client.Json;

/// <summary>
/// Round-trip <see cref="TimeSpan"/> against SurrealDB's <c>duration</c>
/// type. Writes compact <c>1h30m</c> / <c>500ms</c> form (matching the
/// SurrealQL literal grammar); reads tolerate both that form and the
/// nanosecond-int form some response shapes emit.
/// </summary>
public sealed class SurrealTimeSpanJsonConverter : JsonConverter<TimeSpan>
{
    public override TimeSpan Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            // Ints are nanoseconds per the SurrealDB serde convention.
            var ns = reader.GetInt64();
            return TimeSpan.FromTicks(ns / 100L);
        }
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException(
                $"Expected string or numeric nanoseconds for duration; got {reader.TokenType}.");
        }
        var s = reader.GetString();
        if (string.IsNullOrEmpty(s))
        {
            throw new JsonException("duration string must not be empty.");
        }
        return ParseSurrealDuration(s!);
    }

    public override void Write(Utf8JsonWriter writer, TimeSpan value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(FormatSurrealDuration(value));
    }

    internal static TimeSpan ParseSurrealDuration(string s)
    {
        // Grammar: <number><unit>(<number><unit>)* where unit ∈ {ns, us|µs, ms, s, m, h, d, w, y}.
        // Composite forms (1h30m, 500ms) are valid; we sum their parts.
        var total = TimeSpan.Zero;
        int i = 0;
        while (i < s.Length)
        {
            // Read number.
            int numStart = i;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;
            if (numStart == i)
            {
                throw new JsonException($"Invalid duration literal '{s}': expected number at position {numStart}.");
            }
            double number = double.Parse(
                s.Substring(numStart, i - numStart),
                NumberStyles.Float, CultureInfo.InvariantCulture);

            // Read unit (1-3 chars).
            int unitStart = i;
            while (i < s.Length && !char.IsDigit(s[i]) && s[i] != '.') i++;
            if (unitStart == i)
            {
                throw new JsonException($"Invalid duration literal '{s}': missing unit after number.");
            }
            var unit = s.Substring(unitStart, i - unitStart);

            total += unit switch
            {
                "ns"        => TimeSpan.FromTicks((long)(number / 100.0)),
                "us" or "µs" => TimeSpan.FromTicks((long)(number * 10.0)),
                "ms"        => TimeSpan.FromMilliseconds(number),
                "s"         => TimeSpan.FromSeconds(number),
                "m"         => TimeSpan.FromMinutes(number),
                "h"         => TimeSpan.FromHours(number),
                "d"         => TimeSpan.FromDays(number),
                "w"         => TimeSpan.FromDays(number * 7),
                "y"         => TimeSpan.FromDays(number * 365),
                _ => throw new JsonException($"Unknown duration unit '{unit}' in '{s}'."),
            };
        }
        return total;
    }

    internal static string FormatSurrealDuration(TimeSpan ts)
    {
        // MEDIUM #M2: the SurrealQL duration grammar rejects negative
        // literals; previously this converter both (a) emitted "-..." which
        // SurrealDB would refuse, and (b) overflowed on TimeSpan.MinValue
        // because -TimeSpan.MinValue has no positive representation. Reject
        // negative inputs explicitly with a descriptive message; do not
        // attempt to silently negate.
        if (ts.Ticks < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ts),
                $"SurrealDB duration literals do not accept negative values; the input TimeSpan {ts} cannot be serialized.");
        }

        // Compact representation: emit only non-zero components, largest unit first.
        var sb = new StringBuilder();

        int days  = ts.Days;
        int hours = ts.Hours;
        int mins  = ts.Minutes;
        int secs  = ts.Seconds;
        int ms    = ts.Milliseconds;
        long subMsTicks = ts.Ticks - new TimeSpan(days, hours, mins, secs, ms).Ticks;

        if (days > 0)  sb.Append(days).Append('d');
        if (hours > 0) sb.Append(hours).Append('h');
        if (mins > 0)  sb.Append(mins).Append('m');
        if (secs > 0)  sb.Append(secs).Append('s');
        if (ms > 0)    sb.Append(ms).Append("ms");
        if (subMsTicks > 0)
        {
            long us = subMsTicks / 10;
            long nsRem = (subMsTicks - us * 10) * 100;
            if (us > 0)    sb.Append(us).Append("us");
            if (nsRem > 0) sb.Append(nsRem).Append("ns");
        }

        // Always emit at least one component, even for zero spans, so the
        // SurrealQL parser sees a syntactically valid literal.
        if (sb.Length == 0) sb.Append("0ns");

        return sb.ToString();
    }
}
