// SPDX-License-Identifier: UNLICENSED
// SurrealForge.Schema -- Mermaid flowchart emitter (C#-first pivot).
//
// Sibling to AggregateEmitter. Where AggregateEmitter renders the
// erDiagram shape (entity boxes + ER cardinality glyphs), this emitter
// renders a `graph LR` flowchart with cardinality-labelled edges --
// matching the visual idiom commonly preferred for node/relationship
// portfolios:
//
//   graph LR
//       %% Define Nodes
//       wallet[wallet: Node]:::nodeClass
//       nft_ownership[nft_ownership: Node]:::nodeClass
//
//       %% Define Edges
//       wallet -- "OWNED_BY [N:1]" --> avatar
//       avatar -- "OWNS [1:N]" --> wallet
//
//       classDef nodeClass fill:#f9f9f9,stroke:#333,stroke-width:2px,rx:10px,ry:10px;
//
// Output set:
//   - One <slice>.flowchart.mermaid per slice (slice-local edges only).
//   - One domain.flowchart.mermaid master diagram with every node (including
//     orphans clustered under `_unassigned`) and every edge. Slices are
//     rendered as `subgraph` clusters so visualisation tools group them.
//
// Determinism: same input set -> byte-identical output. All collections
// re-sorted by ordinal name; no clock reads; no environment lookups.

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SurrealForge.Schema.Model;

namespace SurrealForge.Schema.Generator
{
    /// <summary>
    /// Emits per-slice <c>graph LR</c> flowchart Mermaid files plus a master
    /// flowchart that includes every node (orphans included) and every
    /// relationship, with slices rendered as <c>subgraph</c> clusters.
    /// </summary>
    public static class MermaidFlowchartEmitter
    {
        /// <summary>Special slice value that means "exclude from visualization."</summary>
        public const string SkipSliceName = "_skip";

        /// <summary>Slice name applied to entities without an explicit slice annotation.</summary>
        public const string UnassignedSliceName = "_unassigned";

        /// <summary>Result of a flowchart emit pass.</summary>
        public sealed class FlowchartResult
        {
            /// <summary>per-slice flowchart files. Key: <c>"&lt;slice&gt;.flowchart.mermaid"</c>.</summary>
            public IReadOnlyDictionary<string, string> SliceFiles { get; }

            /// <summary>The master flowchart diagram contents.</summary>
            public string MasterFlowchart { get; }

            /// <summary>Slices in deterministic order (sorted by name).</summary>
            public IReadOnlyList<string> SliceNames { get; }

            /// <summary>Entities seen without a slice annotation. Empty list = clean run.</summary>
            public IReadOnlyList<string> UnassignedEntities { get; }

            public FlowchartResult(
                IReadOnlyDictionary<string, string> sliceFiles,
                string masterFlowchart,
                IReadOnlyList<string> sliceNames,
                IReadOnlyList<string> unassignedEntities)
            {
                SliceFiles = sliceFiles;
                MasterFlowchart = masterFlowchart;
                SliceNames = sliceNames;
                UnassignedEntities = unassignedEntities;
            }
        }

        /// <summary>
        /// Group entities by slice; emit one slice flowchart per group + a
        /// master flowchart spanning every non-skipped entity (orphans
        /// bucketed under <see cref="UnassignedSliceName"/>).
        /// </summary>
        public static FlowchartResult Emit(IEnumerable<SchemaModel> sources)
        {
            if (sources == null) throw new ArgumentNullException(nameof(sources));

            var ordered = sources
                .OrderBy(m => m.SourceFile, StringComparer.Ordinal)
                .ToList();

            // Bucket entities by slice (preserve sort order across runs).
            var bySlice = new SortedDictionary<string, List<SchemaEntity>>(StringComparer.Ordinal);
            var skipped = new HashSet<string>(StringComparer.Ordinal);
            var unassigned = new List<string>();
            var entityToSlice = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var model in ordered)
            {
                foreach (var entity in model.Entities)
                {
                    var slice = ReadSliceAnnotation(entity);
                    if (string.Equals(slice, SkipSliceName, StringComparison.Ordinal))
                    {
                        skipped.Add(entity.Name);
                        continue;
                    }
                    if (slice == null)
                    {
                        unassigned.Add(entity.Name);
                        slice = UnassignedSliceName;
                    }
                    if (!bySlice.TryGetValue(slice, out var bucket))
                    {
                        bucket = new List<SchemaEntity>();
                        bySlice[slice] = bucket;
                    }
                    bucket.Add(entity);
                    entityToSlice[entity.Name] = slice;
                }
            }

