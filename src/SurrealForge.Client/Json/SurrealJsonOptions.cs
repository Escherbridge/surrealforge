using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using SurrealForge.Client.Schema;

namespace SurrealForge.Client.Json;

/// <summary>
/// Centralised <see cref="JsonSerializerOptions"/> for every SurrealDB
/// payload the client emits or consumes.
/// <para>
/// Closes the code-review C6 design root: <see cref="JsonStringEnumConverter"/>
/// is registered by default so enums round-trip as strings against
/// SCHEMAFULL <c>TYPE string ASSERT $value INSIDE [...]</c>. The previous
/// SDK-mediated default emitted enums as ints, which silently violated
/// every assertion and caused inserts to fail at runtime.
/// </para>
/// <para>
/// Custom converters:
/// <list type="bullet">
///   <item><see cref="RecordIdJsonConverter"/> — <c>table:id</c> string form
///         per <see href="https://surrealdb.com/docs/surrealql/datamodel/ids">SurrealDB IDs</see>.</item>
///   <item><see cref="SurrealDateTimeJsonConverter"/> — ISO-8601 with explicit
///         <c>Z</c>, matching SurrealDB's <c>datetime</c> shape.</item>
///   <item><see cref="SurrealTimeSpanJsonConverter"/> — SurrealDB-style
///         duration literals (<c>1h30m</c>, <c>500ms</c>).</item>
///   <item><see cref="SurrealDecimalJsonConverter"/> — always string on the
///         wire to avoid double-precision drift.</item>
/// </list>
/// </para>
/// </summary>
public static class SurrealJsonOptions
{
    /// <summary>
    /// The pre-configured options the client uses for every request / response.
    /// Lazily built on first access so a startup
    /// <see cref="SurrealNaming.Convention"/> override is honored. Safe to
    /// share across threads; <see cref="JsonSerializerOptions"/> is immutable
    /// once first used.
    /// </summary>
    public static JsonSerializerOptions Default => _default.Value;

    private static readonly System.Lazy<JsonSerializerOptions> _default =
        new(BuildDefault, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);

    private static JsonSerializerOptions BuildDefault()
    {
        // Property/field wire names follow the process-wide naming convention
        // (SurrealNaming.Convention, default snake_case) so the JSON key matches
        // the SurrealDB column name the schema scanner derives from the same
        // convention — no per-property [JsonPropertyName] needed. Enum VALUES
        // stay camelCase (the JsonStringEnumConverter below) to match the
        // SCHEMAFULL `ASSERT $value INSIDE [...]` token casing, independent of
        // the field-name policy.
        var policy = SurrealNaming.JsonPolicy;
        var opts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = policy,
            DictionaryKeyPolicy  = policy,
            // Read tolerates either casing so legacy payloads still deserialize.
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented               = false,
        };

        // C6 fix — strings, not ints, for enums. Enum-value casing is fixed to
        // camelCase regardless of the field-name policy (matches the schema's
        // INSIDE token set).
        opts.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        opts.Converters.Add(new RecordIdJsonConverter());
        opts.Converters.Add(new SurrealDateTimeJsonConverter());
        opts.Converters.Add(new SurrealTimeSpanJsonConverter());
        opts.Converters.Add(new SurrealDecimalJsonConverter());

        // 2026-06-07: SCHEMAFULL tables in SurrealDB v2.x reject any
        // CONTENT property that is not declared on the schema. The
        // ISurrealRecord interface's SchemaName instance property would
        // otherwise serialize as "schemaName": "avatar" and trip the
        // server-side "Found field 'schemaName', but no such field exists
        // for table 'X'" parse-time error.
        //
        // Strip the property at the serializer level so every ISurrealRecord
        // implementor (hand-authored POCO, inline adapter POCO, future
        // source-gen output) gets the same wire shape without each
        // implementor needing to remember [JsonIgnore] on the property.
        // Read paths are unaffected -- the property is computed locally and
        // never sent or received over the wire.
        opts.TypeInfoResolver = new DefaultJsonTypeInfoResolver
        {
            Modifiers =
            {
                StripSchemaNameFromSurrealRecords,
                StripTablePrefixFromIdProperty,
            },
        };

