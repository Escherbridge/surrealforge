// SPDX-License-Identifier: UNLICENSED
// SurrealForge.Schema.Tests -- CLI argument parser.

using FluentAssertions;
using SurrealForge.Schema.Cli;

namespace SurrealForge.Schema.Tests
{
    public class CliArgsTests
    {
        [Fact]
        public void Empty_args_yields_empty_command()
        {
            var p = CliArgs.Parse(System.Array.Empty<string>());
            p.Command.Should().BeEmpty();
            p.SubCommand.Should().BeEmpty();
            p.Positionals.Should().BeEmpty();
            p.Flags.Should().BeEmpty();
        }

        [Fact]
        public void Migrate_up_with_flag_value()
        {
            var p = CliArgs.Parse(new[] { "migrate", "up", "--connection", "http://localhost:8442" });
            p.Command.Should().Be("migrate");
            p.SubCommand.Should().Be("up");
            p.Flag("connection").Should().Be("http://localhost:8442");
        }

        [Fact]
        public void Flag_eq_form_is_supported()
        {
            var p = CliArgs.Parse(new[] { "migrate", "up", "--connection=http://host:1234" });
            p.Flag("connection").Should().Be("http://host:1234");
        }

        [Fact]
        public void Bare_flag_present_with_empty_value()
        {
            var p = CliArgs.Parse(new[] { "migrate", "up", "--force" });
            p.HasFlag("force").Should().BeTrue();
            p.Flag("force").Should().Be("");
        }

        [Fact]
        public void Positional_after_command_and_subcommand_lands_in_positionals()
        {
            var p = CliArgs.Parse(new[] { "generate", "Persistence/Schemas/010_wallet.mermaid" });
            p.Command.Should().Be("generate");
            p.SubCommand.Should().Be("Persistence/Schemas/010_wallet.mermaid");
        }

        [Fact]
        public void Generate_with_three_positionals_routes_extras_to_positionals_list()
        {
            var p = CliArgs.Parse(new[] { "generate", "a.mermaid", "b.mermaid", "c.mermaid" });
            p.Command.Should().Be("generate");
            p.SubCommand.Should().Be("a.mermaid");
            p.Positionals.Should().Equal("b.mermaid", "c.mermaid");
        }
    }
}