            // Sort each bucket by entity name for stable slice contents.
            foreach (var kvp in bySlice)
            {
                kvp.Value.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            }

            // Collect every relationship into a stable, de-duplicated list.
            // De-dup by (from, to, cardinality, label) since the same arrow
            // may appear on both endpoints' source files.
            var relSet = new SortedSet<RelationshipKey>();
            foreach (var model in ordered)
            {
                foreach (var rel in model.Relationships)
                {
                    if (skipped.Contains(rel.FromEntity) || skipped.Contains(rel.ToEntity)) continue;
                    relSet.Add(new RelationshipKey(rel));
                }
            }
            var allRelationships = relSet.ToList();

            // Per-slice flowcharts: include every edge that ORIGINATES in
            // this slice (slice-local + cross-slice outbound). Cross-slice
            // targets are rendered as ghost nodes outside the slice's
            // subgraph so the slice diagram visualises what other aggregates
            // it depends on. Inbound cross-slice edges live only on the
            // master diagram to avoid bidirectional duplication.
            var sliceFiles = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var kvp in bySlice)
            {
                var sliceName = kvp.Key;
                var sliceEntities = kvp.Value;
                var sliceEdges = allRelationships
                    .Where(r =>
                        entityToSlice.TryGetValue(r.FromEntity, out var fs) && fs == sliceName)
                    .ToList();
                // Cross-slice target tables become ghost nodes on the
                // slice diagram (no subgraph clustering, just the box).
                var externalTargets = sliceEdges
                    .Where(r => !entityToSlice.TryGetValue(r.ToEntity, out var ts) || ts != sliceName)
                    .Select(r => r.ToEntity)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(s => s, StringComparer.Ordinal)
                    .ToList();
                sliceFiles[sliceName + ".flowchart.mermaid"] = RenderSlice(sliceName, sliceEntities, externalTargets, sliceEdges);
            }

            // Master flowchart: every entity (orphans clustered under their bucket),
            // every edge, slices wrapped in `subgraph` blocks.
            var master = RenderMaster(bySlice, allRelationships);

