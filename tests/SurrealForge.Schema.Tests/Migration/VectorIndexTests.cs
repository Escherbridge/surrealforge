// SPDX-License-Identifier: MIT
// Vector (HNSW/MTREE) index coverage: scanner must carry every attribute
// clause, emitter must render real vector DDL, parser must survive live INFO
// derived-token noise, and reconcile must see no drift against server
// defaults. See src/SurrealForge.Schema/Migration/AGENTS.md §Vector indexes.

using System;
using System.Linq;
using FluentAssertions;
using SurrealForge.Client.Schema;
using SurrealForge.Schema.Generator;
using SurrealForge.Schema.Migration;
using SurrealForge.Schema.Model;

namespace SurrealForge.Schema.Tests.Migration
{
    public class VectorIndexTests
    {
        // ── fixture POCOs ──────────────────────────────────────────────────

        [SurrealTable("document")]
        private sealed class DocumentPoco
        {
            [Column(Order = 1)]
            public string? Title { get; set; }

            [Column(Order = 2, Type = "array<float>")]
            [HnswIndex("hnsw_document_embedding", Dimension = 384, Distance = "COSINE", Type = "F32", Efc = 150, M = 12)]
            public float[]? Embedding { get; set; }
        }

        [SurrealTable("chunk")]
        [ExtraSurrealField("embedding", "array<float>", Order = 2)]
        [HnswIndex("hnsw_chunk_embedding", Dimension = 8, Fields = new[] { "embedding" })]
        private sealed class ChunkPoco
        {
            [Column(Order = 1)]
            public string? Text { get; set; }
        }

        [SurrealTable("photo")]
        private sealed class MTreePoco
        {
            [Column(Order = 1, Type = "array<float>")]
            [MTreeIndex("mtree_photo_embedding", Dimension = 512, Distance = "EUCLIDEAN", Capacity = 40)]
            public double[]? Embedding { get; set; }
        }

        [SurrealTable("bad_dim")]
        private sealed class MissingDimensionPoco
        {
            [Column(Order = 1, Type = "array<float>")]
            [HnswIndex("hnsw_bad")]
            public float[]? Embedding { get; set; }
        }

        [SurrealTable("bad_clr")]
        private sealed class NonVectorClrPoco
        {
            [Column(Order = 1)]
            [HnswIndex("hnsw_bad", Dimension = 8)]
            public string? Embedding { get; set; }
        }

        [SurrealTable("bad_field")]
        [HnswIndex("hnsw_bad", Dimension = 8, Fields = new[] { "no_such_column" })]
        private sealed class UnknownFieldPoco
        {
            [Column(Order = 1)]
            public string? Text { get; set; }
        }

        // ── scanner ────────────────────────────────────────────────────────

        [Fact]
        public void Scanner_carries_all_hnsw_params()
        {
            var model = AttributeSchemaScanner.ScanType(typeof(DocumentPoco));
            var idx = model.Entities.Single().Indexes.Single();

            idx.Name.Should().Be("hnsw_document_embedding");
            idx.Fields.Should().Equal("embedding");
            idx.IsUnique.Should().BeFalse();
            idx.VectorKind.Should().Be(VectorIndexKind.Hnsw);
            idx.Dimension.Should().Be(384);
            idx.Distance.Should().Be("COSINE");
            idx.VectorType.Should().Be("F32");
            idx.Efc.Should().Be(150);
            idx.M.Should().Be(12);
            idx.Capacity.Should().BeNull();
        }

        [Fact]
        public void Scanner_carries_mtree_params()
        {
            var model = AttributeSchemaScanner.ScanType(typeof(MTreePoco));
            var idx = model.Entities.Single().Indexes.Single();

            idx.VectorKind.Should().Be(VectorIndexKind.Mtree);
            idx.Dimension.Should().Be(512);
            idx.Distance.Should().Be("EUCLIDEAN");
            idx.Capacity.Should().Be(40);
            idx.Efc.Should().BeNull();
            idx.M.Should().BeNull();
        }

        [Fact]
        public void Scanner_supports_class_level_index_over_extra_field()
        {
            var model = AttributeSchemaScanner.ScanType(typeof(ChunkPoco));
            var entity = model.Entities.Single();

            entity.Attributes.Select(a => a.Name).Should().Contain("embedding");
            var idx = entity.Indexes.Single();
            idx.Name.Should().Be("hnsw_chunk_embedding");
            idx.Fields.Should().Equal("embedding");
            idx.VectorKind.Should().Be(VectorIndexKind.Hnsw);
            idx.Dimension.Should().Be(8);
        }

