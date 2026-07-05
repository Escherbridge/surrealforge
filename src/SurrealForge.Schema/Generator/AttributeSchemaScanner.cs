// SPDX-License-Identifier: UNLICENSED
// SurrealForge.Schema -- C#-first attribute scanner.
//
// Reflects over a set of POCO types decorated with the
// SurrealForge.Client.Schema.* attributes and projects them onto the
// existing SchemaModel shape so the canonical SurqlEmitter can
// continue to be the single byte-stable .surql writer.
//
// Why project onto SchemaModel rather than introduce a parallel
// emit path:
//   1. The .surql output format is the source-of-truth contract; the
//      Mermaid-driven SurqlEmitter is the byte-stable reference. By
//      flattening attributes into a model the emitter already consumes,
//      the byte-equivalence test reduces to "do the two models project
//      to the same AST?" rather than "do two independent emitters
//      produce identical strings?".
//   2. Keeps the legacy Mermaid path intact during the prototype slice
//      migration -- if attribute output diverges, the Mermaid pipeline
//      remains the rollback path until the migration completes.
//
// Determinism: the scanner sorts entities and columns by the source-order
// markers exposed via the attribute layer (SurrealTableAttribute defines
// the entity's order via class metadata; ColumnAttribute.Order drives the
// column order). All collections enumerated by reflection are explicitly
// re-sorted -- never trust GetProperties()/GetCustomAttributes() ordering.

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using SurrealForge.Client.Schema;
using SurrealForge.Schema.Model;

namespace SurrealForge.Schema.Generator
{
    /// <summary>
    /// Reflects over a set of attribute-decorated POCO types and produces
    /// a <see cref="SchemaModel"/> ready for <see cref="SurqlEmitter"/>.
    /// </summary>
    public static class AttributeSchemaScanner
    {
        /// <summary>
        /// Scan one POCO type. The returned <see cref="SchemaEntity"/>
        /// projects all attribute-driven metadata onto the
        /// <see cref="SchemaAnnotation"/> shape the existing emitter
        /// consumes; <see cref="SchemaModel.SourceFile"/> on the
        /// wrapper is the CLR type's full name.
        /// </summary>
        public static SchemaModel ScanType(Type pocoType)
        {
            if (pocoType == null) throw new ArgumentNullException(nameof(pocoType));
            var table = pocoType.GetCustomAttribute<SurrealTableAttribute>(inherit: false)
                ?? throw new InvalidOperationException(
                    $"Type '{pocoType.FullName}' has no [SurrealTable] attribute -- " +
                    "AttributeSchemaScanner expects every input to declare its table.");

            var (entity, relationships) = BuildEntity(pocoType, table);
            return new SchemaModel(
                sourceFile: pocoType.FullName ?? pocoType.Name,
                entities: new[] { entity },
                relationships: relationships.ToList());
        }

        /// <summary>
        /// Scan multiple types into a single model. Entities are emitted
        /// in source-rank order: <see cref="SurrealTableAttribute"/>-defined
        /// types of the same slice cluster together, then sorted by table name
        /// for byte-stable output across builds.
        /// </summary>
        public static SchemaModel ScanTypes(IEnumerable<Type> pocoTypes)
        {
            if (pocoTypes == null) throw new ArgumentNullException(nameof(pocoTypes));
            var entities = new List<(SchemaEntity entity, IList<SchemaRelationship> rels, string sliceKey, string nameKey)>();
            foreach (var t in pocoTypes)
            {
                var table = t.GetCustomAttribute<SurrealTableAttribute>(inherit: false);
                if (table == null) continue;
                var slice = t.GetCustomAttribute<SliceAttribute>(inherit: false)?.Name
                    ?? "_unassigned";
                var (entity, rels) = BuildEntity(t, table);
                entities.Add((entity, rels, slice, table.Name));
            }
            entities.Sort((a, b) =>
            {
                int s = string.CompareOrdinal(a.sliceKey, b.sliceKey);
                return s != 0 ? s : string.CompareOrdinal(a.nameKey, b.nameKey);
            });
            var allRelationships = entities.SelectMany(e => e.rels).ToList();
            return new SchemaModel(
                sourceFile: "(attribute-scan)",
                entities: entities.Select(e => e.entity).ToList(),
                relationships: allRelationships);
        }

