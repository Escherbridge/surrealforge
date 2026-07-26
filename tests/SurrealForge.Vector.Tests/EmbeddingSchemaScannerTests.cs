// SPDX-License-Identifier: MIT
// [Embedded] scan → EmbeddingJobDefinition tests.

using FluentAssertions;
using SurrealForge.Client.Schema;
using SurrealForge.Vector;

namespace SurrealForge.Vector.Tests;

public sealed class EmbeddingSchemaScannerTests
{
    [SurrealTable("document")]
    public sealed class EmbeddedDoc
    {
        [Embedded("embedding", Profile = "mini", Mode = EmbeddingMode.Batched)]
        public string Body { get; set; } = string.Empty;

        [Embedded("title_vec")]
        [Column(Name = "title_text")]
        public string Title { get; set; } = string.Empty;

        public string Untouched { get; set; } = string.Empty;
    }

    [SurrealTable("broken")]
    public sealed class NonStringSource
    {
        [Embedded("embedding")]
        public int Count { get; set; }
    }

    [Fact]
    public void Scan_surfaces_one_definition_per_embedded_property()
    {
        var defs = EmbeddingSchemaScanner.Scan<EmbeddedDoc>();

        defs.Should().HaveCount(2);

        var body = defs.Single(d => d.TargetColumn == "embedding");
        body.EntityType.Should().Be(typeof(EmbeddedDoc));
        body.Table.Should().Be("document");
        body.SourceColumn.Should().Be("body");
        body.HashColumn.Should().Be("embedding_hash");
        body.Profile.Should().Be("mini");
        body.Mode.Should().Be(EmbeddingMode.Batched);
        body.JobName.Should().Be("document.embedding");

        var title = defs.Single(d => d.TargetColumn == "title_vec");
        title.SourceColumn.Should().Be("title_text", "an explicit [Column(Name=...)] wins over the naming convention");
        title.Profile.Should().Be(SurrealVectorOptions.DefaultProfile);
        title.Mode.Should().Be(EmbeddingMode.WriteTime);
    }

    [Fact]
    public void Scan_throws_loudly_on_a_non_string_source_property()
    {
        var act = () => EmbeddingSchemaScanner.Scan<NonStringSource>();

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*Count*requires a string*");
    }

    [Fact]
    public void Scan_of_a_type_without_embedded_properties_is_empty()
    {
        EmbeddingSchemaScanner.Scan<PlainDoc>().Should().BeEmpty();
    }

    public sealed class PlainDoc
    {
        public string Body { get; set; } = string.Empty;
    }
}
