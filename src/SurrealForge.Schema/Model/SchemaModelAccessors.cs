// SPDX-License-Identifier: UNLICENSED
// SurrealForge.Schema -- shared read accessors over SchemaModel annotations.
//
// Both SurqlEmitter (emit) and SchemaDiff (compare) need to pull the primary
// value out of an annotation (DEFAULT "…", ASSERT "…") or test for a bare
// directive (readonly, flexible). SurqlEmitter historically kept these private;
// this exposes the same decode semantics so the diff compares on identical
// values to what the emitter would write. Keep the two in lockstep.

#nullable enable

using System.Collections.Generic;
using System.Text;

namespace SurrealForge.Schema.Model
{
    /// <summary>Read accessors over <see cref="SchemaAnnotation"/> lists.</summary>
    public static class SchemaModelAccessors
    {
        /// <summary>True when any annotation carries <paramref name="directive"/>.</summary>
        public static bool HasDirective(IReadOnlyList<SchemaAnnotation> anns, string directive)
        {
            foreach (var a in anns) if (a.Directive == directive) return true;
            return false;
        }

        /// <summary>
        /// The decoded primary value of the first annotation matching
        /// <paramref name="directive"/> (unescaping a single quoted payload),
        /// or null when absent. Mirrors <c>SurqlEmitter.ExtractPrimaryValue</c>.
        /// </summary>
        public static string? FirstArg(IReadOnlyList<SchemaAnnotation> anns, string directive)
        {
            foreach (var a in anns)
                if (a.Directive == directive) return ExtractPrimaryValue(a);
            return null;
        }

        internal static string? ExtractPrimaryValue(SchemaAnnotation a)
        {
            var raw = a.RawArguments?.Trim() ?? string.Empty;
            if (raw.Length == 0) return string.Empty;
            if (raw[0] == '"')
            {
                var (decoded, consumed) = TryDecodeQuoted(raw);
                if (consumed == raw.Length) return decoded;
            }
            return raw;
        }

        private static (string decoded, int consumed) TryDecodeQuoted(string raw)
        {
            if (raw.Length == 0 || raw[0] != '"') return (raw, 0);
            var sb = new StringBuilder();
            for (int i = 1; i < raw.Length; i++)
            {
                char c = raw[i];
                if (c == '\\' && i + 1 < raw.Length)
                {
                    char nx = raw[i + 1];
                    switch (nx)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case 'n': sb.Append('\n'); break;
                        case 't': sb.Append('\t'); break;
                        default: sb.Append(nx); break;
                    }
                    i++;
                }
                else if (c == '"')
                {
                    return (sb.ToString(), i + 1);
                }
                else sb.Append(c);
            }
            return (sb.ToString(), raw.Length);
        }
    }
}
