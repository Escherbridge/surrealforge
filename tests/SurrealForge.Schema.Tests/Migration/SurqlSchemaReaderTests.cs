// SPDX-License-Identifier: UNLICENSED
// SurqlSchemaReader tests -- parsing emitted .surql back into a SchemaModel so
// it can be the reconcile DESIRED model when no assembly is available (the
// deploy-container path).

using System.Linq;
using FluentAssertions;
using SurrealForge.Schema.Migration;

namespace SurrealForge.Schema.Tests.Migration
{
    public class SurqlSchemaReaderTests
    {
        [Fact]
        public void Parses_table_fields_and_index_ignoring_comments_and_params()
        {
            var surql = @"
-- ============================================================
-- Table: api_key
-- ============================================================

DEFINE TABLE IF NOT EXISTS api_key SCHEMAFULL;

DEFINE PARAM IF NOT EXISTS $api_key_scope VALUE [""read"", ""write""];

DEFINE FIELD IF NOT EXISTS prefix ON TABLE api_key TYPE string;
DEFINE FIELD IF NOT EXISTS scopes ON TABLE api_key TYPE array<string>
    DEFAULT [];

-- ── Indexes ──
DEFINE INDEX IF NOT EXISTS api_key_prefix
    ON TABLE api_key
    FIELDS prefix
    UNIQUE;
";
            var model = SurqlSchemaReader.Parse(surql);

            model.Entities.Should().ContainSingle();
            var t = model.Entities.Single();
            t.Name.Should().Be("api_key");
            t.Attributes.Select(a => a.Name).Should().BeEquivalentTo(new[] { "prefix", "scopes" });
            t.Attributes.Single(a => a.Name == "scopes").Type.Should().Be("array<string>");
            t.Indexes.Should().ContainSingle(i => i.Name == "api_key_prefix" && i.IsUnique);
        }

        [Fact]
        public void Skips_nested_object_slot_fields()
        {
            var surql = @"
DEFINE TABLE IF NOT EXISTS t SCHEMAFULL;
DEFINE FIELD IF NOT EXISTS edges ON TABLE t TYPE array<object> FLEXIBLE;
DEFINE FIELD OVERWRITE edges.* ON TABLE t TYPE object FLEXIBLE;
";
            var model = SurqlSchemaReader.Parse(surql);
            var t = model.Entities.Single();
            t.Attributes.Select(a => a.Name).Should().Equal("edges");
        }

        [Fact]
        public void SplitStatements_respects_semicolons_inside_strings_and_brackets()
        {
            var surql = "DEFINE FIELD a ON t TYPE string ASSERT $value != \"x;y\"; DEFINE FIELD b ON t TYPE array<int>;";
            var stmts = SurqlSchemaReader.SplitStatements(surql).ToList();
            stmts.Should().HaveCount(2);
            stmts[0].Should().Contain("x;y");
        }

        [Fact]
        public void Round_trips_to_a_model_the_diff_sees_as_equal_to_the_live_shape()
        {
            // The desired (.surql) and actual (introspected) parsers share the
            // DDL extractor, so a field emitted as array<string> reads back with
            // the same normalized type on both sides -> no false drift.
            var surql = "DEFINE TABLE IF NOT EXISTS t SCHEMAFULL; DEFINE FIELD IF NOT EXISTS s ON TABLE t TYPE array<string>;";
            var desired = SurqlSchemaReader.Parse(surql);

            var actualBody = @"[{""status"":""OK"",""result"":{""fields"":{
                ""s"":""DEFINE FIELD s ON t TYPE array<string>""}}}]";
            var actualEntity = LiveSchemaIntrospector.ParseTableInfo("t", actualBody);
            var actual = SchemaModelTestFactory.NewModel(actualEntity);

            SchemaDiff.Diff(desired, actual).HasChanges.Should().BeFalse();
        }
    }
}
