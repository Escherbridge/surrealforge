// SPDX-License-Identifier: MIT
// SurrealForge.Vector -- [Embedded] reflection scanner.
// Loud scan-time validation mirrors the Phase-0 index scanner convention.
// See AGENTS.md §Schema scanning.

using System;
using System.Collections.Generic;
using System.Reflection;
using SurrealForge.Client.Schema;

namespace SurrealForge.Vector;

/// <summary>Turns <c>[Embedded]</c> properties on a POCO into <see cref="EmbeddingJobDefinition"/>s.</summary>
public static class EmbeddingSchemaScanner
{
    /// <summary>Scan <typeparamref name="T"/> for [Embedded] properties.</summary>
    public static IReadOnlyList<EmbeddingJobDefinition> Scan<T>() => Scan(typeof(T));

    /// <summary>Non-generic scan. Throws when [Embedded] sits on a non-string property.</summary>
    public static IReadOnlyList<EmbeddingJobDefinition> Scan(Type entityType)
    {
        if (entityType is null) throw new ArgumentNullException(nameof(entityType));

        var defs = new List<EmbeddingJobDefinition>();
        string? table = null;

        foreach (var prop in entityType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var embedded = prop.GetCustomAttribute<EmbeddedAttribute>(inherit: false);
            if (embedded is null) continue;

            if (prop.PropertyType != typeof(string))
                throw new InvalidOperationException(
                    "[Embedded] on '" + entityType.FullName + "." + prop.Name + "' requires a string " +
                    "source property; found '" + prop.PropertyType.Name + "'. Embeddings are computed " +
                    "from text — point the attribute at the text column.");

            table ??= SurrealSchemaRegistry.For(entityType);
            var sourceColumn = ResolveColumnName(prop);

            defs.Add(new EmbeddingJobDefinition(
                entityType, table, sourceColumn, embedded.TargetColumn, embedded.Profile, embedded.Mode));
        }

        return defs;
    }

    /// <summary>[Column(Name=...)] wins; otherwise the process-wide naming convention.</summary>
    private static string ResolveColumnName(PropertyInfo prop)
    {
        var column = prop.GetCustomAttribute<ColumnAttribute>(inherit: false);
        if (column?.Name is string explicitName && !string.IsNullOrWhiteSpace(explicitName))
            return explicitName;
        return SurrealNaming.ToColumnName(prop.Name);
    }
}
