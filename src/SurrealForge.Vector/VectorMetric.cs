// SPDX-License-Identifier: MIT
// SurrealForge.Vector -- see AGENTS.md §Query surface.

namespace SurrealForge.Vector;

/// <summary>Distance/similarity metric for vector search. See AGENTS.md §Query surface.</summary>
public enum VectorMetric
{
    /// <summary>Cosine — brute-force path uses <c>vector::similarity::cosine</c> (higher = closer).</summary>
    Cosine,
    /// <summary>Euclidean — brute-force path uses <c>vector::distance::euclidean</c> (lower = closer).</summary>
    Euclidean,
    /// <summary>Manhattan — brute-force path uses <c>vector::distance::manhattan</c> (lower = closer).</summary>
    Manhattan,
}