        [Fact]
        public void Scanner_throws_when_dimension_missing()
        {
            Action act = () => AttributeSchemaScanner.ScanType(typeof(MissingDimensionPoco));
            act.Should().Throw<InvalidOperationException>().WithMessage("*Dimension*");
        }

        [Fact]
        public void Scanner_throws_when_clr_type_is_not_a_vector()
        {
            Action act = () => AttributeSchemaScanner.ScanType(typeof(NonVectorClrPoco));
            act.Should().Throw<InvalidOperationException>().WithMessage("*not a numeric vector*");
        }

        [Fact]
        public void Scanner_throws_when_class_level_field_is_unknown()
        {
            Action act = () => AttributeSchemaScanner.ScanType(typeof(UnknownFieldPoco));
            act.Should().Throw<InvalidOperationException>().WithMessage("*unknown column*");
        }

        // ── emitter ────────────────────────────────────────────────────────

        [Fact]
        public void Emitter_renders_full_hnsw_clause()
        {
            var idx = new SchemaIndex("hnsw_document_embedding", new[] { "embedding" }, false, 0,
                VectorIndexKind.Hnsw, 384, "COSINE", "F32", 150, 12, null);
            var ddl = SurqlEmitter.EmitIndexStatement("document", idx, SurqlEmitter.EmitOptions.Default);

            ddl.Should().Be(
                "DEFINE INDEX IF NOT EXISTS hnsw_document_embedding\n" +
                "    ON TABLE document\n" +
                "    FIELDS embedding\n" +
                "    HNSW DIMENSION 384 DIST COSINE TYPE F32 EFC 150 M 12;\n");
        }

        [Fact]
        public void Emitter_renders_mtree_clause_omitting_unset_knobs()
        {
            var idx = new SchemaIndex("mtree_photo_embedding", new[] { "embedding" }, false, 0,
                VectorIndexKind.Mtree, 512, "EUCLIDEAN", null, null, null, 40);
            var ddl = SurqlEmitter.EmitIndexStatement("photo", idx, SurqlEmitter.EmitOptions.Strict);

            ddl.Should().Be(
                "DEFINE INDEX mtree_photo_embedding\n" +
                "    ON TABLE photo\n" +
                "    FIELDS embedding\n" +
                "    MTREE DIMENSION 512 DIST EUCLIDEAN CAPACITY 40;\n");
        }

        [Fact]
        public void Emitter_plain_index_is_byte_identical_to_legacy_shape()
        {
            var idx = new SchemaIndex("t_a", new[] { "a" }, true, 0);
            var ddl = SurqlEmitter.EmitIndexStatement("t", idx, SurqlEmitter.EmitOptions.Default);

            ddl.Should().Be(
                "DEFINE INDEX IF NOT EXISTS t_a\n" +
                "    ON TABLE t\n" +
                "    FIELDS a\n" +
                "    UNIQUE;\n");
        }

        // ── introspector parse ─────────────────────────────────────────────

        [Fact]
        public void Parse_survives_live_hnsw_derived_tokens()
        {
            // Shape as returned by INFO FOR TABLE on SurrealDB 2.x: M0/LM are
            // derived from M and must not poison the parse.
            var ddl = "DEFINE INDEX hnsw_document_embedding ON document FIELDS embedding "
                + "HNSW DIMENSION 384 DIST COSINE TYPE F32 EFC 150 M 12 M0 24 LM 0.40242960438184466f";
            var idx = LiveSchemaIntrospector.ParseIndexDefinition("hnsw_document_embedding", ddl)!;

            idx.VectorKind.Should().Be(VectorIndexKind.Hnsw);
            idx.Fields.Should().Equal("embedding");
            idx.Dimension.Should().Be(384);
            idx.Distance.Should().Be("COSINE");
            idx.VectorType.Should().Be("F32");
            idx.Efc.Should().Be(150);
            idx.M.Should().Be(12);
            idx.Capacity.Should().BeNull();
            idx.IsUnique.Should().BeFalse();
        }

        [Fact]
        public void Parse_survives_live_mtree_cache_tokens()
        {
            var ddl = "DEFINE INDEX mt ON photo FIELDS embedding "
                + "MTREE DIMENSION 3 DIST EUCLIDEAN TYPE F64 CAPACITY 40 "
                + "DOC_IDS_ORDER 100 DOC_IDS_CACHE 100 MTREE_CACHE 100";
            var idx = LiveSchemaIntrospector.ParseIndexDefinition("mt", ddl)!;

            idx.VectorKind.Should().Be(VectorIndexKind.Mtree);
            idx.Dimension.Should().Be(3);
            idx.Distance.Should().Be("EUCLIDEAN");
            idx.VectorType.Should().Be("F64");
            idx.Capacity.Should().Be(40);
            idx.Efc.Should().BeNull();
            idx.M.Should().BeNull();
        }