        // ─── Entity build ─────────────────────────────────────────────────

        private static (SchemaEntity entity, IList<SchemaRelationship> relationships) BuildEntity(
            Type pocoType, SurrealTableAttribute table)
        {
            var annotations = new List<SchemaAnnotation>();
            var relationships = new List<SchemaRelationship>();
            var sliceName = pocoType.GetCustomAttribute<SliceAttribute>(inherit: false)?.Name;

            // schemafull (default true -- mirrors @surreal.schemafull header).
            if (table.Schemafull)
            {
                annotations.Add(SimpleAnnotation("schemafull", string.Empty));
            }

            // [RelateEdge(typeof(From), typeof(To))] -> emit
            //   DEFINE TABLE x TYPE RELATION FROM <from> TO <to>
            // and skip the in/out scalar fields. The emitter recognises the
            // `relation` directive carrying `from=... to=...`.
            var relateEdge = pocoType.GetCustomAttribute<RelateEdgeAttribute>(inherit: false);
            if (relateEdge != null)
            {
                var fromTable = relateEdge.From.GetCustomAttribute<SurrealTableAttribute>(inherit: false)?.Name
                    ?? throw new InvalidOperationException(
                        $"[RelateEdge] on '{pocoType.FullName}': From type '{relateEdge.From.Name}' has no [SurrealTable].");
                var toTable = relateEdge.To.GetCustomAttribute<SurrealTableAttribute>(inherit: false)?.Name
                    ?? throw new InvalidOperationException(
                        $"[RelateEdge] on '{pocoType.FullName}': To type '{relateEdge.To.Name}' has no [SurrealTable].");
                annotations.Add(new SchemaAnnotation(
                    directive: "relation",
                    rawArguments: "from=" + fromTable + " to=" + toTable,
                    arguments: new Dictionary<string, string> { ["from"] = fromTable, ["to"] = toTable },
                    sourceLine: 0,
                    sourceColumn: 0));
                // RELATE edge participates in the flowchart as a labelled edge
                // from <from> -> <to>, with the edge table's name as the label.
                relationships.Add(new SchemaRelationship(
                    fromEntity: fromTable,
                    toEntity: toTable,
                    cardinality: "}o--o{",
                    label: table.Name,
                    annotations: Array.Empty<SchemaAnnotation>(),
                    sourceLine: 0));
            }

            if (!string.IsNullOrWhiteSpace(table.Aggregate))
            {
                annotations.Add(QuotedAnnotation("aggregate", table.Aggregate!));
            }
            if (!string.IsNullOrWhiteSpace(table.Guardrail))
            {
                annotations.Add(QuotedAnnotation("guardrail", table.Guardrail!));
            }
            foreach (var note in pocoType.GetCustomAttributes<SurrealNoteAttribute>(inherit: false))
            {
                annotations.Add(QuotedAnnotation("note", note.Text));
            }
            if (sliceName != null)
            {
                annotations.Add(QuotedAnnotation("slice", sliceName));
            }

            // [ChangeFeed("3d")] -> @surreal.changefeed annotation carrying the
            // duration token + (optional) INCLUDE ORIGINAL flag.
            var changeFeed = pocoType.GetCustomAttribute<ChangeFeedAttribute>(inherit: false);
            if (changeFeed != null)
            {
                annotations.Add(new SchemaAnnotation(
                    directive: "changefeed",
                    rawArguments: "duration=" + changeFeed.Duration
                        + (changeFeed.IncludeOriginal ? " original=true" : string.Empty),
                    arguments: new Dictionary<string, string>
                    {
                        ["duration"] = changeFeed.Duration,
                        ["original"] = changeFeed.IncludeOriginal ? "true" : "false",
                    },
                    sourceLine: 0,
                    sourceColumn: 0));
            }

            // [Permissions(...)] -> @surreal.permissions annotation carrying the
            // FULL shorthand or the per-operation clause expressions.
            var perms = pocoType.GetCustomAttribute<PermissionsAttribute>(inherit: false);
            if (perms != null)
            {
                var args = new Dictionary<string, string>();
                if (perms.Full) args["full"] = "true";
                if (perms.Select != null) args["select"] = perms.Select;
                if (perms.Create != null) args["create"] = perms.Create;
                if (perms.Update != null) args["update"] = perms.Update;
                if (perms.Delete != null) args["delete"] = perms.Delete;
                if (args.Count > 0)
                {
                    annotations.Add(new SchemaAnnotation(
                        directive: "permissions",
                        rawArguments: string.Join(" ", args.Select(kv => kv.Key + "=" + kv.Value)),
                        arguments: args,
                        sourceLine: 0,
                        sourceColumn: 0));
                }
            }

            // [Relation(typeof(From), typeof(To), Cardinality, Label)] -> flowchart-only
            // edge declarations on the class. Doesn't affect .surql.
            foreach (var rel in pocoType.GetCustomAttributes<RelationAttribute>(inherit: false))
            {
                var fromTable = rel.From.GetCustomAttribute<SurrealTableAttribute>(inherit: false)?.Name
                    ?? throw new InvalidOperationException(
                        $"[Relation] on '{pocoType.FullName}': From type '{rel.From.Name}' has no [SurrealTable].");
                var toTable = rel.To.GetCustomAttribute<SurrealTableAttribute>(inherit: false)?.Name
                    ?? throw new InvalidOperationException(
                        $"[Relation] on '{pocoType.FullName}': To type '{rel.To.Name}' has no [SurrealTable].");
                relationships.Add(new SchemaRelationship(
                    fromEntity: fromTable,
                    toEntity: toTable,
                    cardinality: CardinalityToErGlyph(rel.Cardinality),
                    label: rel.Label,
                    annotations: Array.Empty<SchemaAnnotation>(),
                    sourceLine: 0));
            }

            // Columns: AUTO-INCLUDE every public read/write instance property
            // as a column. [Column] is now an OPTIONAL override (order, explicit
            // type, name, flexible) — not the gate for inclusion. Excluded:
            //   * get-only properties (no setter) — e.g. the SchemaName
            //     interface member and any computed/expression-bodied property;
            //   * [NotMapped] properties — the explicit opt-out for helper /
            //     navigation properties that must not persist.
            // [Column(Order)] still drives ordering when present; otherwise
            // declaration order (MetadataToken) is the source-order key.
            var props = pocoType
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead && p.CanWrite
                            && p.GetCustomAttribute<NotMappedAttribute>(inherit: false) == null)
                .Select(p => new { Prop = p, Column = p.GetCustomAttribute<ColumnAttribute>(inherit: false) })
                .ToList();

