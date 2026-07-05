using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SurrealForge.Client.Json;

/// <summary>
/// Always-string-on-wire converter for <see cref="decimal"/>. SurrealDB's
/// <c>decimal</c> type preserves arbitrary precision; emitting C# decimals as
/// JSON numbers risks loss-of-precision when the value flows through a
/// double-mediated parser anywhere in the pipeline (the client itself uses
/// <see cref="JsonElement"/> which is precise, but downstream tooling like
/// dashboards / log indices is often loose). String-on-wire is the safe
/// default for any value path that carries money or balances.
/// </summary>
public sealed class SurrealDecimalJsonConverter : JsonConverter<decimal>
{
    public override decimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var s = reader.GetString();
            if (string.IsNullOrEmpty(s))
            {
                throw new JsonException("decimal string must not be empty.");
            }
            return decimal.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
        }
        if (reader.TokenType == JsonTokenType.Number)
        {
            // Tolerate number-form for round-trip with naive emitters, but the
            // canonical wire shape is the string form (written by Write below).
            return reader.GetDecimal();
        }
        throw new JsonException($"Expected string or number for decimal; got {reader.TokenType}.");
    }

    public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
    }
}
