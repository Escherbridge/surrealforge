// SPDX-License-Identifier: MIT
// SurrealForge.Vector -- see AGENTS.md §Encoder abstraction.

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SurrealForge.Vector;

/// <summary>Text → embedding encoder. The batch overload is mandatory — see AGENTS.md §Encoder abstraction.</summary>
public interface IVectorEncoder
{
    /// <summary>Output vector dimension (must match the index's DIMENSION).</summary>
    int Dimension { get; }

    /// <summary>Encode a single string (the query-side path).</summary>
    ValueTask<float[]> EncodeAsync(string text, CancellationToken ct = default);

    /// <summary>Encode a batch (the write/backfill cost center; not optional sugar).</summary>
    ValueTask<float[][]> EncodeAsync(IReadOnlyList<string> texts, CancellationToken ct = default);
}