            // Source-order key: explicit Order > MetadataToken > Name.
            props.Sort((a, b) =>
            {
                int ao = a.Column?.Order ?? 0;
                int bo = b.Column?.Order ?? 0;
                if (ao != bo) return ao.CompareTo(bo);
                if (a.Prop.MetadataToken != b.Prop.MetadataToken)
                    return a.Prop.MetadataToken.CompareTo(b.Prop.MetadataToken);
                return string.CompareOrdinal(a.Prop.Name, b.Prop.Name);
            });

            // Merge real CLR-backed properties + virtual [ExtraSurrealField]
            // declarations into one ordered field list. Source position is
            // the Column.Order or ExtraSurrealField.Order field.
            var fieldOrder = new List<(int order, SchemaAttribute attr, IList<SchemaIndex> propIndexes)>();

            var classLevelIndexes = new List<SchemaIndex>();

            // When the table is a RELATE edge, SurrealDB synthesises the
            // `in` and `out` record fields from the `TYPE RELATION FROM/TO`
            // clause -- any hand-declared `in`/`out` columns would clash with
            // those, so we skip them. The author's [Column(Name="in"/"out")]
            // declarations on the POCO stay for runtime JSON round-trip, but
            // do not contribute to the emitted DDL.
            bool isRelateTable = relateEdge != null;

