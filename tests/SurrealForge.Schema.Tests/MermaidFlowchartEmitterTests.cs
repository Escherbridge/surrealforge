// SPDX-License-Identifier: UNLICENSED
// SurrealForge.Schema.Tests -- Flowchart emitter smoke tests.
//
// The MermaidFlowchartEmitter renders the "graph LR" portfolio shape
// requested by the C#-first pivot. These tests assert the structural
// contract against synthetically-constructed SchemaModel inputs:
//   - one slice file per @surreal.slice group
//   - one master file with subgraph clusters per slice + every edge
//   - orphan entities cluster under "_unassigned"
//   - cardinality glyph -> "1:N" / "N:M" / etc shorthand
//
// Live attribute-driven coverage lives in
// YourApp.Tests.Persistence.SurrealDb.AttributePocoByteEquivalenceTests
// which discovers every [SurrealTable]-decorated POCO at runtime.

using System;
using System.Collections.Generic;
using FluentAssertions;
using SurrealForge.Schema.Generator;
using SurrealForge.Schema.Model;

namespace SurrealForge.Schema.Tests
{
    public class MermaidFlowchartEmitterTests
    {
        [Fact]
        public void Emit_produces_graph_lr_with_classDef_and_node_per_entity()
        {
            var model = BuildModel(
                slices: new[] { ("blog", new[] { "user", "post" }) },
                relationships: new[]
                {
                    ("user", "post", "||--o{", "AUTHORED"),
                });

            var result = MermaidFlowchartEmitter.Emit(new[] { model });

            result.SliceFiles.Should().ContainKey("blog.flowchart.mermaid");
            var slice = result.SliceFiles["blog.flowchart.mermaid"];

            slice.Should().Contain("graph LR");
            slice.Should().Contain("user[\"user: Node\"]:::nodeClass");
            slice.Should().Contain("post[\"post: Node\"]:::nodeClass");
            slice.Should().Contain("classDef nodeClass");
            slice.Should().Contain("user -- \"AUTHORED [1:N]\" --> post");
        }

        [Fact]
        public void Master_diagram_contains_subgraph_per_slice_and_classdef_block()
        {
            var model = BuildModel(
                ("blog", new[] { "user", "post" }),
                ("metrics", new[] { "metric" }));

            var result = MermaidFlowchartEmitter.Emit(new[] { model });

            result.MasterFlowchart.Should().Contain("graph LR");
            // Subgraph IDs are prefixed with `slice_` to avoid collision with
            // entity names (a slice named "blog" must NOT share an identifier
            // with any node, even on the master diagram).
            result.MasterFlowchart.Should().Contain("subgraph slice_blog [\"blog\"]");
            result.MasterFlowchart.Should().Contain("subgraph slice_metrics [\"metrics\"]");
            result.MasterFlowchart.Should().Contain("classDef nodeClass");
            result.MasterFlowchart.Should().Contain("%%   blog: post, user");
            result.MasterFlowchart.Should().Contain("%%   metrics: metric");
        }

        [Fact]
        public void Orphan_entities_cluster_under_unassigned_in_master()
        {
            var orphan = new SchemaEntity(
                name: "loose_node",
                attributes: new List<SchemaAttribute>(),
                annotations: new List<SchemaAnnotation>(),
                indexes: new List<SchemaIndex>(),
                sourceLine: 0);
            var skipMe = new SchemaEntity(
                name: "should_skip",
                attributes: new List<SchemaAttribute>(),
                annotations: new[] { Slice("_skip") },
                indexes: new List<SchemaIndex>(),
                sourceLine: 0);

            var model = new SchemaModel(
                sourceFile: "synthetic",
                entities: new[] { orphan, skipMe },
                relationships: Array.Empty<SchemaRelationship>());

            var result = MermaidFlowchartEmitter.Emit(new[] { model });

            result.UnassignedEntities.Should().Contain("loose_node");
            result.MasterFlowchart.Should().Contain("subgraph slice__unassigned [\"_unassigned\"]");
            result.MasterFlowchart.Should().Contain("loose_node[\"loose_node: Node\"]:::nodeClass");
            result.MasterFlowchart.Should().NotContain("should_skip");
        }

        [Fact]
        public void Edge_cardinality_glyphs_translate_to_human_readable_shorthand()
        {
            var model = BuildModel(
                slices: new[] { ("blog", new[] { "user", "post" }) },
                relationships: new[]
                {
                    ("user", "post", "||--o{", "AUTHORED"),
                });

            var result = MermaidFlowchartEmitter.Emit(new[] { model });
            result.SliceFiles["blog.flowchart.mermaid"]
                .Should().Contain("user -- \"AUTHORED [1:N]\" --> post");
        }

        // ─── helpers ───────────────────────────────────────────────────────

        private static SchemaModel BuildModel(
            params (string slice, string[] entities)[] slices)
            => BuildModel(slices, Array.Empty<(string, string, string, string)>());

        private static SchemaModel BuildModel(
            (string slice, string[] entities)[] slices,
            (string from, string to, string cardinality, string label)[] relationships)
        {
            var entities = new List<SchemaEntity>();
            foreach (var (slice, names) in slices)
            {
                foreach (var name in names)
                {
                    entities.Add(new SchemaEntity(
                        name,
                        new List<SchemaAttribute>(),
                        new[] { Slice(slice) },
                        new List<SchemaIndex>(),
                        sourceLine: 0));
                }
            }
            var rels = new List<SchemaRelationship>();
            foreach (var (from, to, cardinality, label) in relationships)
            {
                rels.Add(new SchemaRelationship(
                    from, to, cardinality, label,
                    annotations: Array.Empty<SchemaAnnotation>(),
                    sourceLine: 0));
            }
            return new SchemaModel("synthetic", entities, rels);
        }

        private static SchemaAnnotation Slice(string name) =>
            new SchemaAnnotation(
                directive: "slice",
                rawArguments: "\"" + name + "\"",
                arguments: new Dictionary<string, string>(),
                sourceLine: 0,
                sourceColumn: 0);
    }
}
