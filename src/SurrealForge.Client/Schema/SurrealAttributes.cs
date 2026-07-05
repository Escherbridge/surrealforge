// SPDX-License-Identifier: UNLICENSED
// SurrealForge.Client -- C#-first schema authoring attribute surface.
//
// These attributes decorate POCOs in the consumer project. The
// SurrealForge.Schema package scans them at build/CLI time and emits
// the .surql DDL + flowchart Mermaid views + (planned) DBML manifest.
// Attribute layer carries SHAPE ONLY; executable validation lives in
// partial-class siblings (see DESIGN-mermaid-portfolio.md §"Partial class").
//
// Design contract:
//   - Attribute presence MUST be inert at runtime (no behavioural coupling).
//   - Attribute argument set MUST be expressible without closures or
//     stringly-typed method references (compile-time-checked surface).
//   - Default values mirror the legacy @surreal.* Mermaid directives so
//     emitted .surql remains byte-equivalent during the migration.

#nullable enable

using System;

namespace SurrealForge.Client.Schema
{
    /// <summary>
    /// Declares a class as the in-code definition of a SurrealDB table.
    /// One attribute = one <c>DEFINE TABLE</c> in the emitted <c>.surql</c>.
    /// </summary>
    /// <remarks>
    /// The <see cref="Name"/> is the wire-format table name (snake_case).
    /// <see cref="Schemafull"/> controls whether the emitted DDL ends in
    /// <c>SCHEMAFULL</c>; defaults to <c>true</c> by default.
    /// </remarks>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class SurrealTableAttribute : Attribute
    {
        /// <summary>The SurrealDB table name (snake_case).</summary>
        public string Name { get; }

        /// <summary>
        /// Emit <c>SCHEMAFULL</c> on the table definition. Default <c>true</c>
        /// (G6: every value table is SCHEMAFULL).
        /// </summary>
        public bool Schemafull { get; set; } = true;

        /// <summary>
        /// Aggregate name displayed in the emitted <c>.surql</c> header
        /// comment block. Free text; falls back to nothing when omitted.
        /// </summary>
        public string? Aggregate { get; set; }

        /// <summary>
        /// Guardrail tag line (e.g. <c>"G6 SCHEMAFULL"</c>). Free text;
        /// emitted as the <c>-- Guardrail:</c> header line.
        /// </summary>
        public string? Guardrail { get; set; }

        public SurrealTableAttribute(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Table name must not be empty.", nameof(name));
            Name = name;
        }
    }

    /// <summary>
    /// Emits a SurrealDB <c>CHANGEFEED &lt;duration&gt;</c> clause on the table,
    /// enabling change-data-capture for the given retention window. No EF
    /// analog; used by SurrealDB live queries / CDC consumers.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class ChangeFeedAttribute : Attribute
    {
        /// <summary>Retention duration token (e.g. <c>"3d"</c>, <c>"1h"</c>).</summary>
        public string Duration { get; }

        /// <summary>
        /// Emit the <c>INCLUDE ORIGINAL</c> modifier so the feed carries the
        /// pre-change row state alongside the new one. Default <c>false</c>.
        /// </summary>
        public bool IncludeOriginal { get; set; }

        public ChangeFeedAttribute(string duration)
        {
            if (string.IsNullOrWhiteSpace(duration))
                throw new ArgumentException("Changefeed duration must not be empty.", nameof(duration));
            Duration = duration;
        }
    }

