// SPDX-License-Identifier: MIT
// Window / overlap / edge-case tests for the token-budgeted chunker.

using FluentAssertions;
using SurrealForge.Vector;

namespace SurrealForge.Vector.Tests;

public sealed class TextChunkerTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \t\n ")]
    public void Empty_or_whitespace_input_yields_no_chunks(string? text)
    {
        TextChunker.Chunk(text!).Should().BeEmpty();
    }

    [Fact]
    public void Short_text_yields_a_single_chunk_equal_to_the_text()
    {
        var chunks = TextChunker.Chunk("hello world");

        chunks.Should().HaveCount(1);
        chunks[0].Text.Should().Be("hello world");
        chunks[0].Start.Should().Be(0);
    }

    [Fact]
    public void Windows_overlap_by_the_configured_amount()
    {
        // CharsPerToken=1 makes the char math exact: window 4, overlap 2 → step 2.
        var options = new TextChunkerOptions
        {
            MaxTokens = 4,
            OverlapTokens = 2,
            CharsPerToken = 1,
            BreakOnWhitespace = false,
        };

        var chunks = TextChunker.Chunk("abcdefgh", options);

        chunks.Should().HaveCount(3);
        chunks[0].Text.Should().Be("abcd");
        chunks[0].Start.Should().Be(0);
        chunks[1].Text.Should().Be("cdef");
        chunks[1].Start.Should().Be(2);
        chunks[2].Text.Should().Be("efgh");
        chunks[2].Start.Should().Be(4);
    }

    [Fact]
    public void Break_on_whitespace_avoids_splitting_words()
    {
        var options = new TextChunkerOptions
        {
            MaxTokens = 10,
            OverlapTokens = 0,
            CharsPerToken = 1,
            BreakOnWhitespace = true,
        };

        var chunks = TextChunker.Chunk("aaaa bbbb cccc", options);

        // Window of 10 chars would cut inside "bbbb cccc"; the whitespace at
        // index 9 becomes the boundary instead.
        chunks[0].Text.Should().Be("aaaa bbbb");
        chunks.Should().OnlyContain(c => !c.Text.Contains("bbbb c"));
        string.Concat(chunks.Select(c => c.Text.Trim())).Should().Contain("cccc");
    }

    [Fact]
    public void Chunk_starts_index_into_the_source_string()
    {
        var options = new TextChunkerOptions
        {
            MaxTokens = 4,
            OverlapTokens = 1,
            CharsPerToken = 1,
            BreakOnWhitespace = false,
        };
        const string text = "abcdefgh";

        foreach (var chunk in TextChunker.Chunk(text, options))
        {
            text.Substring(chunk.Start, chunk.Text.Length).Should().Be(chunk.Text);
        }
    }

    [Fact]
    public void Overlap_greater_or_equal_to_window_is_rejected()
    {
        var options = new TextChunkerOptions { MaxTokens = 4, OverlapTokens = 4 };
        var act = () => TextChunker.Chunk("abcdefgh", options);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Non_positive_max_tokens_is_rejected()
    {
        var options = new TextChunkerOptions { MaxTokens = 0 };
        var act = () => TextChunker.Chunk("abc", options);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Progress_is_guaranteed_even_with_pathological_overlap()
    {
        // Overlap of window-1 chars: still terminates and covers the text.
        var options = new TextChunkerOptions
        {
            MaxTokens = 4,
            OverlapTokens = 3,
            CharsPerToken = 1,
            BreakOnWhitespace = false,
        };

        var chunks = TextChunker.Chunk("abcdefgh", options);

        chunks.Should().NotBeEmpty();
        chunks[chunks.Count - 1].Text.Should().EndWith("h");
    }
}
