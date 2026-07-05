using System.Text.Json;
using FluentAssertions;
using SurrealForge.Client.Json;

namespace SurrealForge.Client.Tests.Json;

/// <summary>
/// MEDIUM #M5 regression coverage: <see cref="RecordIdJsonConverter"/> must
/// fail loudly when the object-form <c>id</c> property holds a non-string
/// JSON token (object / number / etc.). Previously the reader called
/// <c>GetString()</c> unconditionally, which left the reader position out of
/// sync with the consumed tokens.
/// </summary>
public sealed class RecordIdJsonConverterTests
{
    private static readonly JsonSerializerOptions Options = SurrealJsonOptions.Default;

    [Fact]
    public void Object_form_with_nested_object_id_value_throws_JsonException()
    {
        const string payload = """{ "tb": "wallet", "id": { "x": 1 } }""";

        var act = () => JsonSerializer.Deserialize<RecordId>(payload, Options);

        act.Should().Throw<JsonException>()
            .WithMessage("*'id'*string*StartObject*");
    }

    [Fact]
    public void Object_form_with_int_id_value_throws_JsonException()
    {
        const string payload = """{ "tb": "wallet", "id": 42 }""";

        var act = () => JsonSerializer.Deserialize<RecordId>(payload, Options);

        act.Should().Throw<JsonException>()
            .WithMessage("*'id'*string*Number*");
    }

    [Fact]
    public void Object_form_well_formed_round_trips()
    {
        const string payload = """{ "tb": "wallet", "id": "abc-123" }""";

        var rid = JsonSerializer.Deserialize<RecordId>(payload, Options);

        rid.Should().Be(new RecordId("wallet", "abc-123"));
    }
}
