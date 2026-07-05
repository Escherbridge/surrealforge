// SPDX-License-Identifier: UNLICENSED
// SurrealForge.Schema -- intermediate-representation (IR) of a SurrealDB
// schema, neutral to its origin.
//
// History: this shape used to live at SurrealForge.Schema.Mermaid as the
// AST produced by MermaidParser; with the C#-first pivot it was renamed and
// repositioned as the IR consumed by SurqlEmitter + MermaidFlowchartEmitter.
// The only producer today is AttributeSchemaScanner; the type names + member
// shape were preserved for byte-equivalence with the legacy emit.

#nullable enable

using System.Collections.Generic;

namespace SurrealForge.Schema.Model
{
    /// <summary>
    /// Root IR: zero-or-more tables + zero-or-more cross-table relationships.
    /// </summary>
    public sealed class SchemaModel
    {
        /// <summary>
        /// Origin marker (e.g. the CLR full name of the scanned type, or
        /// <c>"(attribute-scan)"</c> for a multi-type scan).
        /// </summary>
        public string SourceFile { get; }

        /// <summary>Entities in source order.</summary>
        public IReadOnlyList<SchemaEntity> Entities { get; }

        /// <summary>Relationships in source order.</summary>
        public IReadOnlyList<SchemaRelationship> Relationships { get; }

        public SchemaModel(
            string sourceFile,
            IReadOnlyList<SchemaEntity> entities,
            IReadOnlyList<SchemaRelationship> relationships)
        {
            SourceFile = sourceFile ?? string.Empty;
            Entities = entities ?? new List<SchemaEntity>();
            Relationships = relationships ?? new List<SchemaRelationship>();
        }
    }

    /// <summary>One <c>DEFINE TABLE</c> emit unit.</summary>
    public sealed class SchemaEntity
    {
        public string Name { get; }
        public IReadOnlyList<SchemaAttribute> Attributes { get; }
        public IReadOnlyList<SchemaAnnotation> Annotations { get; }
        public IReadOnlyList<SchemaIndex> Indexes { get; }
        public int SourceLine { get; }

        public SchemaEntity(
            string name,
            IReadOnlyList<SchemaAttribute> attributes,
            IReadOnlyList<SchemaAnnotation> annotations,
            IReadOnlyList<SchemaIndex> indexes,
            int sourceLine)
        {
            Name = name;
            Attributes = attributes ?? new List<SchemaAttribute>();
            Annotations = annotations ?? new List<SchemaAnnotation>();
            Indexes = indexes ?? new List<SchemaIndex>();
            SourceLine = sourceLine;
        }
    }

    /// <summary>One <c>DEFINE FIELD</c> emit unit.</summary>
    public sealed class SchemaAttribute
    {
        public string Name { get; }
        public string Type { get; }
        public bool IsKey { get; }
        public string? Comment { get; }
        public IReadOnlyList<SchemaAnnotation> Annotations { get; }
        public int SourceLine { get; }

        public SchemaAttribute(
            string name,
            string type,
            bool isKey,
            string? comment,
            IReadOnlyList<SchemaAnnotation> annotations,
            int sourceLine)
        {
            Name = name;
            Type = type;
            IsKey = isKey;
            Comment = comment;
            Annotations = annotations ?? new List<SchemaAnnotation>();
            SourceLine = sourceLine;
        }
    }

    /// <summary>Directed cross-table relationship for the flowchart emit.</summary>
    public sealed class SchemaRelationship
    {
        public string FromEntity { get; }
        public string ToEntity { get; }
        public string Cardinality { get; }
        public string? Label { get; }
        public IReadOnlyList<SchemaAnnotation> Annotations { get; }
        public int SourceLine { get; }

        public SchemaRelationship(
            string fromEntity,
            string toEntity,
            string cardinality,
            string? label,
            IReadOnlyList<SchemaAnnotation> annotations,
            int sourceLine)
        {
            FromEntity = fromEntity;
            ToEntity = toEntity;
            Cardinality = cardinality;
            Label = label;
            Annotations = annotations ?? new List<SchemaAnnotation>();
            SourceLine = sourceLine;
        }
    }

    /// <summary>
    /// Free-form annotation key-value pair carrying emit metadata
    /// (header comments, fieldgroups, assertions, defaults, slice
    /// membership, etc.). Directives are gate-listed by the emitter; the
    /// scanner constructs them mechanically from attribute decorations.
    /// </summary>
    public sealed class SchemaAnnotation
    {
        public string Directive { get; }
        public string RawArguments { get; }
        public IReadOnlyDictionary<string, string> Arguments { get; }
        public int SourceLine { get; }
        public int SourceColumn { get; }

        public SchemaAnnotation(
            string directive,
            string rawArguments,
            IReadOnlyDictionary<string, string> arguments,
            int sourceLine,
            int sourceColumn)
        {
            Directive = directive;
            RawArguments = rawArguments ?? string.Empty;
            Arguments = arguments ?? new Dictionary<string, string>();
            SourceLine = sourceLine;
            SourceColumn = sourceColumn;
        }
    }

    /// <summary>One <c>DEFINE INDEX</c> emit unit.</summary>
    public sealed class SchemaIndex
    {
        public string Name { get; }
        public IReadOnlyList<string> Fields { get; }
        public bool IsUnique { get; }
        public int SourceLine { get; }

        public SchemaIndex(string name, IReadOnlyList<string> fields, bool isUnique, int sourceLine)
        {
            Name = name;
            Fields = fields;
            IsUnique = isUnique;
            SourceLine = sourceLine;
        }
    }
}