    /// <summary>
    /// Emits a SurrealDB <c>PERMISSIONS</c> clause on the table, the engine's
    /// row-level security gate (the analog of EF's <c>HasQueryFilter</c>, but
    /// enforced for writes too). Each property is a raw SurrealQL boolean
    /// expression (e.g. <c>"$auth.id = id"</c>); the special tokens
    /// <c>"FULL"</c> and <c>"NONE"</c> are passed through unquoted. A null
    /// operation clause is omitted, leaving that operation at the SurrealDB
    /// default (NONE). Set <see cref="Full"/> to emit the table-wide
    /// <c>PERMISSIONS FULL</c> shorthand instead of per-operation clauses.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class PermissionsAttribute : Attribute
    {
        /// <summary>Emit <c>PERMISSIONS FULL</c>; ignores the per-operation clauses.</summary>
        public bool Full { get; set; }

        /// <summary>Expression for <c>FOR select</c>. Null omits the clause.</summary>
        public string? Select { get; set; }
        /// <summary>Expression for <c>FOR create</c>. Null omits the clause.</summary>
        public string? Create { get; set; }
        /// <summary>Expression for <c>FOR update</c>. Null omits the clause.</summary>
        public string? Update { get; set; }
        /// <summary>Expression for <c>FOR delete</c>. Null omits the clause.</summary>
        public string? Delete { get; set; }
    }

    /// <summary>
    /// Long-form note attached to a table. Emitted as one <c>-- Note:</c>
    /// header line per occurrence; multi-line strings split on <c>\n</c>.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
    public sealed class SurrealNoteAttribute : Attribute
    {
        public string Text { get; }
        public SurrealNoteAttribute(string text) { Text = text ?? string.Empty; }
    }

    /// <summary>
    /// Slice membership for the aggregate diagram emitter. Each slice maps
    /// to one generated <c>&lt;slice&gt;.flowchart.mermaid</c> file; the
    /// master flowchart unions every slice (including orphans tagged
    /// <c>"_unassigned"</c>).
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class SliceAttribute : Attribute
    {
        public string Name { get; }
        public SliceAttribute(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Slice name must not be empty.", nameof(name));
            Name = name;
        }
    }

    /// <summary>
    /// Marks a property as a persisted SurrealDB column. Properties without
    /// this attribute are ignored by the schema emitter (treated as in-memory
    /// transients).
    /// </summary>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
    public sealed class ColumnAttribute : Attribute
    {
        /// <summary>
        /// SurrealDB column name. Defaults to <c>null</c>, which causes the
        /// emitter to derive snake_case from the CLR property name.
        /// </summary>
        public string? Name { get; set; }

        /// <summary>
        /// Explicit SurrealDB type token (<c>"string"</c>, <c>"option&lt;string&gt;"</c>,
        /// <c>"record&lt;wallet&gt;"</c>, <c>"array&lt;string&gt;"</c>, ...). If
        /// omitted, the emitter infers from the CLR property type via the
        /// inverse of <c>CSharpTypeMapper.Map</c>.
        /// </summary>
        public string? Type { get; set; }

        /// <summary>
        /// Source-order rank within the table's emitted column list. The
        /// reflection API does not guarantee a stable order for declared
        /// properties across runtimes/builds, so authors set <c>Order</c>
        /// explicitly to drive the emit order. Lower numbers emit first;
        /// ties broken by metadata token then by property name.
        /// </summary>
        public int Order { get; set; }

        /// <summary>
        /// Emit the column with the SurrealDB <c>FLEXIBLE</c> modifier, which
        /// disables the engine's structural validation of the inner shape.
        /// Used for embedded JSON blobs whose contract is enforced by the
        /// C# POCO at deserialization time rather than by the DB. Default
        /// <c>false</c>.
        /// </summary>
        public bool Flexible { get; set; }
    }

    /// <summary>
    /// Marks a column as the SurrealDB row identifier. Equivalent to the
    /// Mermaid <c>id</c> field; no per-table <c>id</c> column is emitted
    /// when the property is not <see cref="IdAttribute"/>-marked but is
    /// named <c>Id</c> -- explicit beats implicit, so the attribute is
    /// required.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
    public sealed class IdAttribute : Attribute
    {
    }

    /// <summary>
    /// Marks a column as nullable (<c>option&lt;T&gt;</c> in SurrealDB).
    /// When the CLR property is already a nullable reference / value type,
    /// the emitter infers <c>option&lt;...&gt;</c> automatically; this
    /// attribute makes the intent explicit and forces the wrap regardless
    /// of the CLR shape.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
    public sealed class OptionalAttribute : Attribute
    {
    }