            foreach (var entry in props)
            {
                var colName = ResolveColumnName(entry.Prop, entry.Column);
                if (isRelateTable && (colName == "in" || colName == "out"))
                {
                    continue;
                }
                var ord = entry.Column?.Order ?? 0;
                var propIndexes = new List<SchemaIndex>();
                fieldOrder.Add((ord, BuildAttribute(entry.Prop, entry.Column, table.Name), propIndexes));

                // [References<T>] on a property creates a flowchart edge
                // FromTable(this) -> ToTable(target) with the column name
                // as the label. Cardinality is N:1 by default (many rows of
                // this table can point at one row of the target).
                var refs = entry.Prop.GetCustomAttribute<ReferencesAttribute>(inherit: false);
                if (refs != null)
                {
                    var targetTable = refs.Target.GetCustomAttribute<SurrealTableAttribute>(inherit: false)?.Name;
                    if (targetTable != null)
                    {
                        relationships.Add(new SchemaRelationship(
                            fromEntity: table.Name,
                            toEntity: targetTable,
                            cardinality: refs.Optional ? "}o--o|" : "}o--||",
                            label: colName,
                            annotations: Array.Empty<SchemaAnnotation>(),
                            sourceLine: 0));
                    }
                }

                // Property-level [Index] => single-column index on this column.
                foreach (var idx in entry.Prop.GetCustomAttributes<IndexAttribute>(inherit: false))
                {
                    var fields = (idx.Fields != null && idx.Fields.Length > 0)
                        ? (IReadOnlyList<string>)idx.Fields
                        : new[] { colName };
                    classLevelIndexes.Add(new SchemaIndex(idx.Name, fields, idx.Unique, sourceLine: 0));
                }
                // HNSW property-level index.
                var hnsw = entry.Prop.GetCustomAttribute<HnswIndexAttribute>(inherit: false);
                if (hnsw != null)
                {
                    classLevelIndexes.Add(new SchemaIndex(
                        hnsw.Name,
                        new[] { colName },
                        isUnique: false,
                        sourceLine: 0));
                }
            }

            // Virtual fields declared at the class level (no CLR backing).
            foreach (var extra in pocoType.GetCustomAttributes<ExtraSurrealFieldAttribute>(inherit: false))
            {
                fieldOrder.Add((extra.Order, BuildExtraField(extra), new List<SchemaIndex>()));
            }

            // Class-level [Index(...)] indexes.
            foreach (var idx in pocoType.GetCustomAttributes<IndexAttribute>(inherit: false))
            {
                if (idx.Fields == null || idx.Fields.Length == 0)
                {
                    throw new InvalidOperationException(
                        $"Class-level [Index(\"{idx.Name}\")] on '{pocoType.FullName}' must specify Fields=...");
                }
                classLevelIndexes.Add(new SchemaIndex(idx.Name, idx.Fields!, idx.Unique, sourceLine: 0));
            }

            // Sort the merged field list by Order, falling back to insertion order.
            var orderedFields = fieldOrder
                .Select((f, i) => (f.order, i, f.attr, f.propIndexes))
                .OrderBy(t => t.order)
                .ThenBy(t => t.i)
                .Select(t => t.attr)
                .ToList();

            var entity = new SchemaEntity(
                name: table.Name,
                attributes: orderedFields,
                annotations: annotations,
                indexes: classLevelIndexes,
                sourceLine: 0);
            return (entity, relationships);
        }

        private static string CardinalityToErGlyph(SurrealCardinality c)
        {
            // Maps the SurrealCardinality enum to the Mermaid ER glyph the
            // flowchart emitter recognises in CardinalityShorthand().
            switch (c)
            {
                case SurrealCardinality.OneToOne:   return "||--||";
                case SurrealCardinality.ZeroOrOne:  return "|o--||";
                case SurrealCardinality.OneToMany:  return "||--o{";
                case SurrealCardinality.ZeroToMany: return "}o--o{";
                default: return "||--||";
            }
        }

        // ─── Attribute build ──────────────────────────────────────────────

