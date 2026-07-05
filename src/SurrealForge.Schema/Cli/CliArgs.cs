// SPDX-License-Identifier: UNLICENSED
// SurrealForge.Schema -- Hand-rolled CLI argument parser.
//
// Why hand-rolled rather than System.CommandLine? The latter has lived on a
// prerelease (`2.0.0-betaN`) version line for years; pinning a betaN version
// in a packaged tool creates a maintenance hazard. The argument shape we need
// is tiny:
//
//   surrealforge <command> <subcommand?> [positional...] [--flag value]...
//
// CliArgs.Parse(string[]) returns a structured view that the dispatcher in
// Program.cs interprets. Unknown flags are tolerated so future additions
// don't blow up old binaries.

using System;
using System.Collections.Generic;

namespace SurrealForge.Schema.Cli
{
    /// <summary>Parsed CLI invocation.</summary>
    public sealed class CliArgs
    {
        /// <summary>First positional token (e.g. <c>migrate</c>).</summary>
        public string Command { get; }
        /// <summary>Second positional token, may be empty (e.g. <c>up</c>).</summary>
        public string SubCommand { get; }
        /// <summary>Remaining positionals (e.g. file paths).</summary>
        public IReadOnlyList<string> Positionals { get; }
        /// <summary>Long-name flags (always <c>--name value</c>); bare <c>--flag</c> becomes <c>flag => ""</c>.</summary>
        public IReadOnlyDictionary<string, string> Flags { get; }

        public CliArgs(
            string command,
            string subCommand,
            IReadOnlyList<string> positionals,
            IReadOnlyDictionary<string, string> flags)
        {
            Command = command ?? string.Empty;
            SubCommand = subCommand ?? string.Empty;
            Positionals = positionals ?? new List<string>();
            Flags = flags ?? new Dictionary<string, string>();
        }

        public bool HasFlag(string name) => Flags.ContainsKey(name);
        public string? Flag(string name) => Flags.TryGetValue(name, out var v) ? v : null;

        public static CliArgs Parse(string[] args)
        {
            if (args == null || args.Length == 0)
            {
                return new CliArgs(string.Empty, string.Empty, new List<string>(), new Dictionary<string, string>());
            }

            string command = string.Empty;
            string subCommand = string.Empty;
            var positionals = new List<string>();
            var flags = new Dictionary<string, string>(StringComparer.Ordinal);
            int positionalIdx = 0;

            for (int i = 0; i < args.Length; i++)
            {
                var a = args[i];
                if (a.StartsWith("--", StringComparison.Ordinal))
                {
                    var name = a.Substring(2);
                    string value = string.Empty;
                    int eq = name.IndexOf('=');
                    if (eq > 0)
                    {
                        value = name.Substring(eq + 1);
                        name = name.Substring(0, eq);
                    }
                    else if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        value = args[i + 1];
                        i++;
                    }
                    flags[name] = value;
                }
                else
                {
                    if (positionalIdx == 0) command = a;
                    else if (positionalIdx == 1) subCommand = a;
                    else positionals.Add(a);
                    positionalIdx++;
                }
            }

            return new CliArgs(command, subCommand, positionals, flags);
        }
    }
}
