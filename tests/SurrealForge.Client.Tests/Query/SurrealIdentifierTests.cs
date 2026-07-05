using System;
using FluentAssertions;
using SurrealForge.Client.Query;
using Xunit;

namespace SurrealForge.Client.Tests.Query;

public sealed class SurrealIdentifierTests
{
    // ─── ForTable — valid names ───────────────────────────────────────────────

    [Theory]
    [InlineData("wallet")]
    [InlineData("bridge_tx")]
    [InlineData("avatar_nft")]
    [InlineData("operation_log")]
    [InlineData("swap_state")]
    [InlineData("a")]
    [InlineData("a1")]
    [InlineData("table123")]
    public void ForTable_accepts_valid_names(string name)
    {
        var result = SurrealIdentifier.ForTable(name);
        result.Should().Be(name);
    }

    // ─── ForTable — hostile / invalid character inputs ───────────────────────

    [Theory]
    [InlineData("users; DROP TABLE x", "semicolon injection")]
    [InlineData("users`x",             "backtick")]
    [InlineData("users x",             "space")]
    [InlineData("USERS",               "uppercase")]
    [InlineData("1users",              "leading digit")]
    [InlineData("Users",               "mixed case")]
    [InlineData("",                    "empty string")]
    [InlineData("   ",                 "whitespace only")]
    [InlineData("wallet-tx",           "hyphen")]
    [InlineData("wallet.tx",           "dot")]
    [InlineData("wallet'name",         "single quote")]
    [InlineData("wallet\"name",        "double quote")]
    [InlineData("wallet\nname",        "newline")]
    [InlineData("DROP",                "SQL keyword uppercase")]
    [InlineData("_wallet",             "leading underscore")]
    public void ForTable_rejects_hostile_inputs(string name, string reason)
    {
        var act = () => SurrealIdentifier.ForTable(name);
        act.Should().Throw<ArgumentException>(because: reason);
    }

    // ─── ForTable — reserved-word denylist (closes code-review H4) ───────────
    //
    // One test per ~10 common reserved words: SurrealQL keywords that pass the
    // allowlist regex (lowercase alphanumeric) but still must be rejected
    // because they would alter clause semantics if used unquoted.

    [Theory]
    [InlineData("select")]
    [InlineData("from")]
    [InlineData("where")]
    [InlineData("create")]
    [InlineData("update")]
    [InlineData("delete")]
    [InlineData("relate")]
    [InlineData("define")]
    [InlineData("table")]
    [InlineData("index")]
    [InlineData("return")]
    [InlineData("live")]
    public void ForTable_rejects_reserved_words(string reserved)
    {
        var act = () => SurrealIdentifier.ForTable(reserved);

        act.Should().Throw<SurrealIdentifierException>(
            "reserved word " + reserved + " would alter query semantics if used unquoted")
           .WithMessage("*" + reserved + "*reserved word*");
    }

    [Fact]
    public void ForTable_reserved_word_message_includes_the_offending_word()
    {
        var act = () => SurrealIdentifier.ForTable("select");

        act.Should().Throw<SurrealIdentifierException>()
           .WithMessage("*select*");
    }

    // ─── ForRecordId — valid ─────────────────────────────────────────────────

    [Theory]
    [InlineData("wallet",    "abc123",         "wallet:abc123")]
    [InlineData("bridge_tx", "550e8400-e29b",  "bridge_tx:550e8400-e29b")]
    [InlineData("avatar",    "myid_42",        "avatar:myid_42")]
    public void ForRecordId_returns_combined_id(string table, string id, string expected)
    {
        var result = SurrealIdentifier.ForRecordId(table, id);
        result.Should().Be(expected);
    }

    // ─── ForRecordId — invalid ───────────────────────────────────────────────

    [Fact]
    public void ForRecordId_rejects_invalid_table()
    {
        var act = () => SurrealIdentifier.ForRecordId("INVALID", "someid");
        act.Should().Throw<ArgumentException>().WithMessage("*table*");
    }

    [Theory]
    [InlineData("",              "empty id")]
    [InlineData("   ",           "whitespace id")]
    [InlineData("id with space", "space in id")]
    [InlineData("id;DROP",       "semicolon in id")]
    [InlineData("id'",           "quote in id")]
    public void ForRecordId_rejects_invalid_id_suffix(string id, string reason)
    {
        var act = () => SurrealIdentifier.ForRecordId("wallet", id);
        act.Should().Throw<ArgumentException>(because: reason);
    }

    [Theory]
    [InlineData("select")]
    [InlineData("delete")]
    [InlineData("where")]
    public void ForRecordId_rejects_reserved_id_suffix(string reserved)
    {
        var act = () => SurrealIdentifier.ForRecordId("wallet", reserved);
        act.Should().Throw<SurrealIdentifierException>()
           .WithMessage("*" + reserved + "*reserved word*");
    }
}
