// SPDX-License-Identifier: MIT
// SurrealForge.Vector -- schema-derived embedding job description.
// See AGENTS.md §Schema scanning.

using System;
using SurrealForge.Client.Query;
using SurrealForge.Client.Schema;

namespace SurrealForge.Vector;

/// <summary>One [Embedded] declaration surfaced as a runnable job description.</summary>
public sealed class EmbeddingJobDefinition
{
    /// <summary>The scanned POCO type.</summary>
    public Type EntityType { get; }

    /// <summary>SurrealDB table name (via <see cref="SurrealSchemaRegistry"/>).</summary>
    public string Table { get; }

    /// <summary>Source text column (snake_case wire name).</summary>
    public string SourceColumn { get; }

    /// <summary>Target vector column.</summary>
    public string TargetColumn { get; }

    /// <summary>Sibling column persisting the source text's content hash (idempotency skip).</summary>
    public string HashColumn => TargetColumn + "_hash";

    /// <summary>Encoder profile name; <see cref="SurrealVectorOptions.DefaultProfile"/> when the attribute omitted it.</summary>
    public string Profile { get; }

    /// <summary>Write-time vs batched execution.</summary>
    public EmbeddingMode Mode { get; }

    /// <summary>Stable job identity: <c>{table}.{target_column}</c>.</summary>
    public string JobName => Table + "." + TargetColumn;

    public EmbeddingJobDefinition(
        Type entityType, string table, string sourceColumn, string targetColumn,
        string? profile, EmbeddingMode mode)
    {
        EntityType = entityType ?? throw new ArgumentNullException(nameof(entityType));
        Table = SurrealIdentifier.ForTable(table);
        SourceColumn = VectorFieldPath.Validate(sourceColumn, nameof(sourceColumn));
        TargetColumn = VectorFieldPath.Validate(targetColumn, nameof(targetColumn));
        Profile = string.IsNullOrWhiteSpace(profile) ? SurrealVectorOptions.DefaultProfile : profile!;
        Mode = mode;
    }
}
