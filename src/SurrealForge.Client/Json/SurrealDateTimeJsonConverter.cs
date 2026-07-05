using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SurrealForge.Client.Json;

/// <summary>
/// Round-trip <see cref="DateTime"/> against SurrealDB's <c>datetime</c>
/// type. Writes ISO-8601 with explicit <c>Z</c> suffix in UTC; reads tolerate
/// any RFC3339 / ISO-8601 form SurrealDB might emit. UTC normalization is
/// load-bearing — SurrealDB ASSERT clauses use lexicographic comparison
/// against ISO-8601, which is only stable when every timestamp is UTC-Z.
/// </summary>
public sealed class SurrealDateTimeJsonConverter : JsonConverter<DateTime>
{
    // Sub-microsecond precision matches SurrealDB's documented <datetime>
    // resolution. "K" emits "Z" for UTC kinds, which is exactly what we want.
    private const string WireFormat = "yyyy-MM-ddTHH:mm:ss.fffffffK";

    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException(
                $"Expected ISO-8601 string for datetime; got {reader.TokenType}.");
        }
        var s = reader.GetString();
        if (string.IsNullOrEmpty(s))
        {
            throw new JsonException("datetime string must not be empty.");
        }
        // RoundtripKind preserves the Z / offset if present; if the wire form
        // is offset-less, AssumeUniversal would be ideal but the two flags
        // are mutually exclusive — RoundtripKind already biases Unspecified
        // toward Utc when there's no offset, so we use RoundtripKind alone
        // and force UTC below.
        if (!DateTime.TryParse(
                s, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var dt))
        {
            throw new JsonException($"Could not parse '{s}' as ISO-8601 datetime.");
        }
        // Normalise Unspecified → UTC (we wrote with Z so the round-trip is consistent).
        if (dt.Kind == DateTimeKind.Unspecified)
        {
            dt = DateTime.SpecifyKind(dt, DateTimeKind.Utc);
        }
        return dt.ToUniversalTime();
    }

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
    {
        // MEDIUM #M1: silently relabelling Kind=Unspecified as UTC produced
        // offset-sized drift (server-local time was being stamped with Z),
        // which corrupted the lexicographic ordering SurrealDB <datetime>
        // ASSERT clauses rely on. Reject the ambiguous case up-front; callers
        // MUST normalise to UTC explicitly before serialisation.
        if (value.Kind == DateTimeKind.Unspecified)
        {
            throw new JsonException(
                "Refusing to serialize DateTime with Kind=Unspecified — convert to UTC explicitly before passing to SurrealDB to avoid silent timezone drift.");
        }

        // Force UTC so the K format specifier emits 'Z' (not a numeric offset).
        var utc = value.Kind == DateTimeKind.Local
            ? value.ToUniversalTime()
            : value;
        writer.WriteStringValue(utc.ToString(WireFormat, CultureInfo.InvariantCulture));
    }
}