    /// <summary>
    /// Forces a column to be NOT NULL (bare <c>T</c>, never <c>option&lt;T&gt;</c>),
    /// overriding the emitter's CLR nullability inference. The mirror of
    /// <see cref="OptionalAttribute"/> and the equivalent of EF's
    /// <c>[Required]</c>: apply it to a <c>Nullable&lt;T&gt;</c> property
    /// (e.g. <c>int?</c>, <c>DateTime?</c>) that should still persist as a
    /// required SurrealDB column. Mutually exclusive with <c>[Optional]</c>;
    /// the scanner throws if both are present on the same property.
    /// <para>
    /// Set <see cref="NotEmpty"/> to also emit the
    /// <c>ASSERT $value != NONE AND $value != ""</c> guard — the sleek
    /// replacement for hand-writing that raw <c>[Assert(...)]</c> on every
    /// required string column. On a <c>record&lt;&gt;</c>-typed (FK) column the
    /// assert is omitted (a record id is never the empty string and SurrealDB
    /// type-checks it), matching the legacy hand-written behavior byte-for-byte.
    /// </para>
    /// </summary>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
    public sealed class RequiredAttribute : Attribute
    {
        /// <summary>
        /// Emit <c>ASSERT $value != NONE AND $value != ""</c> in addition to
        /// forcing NOT NULL. Default <c>false</c>.
        /// </summary>
        public bool NotEmpty { get; set; }
    }

    /// <summary>
    /// Emits the SurrealDB <c>READONLY</c> modifier on the column: the field
    /// can be set at create time but never updated afterwards. The equivalent
    /// of EF's <c>[Editable(false)]</c>; use for set-once columns such as
    /// <c>created_at</c> or an externally-assigned correlation id.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
    public sealed class ReadOnlyAttribute : Attribute
    {
    }

    /// <summary>
    /// Excludes a public property from the SurrealDB schema. The scanner
    /// auto-includes every public read/write instance property as a column, so
    /// this is the opt-out for computed/helper/navigation properties that must
    /// not become persisted columns. The EF equivalent of <c>[NotMapped]</c>.
    /// (Get-only properties such as the <c>SchemaName</c> interface member are
    /// already excluded without this attribute.)
    /// </summary>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
    public sealed class NotMappedAttribute : Attribute
    {
    }

    /// <summary>
    /// Emits a SurrealDB <c>VALUE</c> clause verbatim on the column: a
    /// server-side computed expression re-evaluated on every write (e.g.
    /// <c>time::now()</c> or <c>string::lowercase($value)</c>). Distinct from
    /// <see cref="DefaultAttribute"/>, which only applies when the field is
    /// absent on insert. The expression is passed through unquoted.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
    public sealed class ValueAttribute : Attribute
    {
        /// <summary>The raw SurrealQL VALUE expression (no leading <c>VALUE</c>).</summary>
        public string Expression { get; }
        public ValueAttribute(string expression) { Expression = expression ?? string.Empty; }
    }

    /// <summary>
    /// Emits a SurrealDB <c>COMMENT</c> clause on the column. The equivalent
    /// of EF Core's <c>[Comment]</c>; free text surfaced in the schema for
    /// operators. The value is emitted as a quoted string literal.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
    public sealed class CommentAttribute : Attribute
    {
        public string Text { get; }
        public CommentAttribute(string text) { Text = text ?? string.Empty; }
    }

    /// <summary>
    /// Emits a SurrealDB <c>ASSERT</c> clause verbatim on the column.
    /// Multiple <c>[Assert]</c> attributes are AND'd in source order.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = true, Inherited = false)]
    public sealed class AssertAttribute : Attribute
    {
        /// <summary>The raw SurrealQL ASSERT expression (no leading <c>ASSERT</c>).</summary>
        public string Expression { get; }
        public AssertAttribute(string expression) { Expression = expression ?? string.Empty; }
    }