        private static SchemaAttribute BuildAttribute(
            PropertyInfo prop, ColumnAttribute? column, string tableName)
        {
            var columnName = ResolveColumnName(prop, column);
            var typeToken = ResolveTypeToken(prop, column);
            var annotations = new List<SchemaAnnotation>();
            bool isReference = prop.GetCustomAttribute<ReferencesAttribute>(inherit: false) != null;
            bool isRecordTyped = typeToken.StartsWith("record<", StringComparison.Ordinal)
                || typeToken.StartsWith("option<record<", StringComparison.Ordinal);

            // FLEXIBLE modifier flows from [Column(Flexible=true)] into a
            // @surreal.flexible annotation the emitter checks for.
            if (column?.Flexible == true)
            {
                annotations.Add(SimpleAnnotation("flexible", string.Empty));
            }

            // READONLY modifier ([ReadOnly]) -> @surreal.readonly flag.
            if (prop.GetCustomAttribute<ReadOnlyAttribute>(inherit: false) != null)
            {
                annotations.Add(SimpleAnnotation("readonly", string.Empty));
            }

            var fieldGroup = prop.GetCustomAttribute<FieldGroupAttribute>(inherit: false);
            if (fieldGroup != null)
            {
                annotations.Add(QuotedAnnotation("fieldgroup", fieldGroup.Text));
            }

            // ASSERT -- silently drop the legacy "$value != NONE AND != ''"
            // pattern on record<>-typed columns: a record ID cannot be the
            // empty string, and SurrealDB rejects non-record values at the
            // type level. Other asserts on FK columns flow through.
            foreach (var assert in prop.GetCustomAttributes<AssertAttribute>(inherit: false))
            {
                if (isRecordTyped && IsLegacyNonEmptyStringAssert(assert.Expression))
                {
                    continue;
                }
                annotations.Add(QuotedAnnotation("assert", assert.Expression));
            }
            // [Required(NotEmpty=true)] -> the sleek replacement for the raw
            // `$value != NONE AND $value != ""` assert. Emitted at the SAME
            // position as a hand-written [Assert] so the output is byte-identical
            // to the legacy form, and dropped on record<>-typed (FK) columns for
            // the same reason the legacy assert is (see above).
            var requiredAttr = prop.GetCustomAttribute<RequiredAttribute>(inherit: false);
            if (requiredAttr?.NotEmpty == true && !isRecordTyped)
            {
                annotations.Add(QuotedAnnotation("assert", "$value != NONE AND $value != \"\""));
            }
            // [Inside(...)] -> emit a DEFINE PARAM block + ASSERT $value INSIDE $<table>_<col>.
            // When the property type is also a CLR enum, capture the enum
            // identity on the field-level annotation so the docs/legend can
            // surface it under the type's name.
            var inside = prop.GetCustomAttribute<InsideAttribute>(inherit: false);
            if (inside != null && inside.Values.Length > 0)
            {
                var paramName = tableName + "_" + columnName;
                var enumName = ResolveEnumName(prop);
                // Field-level "enum" annotation carries the param name + the
                // literal values + (optional) C# enum FullName. The emitter
                // reads this to (a) emit DEFINE PARAM $<paramName> VALUE [...]
                // at the top of the file, and (b) wire the ASSERT to use the
                // param ref rather than the inline list.
                var encodedValues = string.Join(",", inside.Values.Select(EncodeEnumValue));
                var rawArgs = "param=" + paramName + " values=" + encodedValues;
                if (enumName != null) rawArgs += " name=" + enumName;
                annotations.Add(new SchemaAnnotation(
                    directive: "enum",
                    rawArguments: rawArgs,
                    arguments: new Dictionary<string, string>
                    {
                        ["param"] = paramName,
                        ["values"] = encodedValues,
                        ["name"] = enumName ?? string.Empty,
                    },
                    sourceLine: 0,
                    sourceColumn: 0));
                annotations.Add(QuotedAnnotation("assert", "$value INSIDE $" + paramName));
            }
            // VALUE -- server-side computed expression ([Value]).
            var valueAttr = prop.GetCustomAttribute<ValueAttribute>(inherit: false);
            if (valueAttr != null && !string.IsNullOrWhiteSpace(valueAttr.Expression))
            {
                annotations.Add(QuotedAnnotation("value", valueAttr.Expression));
            }

            // DEFAULT
            var defaultAttr = prop.GetCustomAttribute<DefaultAttribute>(inherit: false);
            if (defaultAttr != null)
            {
                annotations.Add(QuotedAnnotation("default", defaultAttr.Value));
            }

            // COMMENT ([Comment]) -> the SchemaAttribute.Comment slot the
            // emitter renders as a COMMENT "<text>" clause.
            var commentAttr = prop.GetCustomAttribute<CommentAttribute>(inherit: false);

            return new SchemaAttribute(
                name: columnName,
                type: typeToken,
                isKey: prop.GetCustomAttribute<IdAttribute>(inherit: false) != null,
                comment: commentAttr?.Text,
                annotations: annotations,
                sourceLine: 0);
        }

