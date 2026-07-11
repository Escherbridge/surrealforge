// SPDX-License-Identifier: UNLICENSED
// Test helpers: build small SchemaModel fixtures for diff/reconcile tests
// without standing up POCO reflection.

using System.Collections.Generic;
using SurrealForge.Schema.Model;

namespace SurrealForge.Schema.Tests.Migration
{
    internal static class SchemaModelTestFactory
    {
        public static SchemaModel NewModel(params SchemaEntity[] entities)
            => new SchemaModel("(test)", entities, System.Array.Empty<SchemaRelationship>());

        public static SchemaEntity Table(string name, params SchemaAttribute[] attrs)
            => new SchemaEntity(name, attrs, System.Array.Empty<SchemaAnnotation>(),
                System.Array.Empty<SchemaIndex>(), 0);

        public static SchemaEntity TableWithIndexes(string name, SchemaAttribute[] attrs, SchemaIndex[] indexes)
            => new SchemaEntity(name, attrs, System.Array.Empty<SchemaAnnotation>(), indexes, 0);

        public static SchemaAttribute Field(string name, string type, params SchemaAnnotation[] anns)
            => new SchemaAttribute(name, type, false, null, anns, 0);

        public static SchemaIndex Index(string name, bool unique, params string[] fields)
            => new SchemaIndex(name, fields, unique, 0);

        public static SchemaAnnotation Default(string value) => Quoted("default", value);
        public static SchemaAnnotation Assert(string value) => Quoted("assert", value);
        public static SchemaAnnotation Bare(string directive)
            => new SchemaAnnotation(directive, string.Empty, Empty, 0, 0);

        private static SchemaAnnotation Quoted(string directive, string value)
            => new SchemaAnnotation(directive, "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"",
                Empty, 0, 0);

        private static readonly IReadOnlyDictionary<string, string> Empty = new Dictionary<string, string>();
    }
}