    /// <summary>
    /// Emits a SurrealDB <c>DEFAULT</c> clause verbatim on the column.
    /// The value is passed through unquoted; string defaults must include
    /// their own quoting (<c>"\"Pending\""</c>).
    /// </summary>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
    public sealed class DefaultAttribute : Attribute
    {
        public string Value { get; }
        public DefaultAttribute(string value) { Value = value ?? string.Empty; }
    }

    /// <summary>
    /// Declares the column as the SurrealDB <c>"$value INSIDE [...]"</c>
    /// closed-set string field. The source-gen path will emit a sibling
    /// enum + the SurrealDB ASSERT. In the attribute-driven path the
    /// values are joined into an <see cref="AssertAttribute"/>-equivalent
    /// emit at .surql generation time.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
    public sealed class InsideAttribute : Attribute
    {
        public string[] Values { get; }
        public InsideAttribute(params string[] values)
        {
            Values = values ?? Array.Empty<string>();
        }
    }

    /// <summary>
    /// Declares the column as a foreign reference to another table. The
    /// emitter maps the type parameter to <c>record&lt;target_table&gt;</c>
    /// (Phase C contract). <see cref="Optional"/> wraps in <c>option&lt;...&gt;</c>
    /// when the FK is nullable on the source side.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
    public sealed class ReferencesAttribute : Attribute
    {
        public Type Target { get; }

        /// <summary>
        /// Wrap the emitted type in <c>option&lt;...&gt;</c>. Use for
        /// nullable FK columns. Default <c>false</c>.
        /// </summary>
        public bool Optional { get; set; }

        /// <summary>
        /// Escape hatch: emit the column as <c>string</c> instead of
        /// <c>record&lt;target&gt;</c>. Used by legacy adapters that query
        /// the FK as a raw id string and have not been migrated to the
        /// record-typed traversal path. Diagram still renders the edge.
        /// Default <c>false</c>.
        /// </summary>
        public bool EmitAsString { get; set; }

        public ReferencesAttribute(Type target)
        {
            Target = target ?? throw new ArgumentNullException(nameof(target));
        }
    }

    /// <summary>
    /// Property-side: shorthand for a single-column index named after the
    /// property. Class-side: declares a multi-column index.
    /// Multiple attributes on the same target are emitted in source order.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
    public sealed class IndexAttribute : Attribute
    {
        /// <summary>Index name (snake_case). Required.</summary>
        public string Name { get; }

        /// <summary>
        /// Field list. When the attribute is on a class, this names the
        /// SurrealDB column(s) to index. When on a property, defaults to
        /// just that property's column.
        /// </summary>
        public string[]? Fields { get; set; }

        /// <summary>Emit <c>UNIQUE</c> clause.</summary>
        public bool Unique { get; set; }

        public IndexAttribute(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Index name must not be empty.", nameof(name));
            Name = name;
        }
    }

    /// <summary>
    /// Emits the column as an HNSW vector index target. Pairs with a
    /// <see cref="ColumnAttribute"/> whose <see cref="ColumnAttribute.Type"/>
    /// is <c>"array&lt;float&gt;"</c> or similar.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
    public sealed class HnswIndexAttribute : Attribute
    {
        /// <summary>Index name (e.g. <c>hnsw_quest_embedding</c>).</summary>
        public string Name { get; }

        /// <summary>Vector dimension. SurrealDB requires this at index-define time.</summary>
        public int Dimension { get; set; }

        /// <summary>Distance metric ("COSINE" / "EUCLIDEAN" / "MANHATTAN").</summary>
        public string Distance { get; set; } = "COSINE";

        public HnswIndexAttribute(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("HNSW index name must not be empty.", nameof(name));
            Name = name;
        }
    }

    /// <summary>
    /// Field-group separator comment. The emitter prints <c>-- &lt;Text&gt;</c>
    /// on a line of its own immediately before this column. Used to break
    /// up logical groupings within a long table. Mirrors the legacy
    /// <c>@surreal.fieldgroup</c> Mermaid directive.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
    public sealed class FieldGroupAttribute : Attribute
    {
        public string Text { get; }
        public FieldGroupAttribute(string text) { Text = text ?? string.Empty; }
    }

