// SPDX-License-Identifier: MIT
// SurrealForge.Vector -- token-budgeted overlapping text windows.
// Heuristics + rationale: AGENTS.md §Chunking.

using System;
using System.Collections.Generic;

namespace SurrealForge.Vector;

/// <summary>Options for <see cref="TextChunker"/>. Token counts are approximate (chars / <see cref="CharsPerToken"/>).</summary>
public sealed class TextChunkerOptions
{
    /// <summary>Approximate token budget per chunk. Default 256.</summary>
    public int MaxTokens { get; set; } = 256;

    /// <summary>Approximate tokens shared between consecutive chunks. Default 32. Must be &lt; <see cref="MaxTokens"/>.</summary>
    public int OverlapTokens { get; set; } = 32;

    /// <summary>Chars-per-token heuristic (English prose ≈ 4). Default 4.0.</summary>
    public double CharsPerToken { get; set; } = 4.0;

    /// <summary>Prefer cutting a window at the last whitespace inside it. Default true.</summary>
    public bool BreakOnWhitespace { get; set; } = true;
}

/// <summary>One chunk: the text plus its start offset in the source string.</summary>
public sealed class TextChunk
{
    /// <summary>The chunk text (a substring of the source).</summary>
    public string Text { get; }

    /// <summary>Start offset of <see cref="Text"/> within the source string.</summary>
    public int Start { get; }

    public TextChunk(string text, int start)
    {
        Text = text ?? throw new ArgumentNullException(nameof(text));
        Start = start;
    }
}

/// <summary>Splits long documents into overlapping, approximately token-budgeted windows. No model dependency.</summary>
public static class TextChunker
{
    /// <summary>Chunk <paramref name="text"/>; empty/whitespace input yields an empty list.</summary>
    public static IReadOnlyList<TextChunk> Chunk(string text, TextChunkerOptions? options = null)
    {
        var o = options ?? new TextChunkerOptions();
        if (o.MaxTokens <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxTokens must be positive.");
        if (o.OverlapTokens < 0 || o.OverlapTokens >= o.MaxTokens)
            throw new ArgumentOutOfRangeException(nameof(options),
                "OverlapTokens must be non-negative and smaller than MaxTokens.");
        if (o.CharsPerToken <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "CharsPerToken must be positive.");

        var chunks = new List<TextChunk>();
        if (string.IsNullOrWhiteSpace(text)) return chunks;

        int windowChars = Math.Max(1, (int)(o.MaxTokens * o.CharsPerToken));
        int overlapChars = Math.Max(0, (int)(o.OverlapTokens * o.CharsPerToken));

        int pos = 0;
        while (pos < text.Length)
        {
            int end = Math.Min(pos + windowChars, text.Length);

            // Mid-document windows prefer a whitespace boundary so a word is
            // never split; the overlap re-covers whatever the cut trimmed.
            if (o.BreakOnWhitespace && end < text.Length)
            {
                int ws = LastWhitespaceBefore(text, pos, end);
                if (ws > pos) end = ws;
            }

            var slice = text.Substring(pos, end - pos);
            if (slice.Trim().Length > 0)
                chunks.Add(new TextChunk(slice, pos));

            if (end >= text.Length) break;
            // Always make forward progress even when overlap ≥ produced window.
            pos = Math.Max(pos + 1, end - overlapChars);
        }

        return chunks;
    }

    private static int LastWhitespaceBefore(string text, int start, int end)
    {
        for (int i = end - 1; i > start; i--)
        {
            if (char.IsWhiteSpace(text[i])) return i;
        }
        return -1;
    }
}