            return new FlowchartResult(
                sliceFiles: sliceFiles,
                masterFlowchart: master,
                sliceNames: bySlice.Keys.ToList(),
                unassignedEntities: unassigned);
        }

        /// <summary>
        /// Emit per-attribute-schema flowcharts directly from a set of
        /// <see cref="SchemaModel"/> instances produced by the
        /// <see cref="AttributeSchemaScanner"/>. Identical contract to
        /// <see cref="Emit"/>; called from the C#-first CLI path.
        /// </summary>
        public static FlowchartResult EmitFromAttributeScan(IEnumerable<SchemaModel> scanResults)
            => Emit(scanResults);

        // ── render: slice ─────────────────────────────────────────────────

        private static string RenderSlice(
            string sliceName,
            IReadOnlyList<SchemaEntity> entities,
            IReadOnlyList<string> externalTargets,
            IReadOnlyList<RelationshipKey> edges)
        {
            // Mermaid requires the `graph <direction>` header to be the FIRST
            // non-blank line of the document. Comments emitted before the
            // header break rendering on every renderer we've tried. Header
            // first, metadata comments inside the graph body.
            var sb = new StringBuilder();
            sb.Append("graph LR\n");
            sb.Append("    %% Slice: ").Append(sliceName).Append('\n');
            sb.Append("    %% GENERATED FILE -- do not edit. Source: SurrealDB schema attribute scan.\n");
            sb.Append("    %% Regenerate with: dotnet run --project packages/SurrealForge.Schema -- flowcharts\n");

            if (entities.Count > 0)
            {
                sb.Append('\n');
                sb.Append("    %% Nodes in this slice\n");
                foreach (var e in entities)
                {
                    AppendNode(sb, e.Name);
                }
            }

            if (externalTargets.Count > 0)
            {
                sb.Append('\n');
                sb.Append("    %% Cross-slice targets (rendered as ghost nodes)\n");
                foreach (var t in externalTargets)
                {
                    sb.Append("    ").Append(t).Append("[\"")
                      .Append(t).Append(": Node (external)\"]:::externalNode\n");
                }
            }

            if (edges.Count > 0)
            {
                sb.Append('\n');
                sb.Append("    %% Edges (outbound from this slice)\n");
                foreach (var edge in edges)
                {
                    AppendEdge(sb, edge);
                }
            }

            sb.Append('\n');
            AppendClassDef(sb);
            if (externalTargets.Count > 0)
            {
                sb.Append("    classDef externalNode fill:#eef5ff,stroke:#5b8def,stroke-width:1px,stroke-dasharray:4 3,rx:10px,ry:10px;\n");
            }
            return sb.ToString();
        }

        // ── render: master ────────────────────────────────────────────────

        private static string RenderMaster(
            SortedDictionary<string, List<SchemaEntity>> bySlice,
            IReadOnlyList<RelationshipKey> edges)
        {
            // `graph LR` must be the first non-blank line, then metadata
            // comments live inside the graph body. Every `%%` line carries
            // at least one space after the `%%` token; a bare `%%` line
            // renders as a node on stricter Mermaid parsers.
            var sb = new StringBuilder();
            sb.Append("graph LR\n");
            sb.Append("    %% SurrealForge -- domain data model (master flowchart)\n");
            sb.Append("    %% GENERATED FILE -- do not edit. Source: SurrealDB schema attribute scan.\n");
            sb.Append("    %% Regenerate with: dotnet run --project packages/SurrealForge.Schema -- flowcharts\n");
            sb.Append("    %% \n");
            sb.Append("    %% Slices in this diagram:\n");
            foreach (var kvp in bySlice)
            {
                sb.Append("    %%   ").Append(kvp.Key).Append(": ");
                sb.Append(string.Join(", ", kvp.Value.Select(e => e.Name)));
                sb.Append('\n');
            }

            // Enum legend: every [Inside]-decorated field surfaces as a
            // `%% <Table>.<Field>: A, B, C` line so operators can see the
            // closed sets without opening the POCO. Sorted by table name +
            // field name for stable diff output.
            var enumLines = CollectEnumLegend(bySlice);
            if (enumLines.Count > 0)
            {
                sb.Append("    %% \n");
                sb.Append("    %% Enums:\n");
                foreach (var line in enumLines)
                {
                    sb.Append("    %%   ").Append(line).Append('\n');
                }
            }

            foreach (var kvp in bySlice)
            {
                sb.Append('\n');
                sb.Append("    subgraph ").Append(SafeSubgraphId(kvp.Key))
                  .Append(" [\"").Append(kvp.Key).Append("\"]\n");
                foreach (var e in kvp.Value)
                {
                    sb.Append("    ");
                    AppendNode(sb, e.Name);
                }
                sb.Append("    end\n");
            }

            if (edges.Count > 0)
            {
                sb.Append('\n');
                sb.Append("    %% Edges (slice-local + cross-slice)\n");
                foreach (var edge in edges)
                {
                    AppendEdge(sb, edge);
                }
            }

            sb.Append('\n');
            AppendClassDef(sb);
            return sb.ToString();
        }

        // ── render: primitives ────────────────────────────────────────────

        private static void AppendNode(StringBuilder sb, string nodeName)
        {
            // Label is quoted because it contains ":" (Node) -- unquoted bracket
            // labels with embedded colons trip Mermaid parsers on stricter
            // renderers (notably GitHub's). Identifier itself stays unquoted.
            sb.Append("    ").Append(nodeName)
              .Append("[\"").Append(nodeName).Append(": Node\"]:::nodeClass\n");
        }

        private static void AppendEdge(StringBuilder sb, RelationshipKey edge)
        {
            sb.Append("    ").Append(edge.FromEntity)
              .Append(" -- \"").Append(BuildEdgeLabel(edge)).Append("\" --> ")
              .Append(edge.ToEntity).Append('\n');
        }

        private static void AppendClassDef(StringBuilder sb)
        {
            sb.Append("    classDef nodeClass fill:#f9f9f9,stroke:#333,stroke-width:2px,rx:10px,ry:10px;\n");
        }

        /// <summary>
        /// Sweep every entity's column annotations for `@surreal.enum`
        /// records and return one human-readable line per closed-set field,
        /// sorted by table + column for stable diff output. Lines are of
        /// the shape `<Table>.<Field>: A, B, C` -- the C# enum type name
        /// (when present) is appended in parens so operators can match the
        /// closed set back to the POCO's nested enum.
        /// </summary>
        private static List<string> CollectEnumLegend(
            SortedDictionary<string, List<SchemaEntity>> bySlice)
        {
            var lines = new List<(string Table, string Field, string Line)>();
            foreach (var bucket in bySlice.Values)
            {
                foreach (var entity in bucket)
                {
                    foreach (var attr in entity.Attributes)
                    {
                        foreach (var ann in attr.Annotations)
                        {
                            if (ann.Directive != "enum") continue;
                            ann.Arguments.TryGetValue("values", out var encoded);
                            ann.Arguments.TryGetValue("name", out var csEnum);
                            if (string.IsNullOrEmpty(encoded)) continue;
                            var values = string.Join(", ", DecodeEnumValues(encoded!));
                            var line = entity.Name + "." + attr.Name + ": " + values;
                            if (!string.IsNullOrEmpty(csEnum))
                            {
                                line += "  (" + csEnum + ")";
                            }
                            lines.Add((entity.Name, attr.Name, line));
                        }
                    }
                }
            }
            lines.Sort((a, b) =>
            {
                int c = string.CompareOrdinal(a.Table, b.Table);
                return c != 0 ? c : string.CompareOrdinal(a.Field, b.Field);
            });
            return lines.Select(t => t.Line).ToList();
        }

        private static IEnumerable<string> DecodeEnumValues(string encoded)
        {
            // Reverse of AttributeSchemaScanner.EncodeEnumValue: split on
            // unescaped commas, then un-escape each segment.
            var sb = new StringBuilder();
            for (int i = 0; i < encoded.Length; i++)
            {
                char c = encoded[i];
                if (c == '\\' && i + 1 < encoded.Length)
                {
                    sb.Append(encoded[i + 1]);
                    i++;
                }
                else if (c == ',')
                {
                    yield return sb.ToString();
                    sb.Clear();
                }
                else sb.Append(c);
            }
            if (sb.Length > 0) yield return sb.ToString();
        }

        private static string BuildEdgeLabel(RelationshipKey edge)
        {
            // Map Mermaid ER cardinality glyphs to "[X:Y]" labels.
            //   ||--||   1:1
            //   ||--o{   1:N
            //   }o--||   N:1
            //   }o--o{   N:M
            //   |o--||   0..1:1
            //   ||--o|   1:0..1
            //   |o--o|   0..1:0..1
            string card = CardinalityShorthand(edge.Cardinality);
            var label = string.IsNullOrEmpty(edge.Label) ? null : SanitizeLabelText(edge.Label!);
            if (string.IsNullOrEmpty(label)) return card;
            return label + " [" + card + "]";
        }

        private static string CardinalityShorthand(string mermaidCardinality)
        {
            switch (mermaidCardinality)
            {
                case "||--||": return "1:1";
                case "||--o{": return "1:N";
                case "||--|{": return "1:N";
                case "}o--||": return "N:1";
                case "}|--||": return "N:1";
                case "}o--o{": return "N:M";
                case "}|--|{": return "N:M";
                case "|o--||": return "0..1:1";
                case "||--o|": return "1:0..1";
                case "|o--o|": return "0..1:0..1";
                // Optional FK shorthand emitted by the scanner for
                // [References(Optional = true)] -- one parent on the right,
                // many possibly-null children on the left.
                case "}o--o|": return "N:0..1";
                case "|o--o{": return "0..1:N";
                default: return mermaidCardinality;
            }
        }

        private static string SanitizeLabelText(string s)
        {
            // The edge label is wrapped in "..." -- strip embedded double-quotes
            // (would break the Mermaid parser) and collapse newlines.
            return s.Replace("\"", "'").Replace('\n', ' ').Replace('\r', ' ');
        }

        private static string SafeSubgraphId(string sliceName)
        {
            // Subgraph IDs must be Mermaid identifiers (no spaces / special
            // chars). They MUST also be distinct from every node identifier
            // in the graph -- when slice name == entity name (e.g. slice
            // "quest" containing node "quest"), Mermaid silently merges them
            // and the diagram fails to render. Always prefix with "slice_"
            // to guarantee the namespace separation.
            var sb = new StringBuilder("slice_", sliceName.Length + 6);
            foreach (var c in sliceName)
            {
                sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
            }
            return sb.ToString();
        }

        private static string? ReadSliceAnnotation(SchemaEntity entity)
        {
            for (int i = 0; i < entity.Annotations.Count; i++)
            {
                var a = entity.Annotations[i];
                if (string.Equals(a.Directive, "slice", StringComparison.Ordinal))
                {
                    var raw = a.RawArguments.Trim();
                    if (raw.Length >= 2 && raw[0] == '"' && raw[raw.Length - 1] == '"')
                    {
                        return Unescape(raw.Substring(1, raw.Length - 2));
                    }
                    return raw;
                }
            }
            return null;
        }

        private static string Unescape(string s)
        {
            if (s.IndexOf('\\') < 0) return s;
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                var c = s[i];
                if (c == '\\' && i + 1 < s.Length)
                {
                    var nx = s[i + 1];
                    switch (nx)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case 'n': sb.Append('\n'); break;
                        case 't': sb.Append('\t'); break;
                        default: sb.Append(nx); break;
                    }
                    i++;
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Stable, totally-ordered key for de-duplicating relationships across
        /// source files. Sort key is (FromEntity, ToEntity, Cardinality, Label).
        /// </summary>
        private readonly struct RelationshipKey : IComparable<RelationshipKey>, IEquatable<RelationshipKey>
        {
            public string FromEntity { get; }
            public string ToEntity { get; }
            public string Cardinality { get; }
            public string? Label { get; }

            public RelationshipKey(SchemaRelationship r)
            {
                FromEntity = r.FromEntity;
                ToEntity = r.ToEntity;
                Cardinality = r.Cardinality;
                Label = r.Label;
            }

            public int CompareTo(RelationshipKey other)
            {
                int c = string.CompareOrdinal(FromEntity, other.FromEntity);
                if (c != 0) return c;
                c = string.CompareOrdinal(ToEntity, other.ToEntity);
                if (c != 0) return c;
                c = string.CompareOrdinal(Cardinality, other.Cardinality);
                if (c != 0) return c;
                return string.CompareOrdinal(Label ?? string.Empty, other.Label ?? string.Empty);
            }

            public bool Equals(RelationshipKey other) => CompareTo(other) == 0;
            public override bool Equals(object? obj) => obj is RelationshipKey k && Equals(k);
            public override int GetHashCode()
            {
                unchecked
                {
                    int h = 17;
                    h = h * 31 + (FromEntity?.GetHashCode() ?? 0);
                    h = h * 31 + (ToEntity?.GetHashCode() ?? 0);
                    h = h * 31 + (Cardinality?.GetHashCode() ?? 0);
                    h = h * 31 + (Label?.GetHashCode() ?? 0);
                    return h;
                }
            }
        }
    }
}