    /// <summary>
    /// Declares a SurrealDB column on the parent table that has NO C#
    /// property representation. Useful for fields the application writes
    /// via raw SurrealQL (e.g. HNSW vector embeddings) but which the
    /// POCO should not surface. Multiple attributes stack in source
    /// order; <see cref="Order"/> drives the emit position within the
    /// table's field list, exactly like <see cref="ColumnAttribute.Order"/>.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
    public sealed class ExtraSurrealFieldAttribute : Attribute
    {
        public string Name { get; }
        public string Type { get; }
        public int Order { get; set; }
        public string? Assert { get; set; }
        public string? Default { get; set; }
        public string? FieldGroup { get; set; }

        /// <summary>
        /// Emit the column with the SurrealDB <c>FLEXIBLE</c> modifier. See
        /// <see cref="ColumnAttribute.Flexible"/>. Default <c>false</c>.
        /// </summary>
        public bool Flexible { get; set; }

        public ExtraSurrealFieldAttribute(string name, string type)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("name must not be empty", nameof(name));
            if (string.IsNullOrWhiteSpace(type))
                throw new ArgumentException("type must not be empty", nameof(type));
            Name = name;
            Type = type;
        }
    }

    /// <summary>
    /// Marks an entity as a SurrealDB index-only pseudo-table. The flowchart
    /// emitter clusters these under the literal slice <c>"_skip"</c> by
    /// default; the <c>.surql</c> emit produces table + field DDL but the
    /// rows are never persisted -- the file exists purely so the
    /// <c>DEFINE INDEX</c> declarations attached to it have a deterministic
    /// migration ordering position.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class VirtualTableAttribute : Attribute
    {
    }

    /// <summary>
    /// Marks an entity as a SurrealDB RELATE-edge table. Edge tables have
    /// exactly two columns (<c>in</c> and <c>out</c>) typed as records of
    /// the connected tables. The flowchart emitter renders them as edges
    /// (not nodes) on the diagram.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class RelateEdgeAttribute : Attribute
    {
        /// <summary>The table the <c>in</c> end of the edge points at.</summary>
        public Type From { get; }
        /// <summary>The table the <c>out</c> end of the edge points at.</summary>
        public Type To { get; }

        public RelateEdgeAttribute(Type from, Type to)
        {
            From = from ?? throw new ArgumentNullException(nameof(from));
            To = to ?? throw new ArgumentNullException(nameof(to));
        }
    }

    /// <summary>
    /// Class-level relationship marker for the flowchart emitter. Declares
    /// a directed edge between this table and another with a cardinality
    /// label. The emit is informational only; the SurrealDB DDL is
    /// unaffected.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
    public sealed class RelationAttribute : Attribute
    {
        public Type From { get; }
        public Type To { get; }
        public SurrealCardinality Cardinality { get; }
        public string? Label { get; set; }

        public RelationAttribute(Type from, Type to, SurrealCardinality cardinality)
        {
            From = from ?? throw new ArgumentNullException(nameof(from));
            To = to ?? throw new ArgumentNullException(nameof(to));
            Cardinality = cardinality;
        }
    }

    /// <summary>
    /// Cardinality marker for a relationship displayed on the slice flowchart.
    /// Authoritative for diagram edge labels; never affects emitted SurrealQL.
    /// Mirrors the Mermaid ER cardinality shorthand but is rendered as the
    /// <c>"AUTHORED [1:N]"</c> edge-label style the slice flowchart uses.
    /// </summary>
    public enum SurrealCardinality
    {
        /// <summary>One-to-one (the property holds exactly one record).</summary>
        OneToOne,
        /// <summary>Zero-or-one (the property holds zero or one record).</summary>
        ZeroOrOne,
        /// <summary>One-to-many (the property holds an unbounded non-empty list).</summary>
        OneToMany,
        /// <summary>Zero-to-many (the property holds an unbounded possibly-empty list).</summary>
        ZeroToMany,
    }
}
