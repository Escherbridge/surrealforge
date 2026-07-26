// SPDX-License-Identifier: MIT
// SurrealForge.Vector -- see AGENTS.md §Query surface.

using SurrealForge.Client.Query;

namespace SurrealForge.Vector;

/// <summary>Which SQL path <c>VectorSearchAsync</c> emits. See AGENTS.md §Query surface.</summary>
public enum VectorSearchStrategy
{
    /// <summary>Indexed KNN via <c>field &lt;|K[,EF]|&gt; $q</c> — requires a Phase-0 HNSW/MTREE index.</summary>
    IndexedKnn,
    /// <summary>Brute-force via <c>vector::similarity::*</c> / <c>vector::distance::*</c> — un-indexed/small tables.</summary>
    BruteForce,
}

/// <summary>Options bag for <c>SurrealVectorSearchExtensions.VectorSearchAsync</c>.</summary>
public sealed class VectorSearchOptions
{
    /// <summary>Number of nearest neighbours to return. Default 10.</summary>
    public int K { get; set; } = 10;

    /// <summary>Metric for the brute-force path (indexed KNN uses the index's own metric). Default Cosine.</summary>
    public VectorMetric Metric { get; set; } = VectorMetric.Cosine;

    /// <summary>Search path. Default <see cref="VectorSearchStrategy.IndexedKnn"/>.</summary>
    public VectorSearchStrategy Strategy { get; set; } = VectorSearchStrategy.IndexedKnn;

    /// <summary>HNSW search beam width (the EF in <c>&lt;|K,EF|&gt;</c>). Null = server default. Indexed path only.</summary>
    public int? Ef { get; set; }

    /// <summary>
    /// Optional extra predicate combined into the WHERE clause. Build via
    /// <see cref="SurrealQuery.Of"/> with a predicate body (e.g.
    /// <c>SurrealQuery.Of("status = $status").WithParam("status", s)</c>);
    /// its params are merged into the search query. Must not bind <c>$q</c>.
    /// </summary>
    public SurrealQuery? Filter { get; set; }
}