        [Fact]
        public void Parse_plain_index_has_no_vector_members()
        {
            var idx = LiveSchemaIntrospector.ParseIndexDefinition(
                "t_a", "DEFINE INDEX t_a ON t FIELDS a UNIQUE")!;
            idx.VectorKind.Should().BeNull();
            idx.IsUnique.Should().BeTrue();
        }

        // ── diff / reconcile drift ─────────────────────────────────────────

        private static SchemaModel ModelWithIndex(string table, SchemaIndex idx)
        {
            var entity = new SchemaEntity(table,
                new[] { new SchemaAttribute("embedding", "array<float>", false, null, Array.Empty<SchemaAnnotation>(), 0) },
                Array.Empty<SchemaAnnotation>(), new[] { idx }, 0);
            return new SchemaModel("(test)", new[] { entity }, Array.Empty<SchemaRelationship>());
        }

        [Fact]
        public void Diff_reports_no_drift_when_live_side_shows_server_defaults()
        {
            // POCO left Type/Efc/M unset; the server filled F64/150/12.
            var desired = ModelWithIndex("document", new SchemaIndex(
                "hnsw", new[] { "embedding" }, false, 0, VectorIndexKind.Hnsw, 384, "COSINE"));
            var actual = ModelWithIndex("document", LiveSchemaIntrospector.ParseIndexDefinition(
                "hnsw", "DEFINE INDEX hnsw ON document FIELDS embedding "
                + "HNSW DIMENSION 384 DIST COSINE TYPE F64 EFC 150 M 12 M0 24")!);

            SchemaDiff.Diff(desired, actual).Indexes.Should().BeEmpty();
        }

        [Fact]
        public void Diff_reports_change_when_dimension_differs()
        {
            var desired = ModelWithIndex("document", new SchemaIndex(
                "hnsw", new[] { "embedding" }, false, 0, VectorIndexKind.Hnsw, 768, "COSINE"));
            var actual = ModelWithIndex("document", new SchemaIndex(
                "hnsw", new[] { "embedding" }, false, 0, VectorIndexKind.Hnsw, 384, "COSINE"));

            var change = SchemaDiff.Diff(desired, actual).Indexes.Single();
            change.Kind.Should().Be(IndexChangeKind.Changed);
            change.Detail.Should().Contain("dim=768").And.Contain("dim=384");
        }

        [Fact]
        public void Diff_reports_change_when_plain_index_becomes_vector()
        {
            var desired = ModelWithIndex("document", new SchemaIndex(
                "idx", new[] { "embedding" }, false, 0, VectorIndexKind.Hnsw, 384, "COSINE"));
            var actual = ModelWithIndex("document", new SchemaIndex(
                "idx", new[] { "embedding" }, false, 0));

            SchemaDiff.Diff(desired, actual).Indexes.Single().Kind.Should().Be(IndexChangeKind.Changed);
        }

        [Fact]
        public void Diff_reports_change_when_pinned_tuning_param_differs()
        {
            var desired = ModelWithIndex("document", new SchemaIndex(
                "hnsw", new[] { "embedding" }, false, 0, VectorIndexKind.Hnsw, 384, "COSINE", null, 200, null, null));
            var actual = ModelWithIndex("document", new SchemaIndex(
                "hnsw", new[] { "embedding" }, false, 0, VectorIndexKind.Hnsw, 384, "COSINE", "F64", 150, 12, null));

            SchemaDiff.Diff(desired, actual).Indexes.Single().Kind.Should().Be(IndexChangeKind.Changed);
        }

        // ── full round-trip: scan → emit → read back → diff ────────────────

        [Fact]
        public void Roundtrip_scan_emit_read_reports_no_index_drift()
        {
            var desired = AttributeSchemaScanner.ScanType(typeof(DocumentPoco));
            var surql = SurqlEmitter.Emit(desired);
            var readBack = SurqlSchemaReader.Parse(surql);

            SchemaDiff.Diff(desired, readBack).Indexes.Should().BeEmpty();
        }

        [Fact]
        public void Reconcile_statement_for_changed_vector_index_carries_full_clause()
        {
            var idx = new SchemaIndex("hnsw", new[] { "embedding" }, false, 0,
                VectorIndexKind.Hnsw, 384, "COSINE", "F32", null, null, null);
            var ddl = SurqlEmitter.EmitIndexStatement("document", idx, SurqlEmitter.EmitOptions.Evolve);

            ddl.Should().Contain("DEFINE INDEX OVERWRITE hnsw");
            ddl.Should().Contain("HNSW DIMENSION 384 DIST COSINE TYPE F32");
        }
    }
}