        return opts;
    }

    private static void StripSchemaNameFromSurrealRecords(JsonTypeInfo typeInfo)
    {
        if (!typeof(ISurrealRecord).IsAssignableFrom(typeInfo.Type)) return;
        // Match on the CLR MEMBER name (SchemaName), not the policy-applied JSON
        // name — the wire name varies with SurrealNaming.Convention (schemaName
        // under camelCase, schema_name under snake_case), and matching the
        // emitted name would miss it under the snake_case policy and leak
        // `schema_name` into the CONTENT body (SCHEMAFULL "no such field").
        for (var i = typeInfo.Properties.Count - 1; i >= 0; i--)
        {
            var p = typeInfo.Properties[i];
            var clrName = (p.AttributeProvider as System.Reflection.MemberInfo)?.Name;
            if (string.Equals(clrName, nameof(ISurrealRecord.SchemaName), System.StringComparison.Ordinal)
                // Fallback: also match the policy-applied JSON name forms, so a
                // POCO that hides the member or uses [JsonPropertyName] is covered.
                || string.Equals(p.Name, "schemaName", System.StringComparison.OrdinalIgnoreCase)
                || string.Equals(p.Name, "schema_name", System.StringComparison.OrdinalIgnoreCase)
                || string.Equals(p.Name, "SchemaName", System.StringComparison.OrdinalIgnoreCase))
            {
                typeInfo.Properties.RemoveAt(i);
            }
        }
    }

    /// <summary>
    /// SurrealDB's <c>/rpc</c> + <c>/sql</c> responses always serialize the
    /// record id as <c>&lt;table&gt;:&lt;id&gt;</c> regardless of how the
    /// schema declares the <c>id</c> field's <c>TYPE</c>. POCOs that hold
    /// the id as a bare <c>string</c> (the 30+ adapter shapes in
    /// Providers/Stores/Surreal/*.cs) would otherwise need a per-store
    /// <c>StripIdPrefix</c> helper to peel off the table prefix before
    /// <c>Guid.ParseExact</c> on the way out. Centralize the strip here so
    /// every implementor gets the bare-hex form for free.
    /// <para>
    /// Scope is intentionally narrow: the <c>id</c> property of any
    /// <see cref="ISurrealRecord"/> that's typed as <c>string</c>. Other
    /// string fields (FK <c>*_id</c> columns) are NOT touched — those
    /// usually want explicit handling in the store anyway, and a broader
    /// auto-strip risks false positives on unrelated text payloads.
    /// </para>
    /// </summary>
    private static void StripTablePrefixFromIdProperty(JsonTypeInfo typeInfo)
    {
        if (!typeof(ISurrealRecord).IsAssignableFrom(typeInfo.Type)) return;
        foreach (var p in typeInfo.Properties)
        {
            if (p.PropertyType != typeof(string)) continue;
            if (!string.Equals(p.Name, "id", System.StringComparison.OrdinalIgnoreCase)
                && !string.Equals(p.Name, "Id", System.StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            p.CustomConverter = SurrealIdStringConverter.Instance;
        }
    }
}

/// <summary>
/// JSON converter for the <c>id</c> property of an <see cref="ISurrealRecord"/>
/// POCO that holds it as a bare <see cref="string"/>. Strips the
/// <c>&lt;table&gt;:</c> prefix on read (so <c>FromPoco</c> + <c>Guid.ParseExact</c>
/// see the bare hex) and writes the value through unchanged (the wave-1
/// stores already write the bare hex form on insert).
/// </summary>
internal sealed class SurrealIdStringConverter : System.Text.Json.Serialization.JsonConverter<string>
{
    public static readonly SurrealIdStringConverter Instance = new();

    public override string? Read(
        ref System.Text.Json.Utf8JsonReader reader,
        System.Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == System.Text.Json.JsonTokenType.Null) return null;
        if (reader.TokenType != System.Text.Json.JsonTokenType.String)
        {
            // Tolerate non-string tokens by deferring to the default reader
            // path; SurrealDB occasionally emits numeric ids for numeric-keyed
            // tables, and re-throwing here would mask the real failure.
            return reader.GetString();
        }
        var raw = reader.GetString();
        if (string.IsNullOrEmpty(raw)) return raw;
        var colon = raw!.IndexOf(':');
        if (colon < 0 || colon >= raw.Length - 1) return raw;
        // Strip the table prefix. SurrealDB wraps non-simple ids in `⟨ ⟩`
        // (U+27E8 / U+27E9 mathematical angle brackets) when the id contains
        // characters outside [a-zA-Z0-9_]; peel those off too.
        return raw.Substring(colon + 1).Trim('⟨', '⟩');
    }

    public override void Write(
        System.Text.Json.Utf8JsonWriter writer,
        string value,
        JsonSerializerOptions options)
    {
        // Wave-1 stores always write the bare-hex form on insert (the table
        // prefix is supplied by `type::record($_t, $_id)` in the SQL),
        // so the value passes through unchanged.
        if (value is null) writer.WriteNullValue();
        else writer.WriteStringValue(value);
    }
}