        /// <summary>
        /// Recognise the canonical "non-empty string" assert the legacy
        /// authoring layer carried on every required column. On record-typed
        /// FK fields it's redundant (SurrealDB enforces the type already),
        /// and emitting it produces invalid DDL because the comparison is
        /// against a string literal rather than a record.
        /// </summary>
        private static bool IsLegacyNonEmptyStringAssert(string expr)
        {
            if (string.IsNullOrWhiteSpace(expr)) return false;
            var trimmed = expr.Replace(" ", string.Empty);
            return trimmed == "$value!=NONEAND$value!=\"\""
                || trimmed == "$value!=\"\"AND$value!=NONE";
        }

        /// <summary>
        /// If the property type is a CLR enum, return a stable display name
        /// (e.g. <c>BridgeTx.StatusKind</c>) for the docs legend. Otherwise null.
        /// </summary>
        private static string? ResolveEnumName(PropertyInfo prop)
        {
            var t = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
            if (!t.IsEnum) return null;
            return (prop.DeclaringType?.Name ?? string.Empty) + "." + t.Name;
        }

        /// <summary>
        /// Encode a single enum value for the <c>values=</c> annotation arg.
        /// Replaces commas and equals so the parser can split on those.
        /// </summary>
        private static string EncodeEnumValue(string v)
            => v.Replace("\\", "\\\\").Replace(",", "\\,").Replace("=", "\\=");

        private static SchemaAttribute BuildExtraField(ExtraSurrealFieldAttribute extra)
        {
            var annotations = new List<SchemaAnnotation>();
            if (extra.Flexible)
                annotations.Add(SimpleAnnotation("flexible", string.Empty));
            if (!string.IsNullOrEmpty(extra.FieldGroup))
                annotations.Add(QuotedAnnotation("fieldgroup", extra.FieldGroup!));
            if (!string.IsNullOrEmpty(extra.Assert))
                annotations.Add(QuotedAnnotation("assert", extra.Assert!));
            if (!string.IsNullOrEmpty(extra.Default))
                annotations.Add(QuotedAnnotation("default", extra.Default!));
            return new SchemaAttribute(
                name: extra.Name,
                type: extra.Type,
                isKey: false,
                comment: null,
                annotations: annotations,
                sourceLine: 0);
        }

        // ─── Naming / typing helpers ──────────────────────────────────────

        private static string ResolveColumnName(PropertyInfo prop, ColumnAttribute? column)
        {
            if (column != null && !string.IsNullOrWhiteSpace(column.Name)) return column.Name!;
            // Derive from the property name via the process-wide naming
            // convention (default snake_case) — the SAME source the runtime
            // JSON policy uses, so the column name and the JSON wire name always
            // agree without a per-property [JsonPropertyName].
            return SurrealForge.Client.Schema.SurrealNaming.ToColumnName(prop.Name);
        }

