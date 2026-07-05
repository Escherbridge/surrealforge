// SPDX-License-Identifier: UNLICENSED
// SurrealForge.Client.Schema -- the single naming-convention knob that
// drives BOTH the runtime JSON property policy AND the schema column-name
// fallback, so the C# property name, the JSON wire name, and the SurrealDB
// column name stay coherent by construction. Set it ONCE at startup (before
// the first SurrealJsonOptions.Default access); default is SnakeCase.

#nullable enable

using System.Text;
using System.Text.Json;

namespace SurrealForge.Client.Schema
{
    /// <summary>The casing used for SurrealDB field/column + JSON wire names.</summary>
    public enum SurrealNamingConvention
    {
        /// <summary><c>created_date</c> — the default; matches the shipped schemas.</summary>
        SnakeCase,
        /// <summary><c>createdDate</c> — lower camelCase.</summary>
        CamelCase,
    }

    /// <summary>
    /// Process-wide naming convention. Configure ONCE at startup before any
    /// SurrealDB serialization or schema generation happens. Both
    /// <c>SurrealJsonOptions.Default</c> (runtime wire names) and the schema
    /// scanner's column-name fallback read this, so they never drift.
    /// </summary>
    public static class SurrealNaming
    {
        /// <summary>
        /// The active convention. Default <see cref="SurrealNamingConvention.SnakeCase"/>
        /// — coherent with the committed snake_case schemas + the stores' raw
        /// queries. Flip to <see cref="SurrealNamingConvention.CamelCase"/> at
        /// startup if a deployment wants camelCase columns end to end.
        /// </summary>
        public static SurrealNamingConvention Convention { get; set; } = SurrealNamingConvention.SnakeCase;

        /// <summary>Apply the active convention to a PascalCase property name.</summary>
        public static string ToColumnName(string propertyName)
            => Convention == SurrealNamingConvention.CamelCase
                ? ToCamelCase(propertyName)
                : ToSnakeCase(propertyName);

        /// <summary>The matching <see cref="JsonNamingPolicy"/> for the active convention.</summary>
        public static JsonNamingPolicy JsonPolicy
            => Convention == SurrealNamingConvention.CamelCase
                ? JsonNamingPolicy.CamelCase
                : SnakeCasePolicy.Instance;

        // snake_case identical to the schema scanner's historical ToSnakeCase
        // so columns stay byte-for-byte stable when [JsonPropertyName] is dropped.
        internal static string ToSnakeCase(string pascal)
        {
            if (string.IsNullOrEmpty(pascal)) return pascal;
            var sb = new StringBuilder(pascal.Length + 4);
            for (int i = 0; i < pascal.Length; i++)
            {
                var c = pascal[i];
                if (char.IsUpper(c))
                {
                    if (i > 0) sb.Append('_');
                    sb.Append(char.ToLowerInvariant(c));
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }

        internal static string ToCamelCase(string pascal)
        {
            if (string.IsNullOrEmpty(pascal) || !char.IsUpper(pascal[0])) return pascal;
            var arr = pascal.ToCharArray();
            arr[0] = char.ToLowerInvariant(arr[0]);
            return new string(arr);
        }

        /// <summary>
        /// A <see cref="JsonNamingPolicy"/> that delegates to
        /// <see cref="ToSnakeCase"/> so the JSON wire name matches the column
        /// name exactly (rather than relying on STJ's SnakeCaseLower, which
        /// can differ on digits/acronyms).
        /// </summary>
        private sealed class SnakeCasePolicy : JsonNamingPolicy
        {
            public static readonly SnakeCasePolicy Instance = new();
            public override string ConvertName(string name) => ToSnakeCase(name);
        }
    }
}
