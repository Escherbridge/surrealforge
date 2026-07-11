// SPDX-License-Identifier: UNLICENSED
// LiveSchemaIntrospector parse tests -- turning INFO FOR DB / INFO FOR TABLE
// JSON replies (and the DEFINE-string values inside them) back into a
// SchemaModel. No live server: we feed captured response bodies.

using System.Linq;
using FluentAssertions;
using SurrealForge.Schema.Migration;
using SurrealForge.Schema.Model;

namespace SurrealForge.Schema.Tests.Migration
{
    public class LiveSchemaIntrospectorTests
    {
        [Fact]
        public void ParseTableNames_extracts_tables_from_INFO_FOR_DB()
        {
            // Statement-wrapped shape: [{ status, result: { tables: {...} } }]
            var body = @"[{""status"":""OK"",""result"":{""tables"":{
                ""api_key"":""DEFINE TABLE api_key SCHEMAFULL"",
                ""wallet"":""DEFINE TABLE wallet SCHEMAFULL""}}}]";

            var names = LiveSchemaIntrospector.ParseTableNames(body);

            names.Should().BeEquivalentTo(new[] { "api_key", "wallet" });
        }

        [Fact]
        public void ParseTableNames_tolerates_bare_result_object()
        {
            var body = @"{""tables"":{""only"":""DEFINE TABLE only""}}";
            LiveSchemaIntrospector.ParseTableNames(body).Should().Equal("only");
        }

        [Fact]
        public void ParseFieldDefinition_captures_type_default_assert_readonly()
        {
            var ddl = "DEFINE FIELD scopes ON api_key TYPE array<string> DEFAULT [] ASSERT $value != NONE READONLY PERMISSIONS FULL";
            var attr = LiveSchemaIntrospector.ParseFieldDefinition("scopes", ddl);

            attr.Should().NotBeNull();
            attr!.Type.Should().Be("array<string>");
            SchemaModelAccessors.FirstArg(attr.Annotations, "default").Should().Be("[]");
            SchemaModelAccessors.FirstArg(attr.Annotations, "assert").Should().Be("$value != NONE");
            SchemaModelAccessors.HasDirective(attr.Annotations, "readonly").Should().BeTrue();
        }

        [Fact]
        public void ParseFieldDefinition_captures_nested_option_array_type()
        {
            // The keyword extractor must not stop the TYPE token at the inner
            // whitespace/brackets: option<array<string>> is one token.
            var ddl = "DEFINE FIELD tags ON t TYPE option<array<string>> DEFAULT NONE";
            var attr = LiveSchemaIntrospector.ParseFieldDefinition("tags", ddl);
            attr!.Type.Should().Be("option<array<string>>");
        }

        [Fact]
        public void ParseFieldDefinition_strips_FLEXIBLE_into_annotation()
        {
            var ddl = "DEFINE FIELD meta ON t FLEXIBLE TYPE object";
            var attr = LiveSchemaIntrospector.ParseFieldDefinition("meta", ddl);
            attr!.Type.Should().Be("object");
            SchemaModelAccessors.HasDirective(attr.Annotations, "flexible").Should().BeTrue();
        }

        [Fact]
        public void ParseIndexDefinition_reads_fields_and_unique()
        {
            var ddl = "DEFINE INDEX ak_prefix ON api_key FIELDS prefix, hash UNIQUE";
            var idx = LiveSchemaIntrospector.ParseIndexDefinition("ak_prefix", ddl);
            idx.Should().NotBeNull();
            idx!.Fields.Should().Equal("prefix", "hash");
            idx.IsUnique.Should().BeTrue();
        }

        [Fact]
        public void ParseTableInfo_builds_entity_and_skips_nested_field_slots()
        {
            var body = @"[{""status"":""OK"",""result"":{
                ""fields"":{
                    ""scopes"":""DEFINE FIELD scopes ON api_key TYPE array<string>"",
                    ""scopes[*]"":""DEFINE FIELD scopes[*] ON api_key TYPE string"",
                    ""prefix"":""DEFINE FIELD prefix ON api_key TYPE string""
                },
                ""indexes"":{
                    ""ak_prefix"":""DEFINE INDEX ak_prefix ON api_key FIELDS prefix UNIQUE""
                }}}]";

            var entity = LiveSchemaIntrospector.ParseTableInfo("api_key", body);

            entity.Name.Should().Be("api_key");
            entity.Attributes.Select(a => a.Name).Should().BeEquivalentTo(new[] { "scopes", "prefix" });
            entity.Attributes.Single(a => a.Name == "scopes").Type.Should().Be("array<string>");
            entity.Indexes.Should().ContainSingle(i => i.Name == "ak_prefix" && i.IsUnique);
        }

        [Fact]
        public void NormalizeType_collapses_whitespace_and_lowercases()
        {
            LiveSchemaIntrospector.NormalizeType("option< array<string> >").Should().Be("option<array<string>>");
            LiveSchemaIntrospector.NormalizeType("ARRAY<String>").Should().Be("array<string>");
        }

        [Fact]
        public void NormalizeType_canonicalizes_SurrealDB3_optional_union_rendering()
        {
            // SurrealDB 3.x reports option<string> back as `none | string`.
            LiveSchemaIntrospector.NormalizeType("none | string").Should().Be("option<string>");
            LiveSchemaIntrospector.NormalizeType("string | none").Should().Be("option<string>");
            LiveSchemaIntrospector.NormalizeType("none | array<string>").Should().Be("option<array<string>>");
            // A genuine multi-branch (non-none) union is left intact.
            LiveSchemaIntrospector.NormalizeType("int | string").Should().Be("int|string");
        }

        [Fact]
        public void ParseFieldDefinition_reads_SurrealDB3_optional_field_as_option()
        {
            // The exact live read-back shape: `none | string PERMISSIONS FULL`.
            var ddl = "DEFINE FIELD s ON probe TYPE none | string PERMISSIONS FULL";
            var attr = LiveSchemaIntrospector.ParseFieldDefinition("s", ddl);
            attr!.Type.Should().Be("option<string>");
        }

        [Fact]
        public void ExtractClauseValue_returns_null_for_absent_keyword()
        {
            LiveSchemaIntrospector.ExtractClauseValue("DEFINE FIELD x ON t TYPE string", "DEFAULT")
                .Should().BeNull();
        }
    }
}
