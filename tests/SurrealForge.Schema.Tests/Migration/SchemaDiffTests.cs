// SPDX-License-Identifier: UNLICENSED
// SchemaDiff unit tests -- the model-driven diff that detects field TYPE
// changes (the production bug), plus adds/removes/constraint changes and the
// no-op case. Uses SchemaModelTestFactory for terse fixtures.

using System.Linq;
using FluentAssertions;
using SurrealForge.Schema.Migration;
using static SurrealForge.Schema.Tests.Migration.SchemaModelTestFactory;

namespace SurrealForge.Schema.Tests.Migration
{
    public class SchemaDiffTests
    {
        [Fact]
        public void Identical_models_produce_no_changes()
        {
            var desired = NewModel(Table("api_key", Field("scopes", "array<string>")));
            var actual = NewModel(Table("api_key", Field("scopes", "array<string>")));

            var cs = SchemaDiff.Diff(desired, actual);

            cs.HasChanges.Should().BeFalse();
            cs.IsEmpty.Should().BeTrue();
        }

        [Fact]
        public void Field_type_change_is_detected_as_TypeChanged()
        {
            // The exact AZOA production case: option<string> -> array<string>.
            var desired = NewModel(Table("api_key", Field("scopes", "array<string>")));
            var actual = NewModel(Table("api_key", Field("scopes", "option<string>")));

            var cs = SchemaDiff.Diff(desired, actual);

            cs.Fields.Should().ContainSingle();
            var fc = cs.Fields.Single();
            fc.Kind.Should().Be(FieldChangeKind.TypeChanged);
            fc.Table.Should().Be("api_key");
            fc.Field.Should().Be("scopes");
            fc.Detail.Should().Contain("option<string>").And.Contain("array<string>");
        }

        [Fact]
        public void Type_change_that_is_not_a_widening_is_flagged_destructive()
        {
            // option<string> -> array<string> is NOT a value-preserving widen;
            // existing scalar rows may not coerce, so it needs opt-in.
            var desired = NewModel(Table("api_key", Field("scopes", "array<string>")));
            var actual = NewModel(Table("api_key", Field("scopes", "option<string>")));

            var fc = SchemaDiff.Diff(desired, actual).Fields.Single();
            fc.IsDestructive.Should().BeTrue();
        }

        [Fact]
        public void Making_a_field_optional_is_a_non_destructive_widening()
        {
            // string -> option<string> never loses data.
            var desired = NewModel(Table("t", Field("name", "option<string>")));
            var actual = NewModel(Table("t", Field("name", "string")));

            var fc = SchemaDiff.Diff(desired, actual).Fields.Single();
            fc.Kind.Should().Be(FieldChangeKind.TypeChanged);
            fc.IsDestructive.Should().BeFalse();
        }

        [Fact]
        public void Added_field_is_detected_and_non_destructive()
        {
            var desired = NewModel(Table("t", Field("a", "string"), Field("b", "int")));
            var actual = NewModel(Table("t", Field("a", "string")));

            var fc = SchemaDiff.Diff(desired, actual).Fields.Single();
            fc.Kind.Should().Be(FieldChangeKind.Added);
            fc.Field.Should().Be("b");
            fc.IsDestructive.Should().BeFalse();
        }

        [Fact]
        public void Removed_field_is_detected_and_destructive()
        {
            var desired = NewModel(Table("t", Field("a", "string")));
            var actual = NewModel(Table("t", Field("a", "string"), Field("stale", "int")));

            var fc = SchemaDiff.Diff(desired, actual).Fields.Single();
            fc.Kind.Should().Be(FieldChangeKind.Removed);
            fc.Field.Should().Be("stale");
            fc.IsDestructive.Should().BeTrue();
        }

        [Fact]
        public void Default_change_with_same_type_is_ConstraintChanged()
        {
            var desired = NewModel(Table("t", Field("flag", "bool", Default("true"))));
            var actual = NewModel(Table("t", Field("flag", "bool", Default("false"))));

            var fc = SchemaDiff.Diff(desired, actual).Fields.Single();
            fc.Kind.Should().Be(FieldChangeKind.ConstraintChanged);
            fc.Detail.Should().Contain("default");
        }

        [Fact]
        public void Assert_whitespace_difference_is_not_a_change()
        {
            var desired = NewModel(Table("t", Field("x", "string", Assert("$value != NONE AND $value != \"\""))));
            var actual = NewModel(Table("t", Field("x", "string", Assert("$value  !=  NONE   AND $value != \"\""))));

            SchemaDiff.Diff(desired, actual).HasChanges.Should().BeFalse();
        }

        [Fact]
        public void New_table_is_TableChange_added_without_per_field_noise()
        {
            var desired = NewModel(Table("t", Field("a", "string")), Table("fresh", Field("b", "int")));
            var actual = NewModel(Table("t", Field("a", "string")));

            var cs = SchemaDiff.Diff(desired, actual);
            cs.Tables.Should().ContainSingle(t => t.Table == "fresh" && t.Added);
            // Fields of a brand-new table are NOT enumerated as field changes
            // (the normal file apply covers them).
            cs.Fields.Should().BeEmpty();
        }

        [Fact]
        public void Dropped_table_is_TableChange_removed_and_destructive()
        {
            var desired = NewModel(Table("t", Field("a", "string")));
            var actual = NewModel(Table("t", Field("a", "string")), Table("legacy", Field("b", "int")));

            var cs = SchemaDiff.Diff(desired, actual);
            cs.Tables.Should().ContainSingle(t => t.Table == "legacy" && t.Removed);
            cs.HasDestructive.Should().BeTrue();
        }

        [Fact]
        public void Index_added_and_removed_are_detected()
        {
            var desired = NewModel(TableWithIndexes("t",
                new[] { Field("a", "string") },
                new[] { Index("t_a", true, "a") }));
            var actual = NewModel(TableWithIndexes("t",
                new[] { Field("a", "string") },
                new[] { Index("t_old", false, "a") }));

            var cs = SchemaDiff.Diff(desired, actual);
            cs.Indexes.Should().Contain(i => i.Index == "t_a" && i.Kind == IndexChangeKind.Added);
            cs.Indexes.Should().Contain(i => i.Index == "t_old" && i.Kind == IndexChangeKind.Removed && i.IsDestructive);
        }

        [Fact]
        public void Widening_helper_recognizes_optional_and_numeric_widen()
        {
            SchemaDiff.IsWidening("string", "option<string>").Should().BeTrue();
            SchemaDiff.IsWidening("int", "decimal").Should().BeTrue();
            SchemaDiff.IsWidening("option<string>", "array<string>").Should().BeFalse();
            SchemaDiff.IsWidening("string", "int").Should().BeFalse();
        }
    }
}
