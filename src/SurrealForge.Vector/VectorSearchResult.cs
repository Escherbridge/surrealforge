// SPDX-License-Identifier: MIT
// SurrealForge.Vector -- see AGENTS.md §Query surface.

using System;

namespace SurrealForge.Vector;

/// <summary>One search hit: the record plus its <c>dist</c> projection. See AGENTS.md §Query surface.</summary>
public sealed class VectorSearchResult<T>
{
    /// <summary>The matched record, deserialized as <typeparamref name="T"/>.</summary>
    public T Record { get; }

    /// <summary>The <c>dist</c> value: KNN/metric distance, or cosine similarity on the brute-force cosine path.</summary>
    public double Distance { get; }

    public VectorSearchResult(T record, double distance)
    {
        if (record is null) throw new ArgumentNullException(nameof(record));
        Record = record;
        Distance = distance;
    }
}