        private static string ResolveTypeToken(PropertyInfo prop, ColumnAttribute? column)
        {
            // [References<T>] wins over Column.Type because the FK contract
            // is more specific than the storage type the author may have
            // declared on the property. The EmitAsString escape hatch flips
            // the emit back to the legacy string shape for adapters that
            // have not been migrated to record-typed traversal yet.
            var refs = prop.GetCustomAttribute<ReferencesAttribute>(inherit: false);
            if (refs != null)
            {
                var targetTable = refs.Target.GetCustomAttribute<SurrealTableAttribute>(inherit: false)?.Name
                    ?? throw new InvalidOperationException(
                        $"[References(typeof({refs.Target.Name}))] on '{prop.DeclaringType?.Name}.{prop.Name}' " +
                        "points at a type without [SurrealTable].");
                if (refs.EmitAsString)
                {
                    return refs.Optional ? "option<string>" : "string";
                }
                var token = "record<" + targetTable + ">";
                return refs.Optional ? "option<" + token + ">" : token;
            }

            // Explicit Column.Type wins over CLR inference when no FK is declared.
            if (column != null && !string.IsNullOrWhiteSpace(column.Type)) return column.Type!;

            // Nullability sources, in order:
            //   * [Required] forces NOT NULL (overrides everything below).
            //   * [Optional] forces option<> (explicit override).
            //   * a C# nullable-reference-type annotation (`string?`) — read from
            //     the compiler's NullableAttribute byte-blob — infers option<>
            //     with no attribute needed. (Nullable<T> value types are handled
            //     inside MapClrTypeToSurreal.)
            var required = prop.GetCustomAttribute<RequiredAttribute>(inherit: false) != null;
            var optional = prop.GetCustomAttribute<OptionalAttribute>(inherit: false) != null
                           || IsNullableReferenceType(prop);
            if (prop.GetCustomAttribute<OptionalAttribute>(inherit: false) != null && required)
            {
                throw new InvalidOperationException(
                    $"'{prop.DeclaringType?.Name}.{prop.Name}' carries both [Optional] and [Required] -- " +
                    "these are mutually exclusive nullability overrides.");
            }
            var (baseToken, inferredOption) = MapClrTypeToSurreal(prop.PropertyType);
            // [Required] suppresses the option<> wrap regardless of CLR
            // inference (e.g. forces a `int?` property to a NOT NULL column),
            // exactly mirroring how [Optional] forces the wrap.
            if (!required && (optional || inferredOption))
            {
                return baseToken.StartsWith("option<", StringComparison.Ordinal) ? baseToken : "option<" + baseToken + ">";
            }
            return baseToken;
        }

        /// <summary>
        /// Best-effort CLR -> SurrealDB token mapping. Used by the
        /// reflection-based AttributeSchemaScanner to project POCO property
        /// types to SurrealQL field type tokens during <c>.surql</c> emission.
        /// </summary>
        private static (string token, bool inferredNullable) MapClrTypeToSurreal(Type t)
        {
            // Unwrap Nullable<T>.
            var nullable = Nullable.GetUnderlyingType(t);
            if (nullable != null) return (MapClrTypeToSurreal(nullable).token, true);

            // Reference-type nullability is read from the compiler's
            // NullableAttribute byte-blob by IsNullableReferenceType (called from
            // ResolveTypeToken), not here — this mapper only carries the storage
            // token. [Optional] remains an explicit override for the rare case of
            // forcing option<> on a non-nullable property.

            // CLR enums round-trip as JSON strings (the consumer wires
            // JsonStringEnumConverter) and project onto SurrealDB `string`.
            // The closed-set assertion is supplied via [Inside(...)] on the
            // property; the mapper here only carries the storage type.
            if (t.IsEnum) return ("string", false);

            if (t == typeof(string)) return ("string", false);
            if (t == typeof(long) || t == typeof(int) || t == typeof(short) || t == typeof(byte)
                || t == typeof(ulong) || t == typeof(uint) || t == typeof(ushort) || t == typeof(sbyte))
                return ("int", false);
            if (t == typeof(decimal) || t == typeof(double) || t == typeof(float)) return ("decimal", false);
            if (t == typeof(bool)) return ("bool", false);
            if (t == typeof(DateTime) || t == typeof(DateTimeOffset)) return ("datetime", false);
            if (t == typeof(TimeSpan)) return ("duration", false);
            if (t == typeof(Guid)) return ("string", false); // Guids round-trip as 'N' hex strings.

            // IDictionary<string,string> / object bag => object.
            if (typeof(System.Collections.IDictionary).IsAssignableFrom(t)
                || t.FullName == "System.Text.Json.JsonElement"
                || t == typeof(object))
            {
                return ("object", false);
            }

            // IEnumerable<T> / arrays.
            if (t.IsArray)
            {
                var (inner, _) = MapClrTypeToSurreal(t.GetElementType()!);
                return ("array<" + inner + ">", false);
            }
            if (t.IsGenericType)
            {
                var genericArgs = t.GetGenericArguments();
                if (genericArgs.Length == 1)
                {
                    var (inner, _) = MapClrTypeToSurreal(genericArgs[0]);
                    return ("array<" + inner + ">", false);
                }
            }

            throw new NotSupportedException(
                "Cannot infer SurrealDB type for CLR type '" + t.FullName + "'. " +
                "Add [Column(Type = \"...\")] explicitly.");
        }

