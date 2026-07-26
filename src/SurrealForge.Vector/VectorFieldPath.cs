// SPDX-License-Identifier: MIT
// SurrealForge.Vector -- see AGENTS.md §Query surface (identifier hygiene).

using System;
using System.Text.RegularExpressions;

namespace SurrealForge.Vector;

/// <summary>Strict snake_case field-path validator (no reserved-word denylist — column names like <c>content</c> are legal).</summary>
internal static class VectorFieldPath
{
    // Lowercase snake_case segments, optionally dotted (a.b.c). Rejects
    // whitespace, quotes, semicolons — anything that could alter SQL shape.
    private static readonly Regex FieldRegex =
        new Regex(@"^[a-z_][a-z0-9_]*(\.[a-z_][a-z0-9_]*)*$", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    /// <summary>Validate and return the field path, or throw <see cref="ArgumentException"/>.</summary>
    internal static string Validate(string field, string paramName)
    {
        if (string.IsNullOrWhiteSpace(field))
            throw new ArgumentException("Field name must not be empty.", paramName);
        if (!FieldRegex.IsMatch(field))
            throw new ArgumentException(
                "Field '" + field + "' is not a valid SurrealDB column path. " +
                "Allowed shape: lowercase snake_case segments, optionally dotted " +
                "(a.b.c). No whitespace, uppercase, quotes, or punctuation.",
                paramName);
        return field;
    }
}
