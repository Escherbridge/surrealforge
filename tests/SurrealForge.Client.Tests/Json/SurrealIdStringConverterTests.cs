using System.Text.Json;
using FluentAssertions;
using SurrealForge.Client;
using SurrealForge.Client.Json;

namespace SurrealForge.Client.Tests.Json;

/// <summary>
/// Coverage for the package-level table-prefix strip on the <c>id</c>
/// property of any <see cref="ISurrealRecord"/> POCO. SurrealDB's
/// <c>/rpc</c> + <c>/sql</c> responses always serialize the system record id
/// as <c>table:&lt;id&gt;</c>; the modifier in SurrealJsonOptions wraps
/// these properties with a converter that strips the table prefix on read
/// so the application sees the bare-hex form.
/// </summary>
public sealed class SurrealIdStringConverterTests
{
    private static readonly JsonSerializerOptions Options = SurrealJsonOptions.Default;

    private sealed class FakeRecord : ISurrealRecord
    {
        public string SchemaName => "fake";
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
    }

    [Fact]
    public void Read_strips_table_prefix_from_id_property()
    {
        const string json = """{"id":"fake:aaaabbbbccccddddeeeeffff00001111","name":"x"}""";

        var rec = JsonSerializer.Deserialize<FakeRecord>(json, Options);

        rec.Should().NotBeNull();
        rec!.Id.Should().Be("aaaabbbbccccddddeeeeffff00001111",
            "the table prefix must be peeled off so Guid.ParseExact(\"N\") can round-trip");
        rec.Name.Should().Be("x");
    }

    [Fact]
    public void Read_passes_unprefixed_id_through_unchanged()
    {
        const string json = """{"id":"aaaabbbbccccddddeeeeffff00001111","name":"x"}""";

        var rec = JsonSerializer.Deserialize<FakeRecord>(json, Options);

        rec!.Id.Should().Be("aaaabbbbccccddddeeeeffff00001111");
    }

    [Fact]
    public void Read_strips_angle_bracket_wrappers_from_complex_ids()
    {
        // SurrealDB wraps non-simple ids in ⟨ ⟩ (U+27E8 / U+27E9).
        const string json = "{\"id\":\"fake:⟨some-complex-id⟩\",\"name\":\"x\"}";

        var rec = JsonSerializer.Deserialize<FakeRecord>(json, Options);

        rec!.Id.Should().Be("some-complex-id");
    }

    [Fact]
    public void Write_emits_bare_id_value_unchanged()
    {
        var rec = new FakeRecord { Id = "aaaabbbbccccddddeeeeffff00001111", Name = "x" };

        var json = JsonSerializer.Serialize(rec, Options);

        json.Should().Contain("\"id\":\"aaaabbbbccccddddeeeeffff00001111\"",
            "the wave-1 stores write the bare-hex form on insert; the converter must pass it through");
        json.Should().NotContain("\"id\":\"fake:");
    }

    [Fact]
    public void Modifier_does_not_touch_non_id_string_properties()
    {
        // 'Name' must NOT be touched by the id-strip modifier even though
        // it could hypothetically contain a colon.
        const string json = """{"id":"fake:abc","name":"category:nested"}""";

        var rec = JsonSerializer.Deserialize<FakeRecord>(json, Options);

        rec!.Id.Should().Be("abc");
        rec.Name.Should().Be("category:nested",
            "the modifier scope is `id` only; other string fields pass through");
    }

    [Fact]
    public void Modifier_does_not_apply_to_non_ISurrealRecord_types()
    {
        // A plain POCO that doesn't implement ISurrealRecord must round-trip
        // its `id` property verbatim — the modifier is scoped to records.
        var json = """{"id":"foo:bar"}""";

        var plain = JsonSerializer.Deserialize<NonRecord>(json, Options);

        plain!.Id.Should().Be("foo:bar");
    }

    private sealed class NonRecord
    {
        public string Id { get; set; } = string.Empty;
    }
}