        /// <summary>
        /// True when <paramref name="prop"/> is a C# nullable-reference-type
        /// (<c>string?</c>), inferred from the compiler-emitted
        /// <c>NullableAttribute</c> (per-member) / <c>NullableContextAttribute</c>
        /// (per-declaring-type) byte-blob. This is plain custom-attribute
        /// reflection — it works on netstandard2.0 as well as net8.0, unlike
        /// <c>NullabilityInfoContext</c> (net6+ only). Value types are excluded
        /// (their nullability is carried by <c>Nullable&lt;T&gt;</c>, handled in
        /// <see cref="MapClrTypeToSurreal"/>); a member with no nullability
        /// metadata (oblivious context) reads as not-null.
        /// In the NRT flag encoding: 1 = not-null, 2 = nullable.
        /// </summary>
        private static bool IsNullableReferenceType(PropertyInfo prop)
        {
            var t = prop.PropertyType;
            if (t.IsValueType) return false; // value-type nullability => Nullable<T>

            // Per-member [Nullable(...)]: a single byte or a byte[] whose FIRST
            // element describes the top-level type.
            foreach (var attr in prop.GetCustomAttributes(inherit: false))
            {
                var at = attr.GetType();
                if (at.FullName != "System.Runtime.CompilerServices.NullableAttribute") continue;
                var field = at.GetField("NullableFlags");
                if (field?.GetValue(attr) is byte[] flags && flags.Length > 0)
                    return flags[0] == 2;
            }

            // Fall back to the declaring type's [NullableContext(flag)] default.
            var declaring = prop.DeclaringType;
            while (declaring != null)
            {
                foreach (var attr in declaring.GetCustomAttributes(inherit: false))
                {
                    var at = attr.GetType();
                    if (at.FullName != "System.Runtime.CompilerServices.NullableContextAttribute") continue;
                    if (at.GetField("Flag")?.GetValue(attr) is byte flag)
                        return flag == 2;
                }
                declaring = declaring.DeclaringType; // nested types inherit the enclosing context
            }

            return false; // oblivious => not-null
        }

        /// <summary>PascalCase -> snake_case. Mirrors generator inverse.</summary>
        internal static string ToSnakeCase(string pascal)
        {
            if (string.IsNullOrEmpty(pascal)) return pascal;
            var sb = new StringBuilder(pascal.Length + 4);
            for (int i = 0; i < pascal.Length; i++)
            {
                var c = pascal[i];
                if (char.IsUpper(c))
                {
                    if (i > 0) sb.Append('_');
                    sb.Append(char.ToLowerInvariant(c));
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }

        // ─── Annotation construction shims ────────────────────────────────

        private static SchemaAnnotation SimpleAnnotation(string directive, string raw)
        {
            // Mirrors the parser's "bare directive" representation
            // (RawArguments == empty), which the SurqlEmitter detects via
            // HasDirective + treats as present.
            return new SchemaAnnotation(
                directive: directive,
                rawArguments: raw,
                arguments: EmptyArgs,
                sourceLine: 0,
                sourceColumn: 0);
        }

        private static SchemaAnnotation QuotedAnnotation(string directive, string value)
        {
            // The emitter's ExtractPrimaryValue path expects either a bare
            // string (returned verbatim) or a quoted+escaped string. Since
            // our annotation values may contain embedded quotes (e.g.
            // ASSERT $value INSIDE [\"A\"]), we feed the emitter the
            // already-quoted form so its TryDecodeQuoted unescaping
            // produces the original text.
            var encoded = "\"" + EscapeForRawArguments(value) + "\"";
            return new SchemaAnnotation(
                directive: directive,
                rawArguments: encoded,
                arguments: EmptyArgs,
                sourceLine: 0,
                sourceColumn: 0);
        }

        private static string EscapeForRawArguments(string s)
        {
            var sb = new StringBuilder(s.Length + 8);
            foreach (var ch in s)
            {
                switch (ch)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\t': sb.Append("\\t"); break;
                    default: sb.Append(ch); break;
                }
            }
            return sb.ToString();
        }

        private static readonly IReadOnlyDictionary<string, string> EmptyArgs = new Dictionary<string, string>();
    }
}
