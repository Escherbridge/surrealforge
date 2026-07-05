using System;
using System.Collections.Generic;
using FluentAssertions;
using SurrealForge.Client.Query;
using Xunit;

namespace SurrealForge.Client.Tests.Query;

public sealed class SurrealQueryTests
{
    // ─── Of / WithParam (ported from YourApp.Tests) ─────────────────────

    [Fact]
    public void Of_returns_query_with_empty_params()
    {
        var q = SurrealQuery.Of("SELECT * FROM wallet");

        q.Sql.Should().Be("SELECT * FROM wallet");
        q.Params.Should().BeEmpty();
        q.IsMultiStatement.Should().BeFalse();
    }

    [Fact]
    public void WithParam_adds_entry_and_returns_new_query()
    {
        var q = SurrealQuery.Of("SELECT * FROM wallet WHERE owner = $owner")
                            .WithParam("owner", "avatar:123");

        q.Params.Should().ContainKey("owner").WhoseValue.Should().Be("avatar:123");
    }

    [Fact]
    public void WithParam_is_immutable_original_unchanged()
    {
        var original = SurrealQuery.Of("SELECT * FROM wallet WHERE owner = $owner");
        var derived  = original.WithParam("owner", "avatar:123");

        original.Params.Should().BeEmpty();
        derived.Params.Should().HaveCount(1);
    }

    [Fact]
    public void WithParam_rejects_empty_key()
    {
        var act = () => SurrealQuery.Of("SELECT * FROM wallet").WithParam("", "value");
        act.Should().Throw<ArgumentException>().WithMessage("*Parameter key*");
    }

    [Fact]
    public void WithParam_rejects_key_with_leading_dollar()
    {
        var act = () => SurrealQuery.Of("SELECT * FROM wallet").WithParam("$owner", "x");
        act.Should().Throw<ArgumentException>().WithMessage("*leading '$'*");
    }

    [Fact]
    public void WithParams_merges_multiple_parameters()
    {
        var q = SurrealQuery.Of("SELECT * FROM wallet WHERE owner = $owner AND chain = $chain")
                            .WithParams(new Dictionary<string, object?>
                            {
                                ["owner"] = "avatar:1",
                                ["chain"] = "algorand",
                            });

        q.Params.Should().HaveCount(2)
                .And.ContainKey("owner")
                .And.ContainKey("chain");
    }

    [Fact]
    public void Of_rejects_null_sql()
    {
        var act = () => SurrealQuery.Of(null!);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Of_rejects_empty_sql()
    {
        var act = () => SurrealQuery.Of("   ");
        act.Should().Throw<ArgumentException>();
    }

    // ─── Of — semicolon ban (closes code-review C5) ──────────────────────────

    [Fact]
    public void Of_rejects_semicolon_in_body()
    {
        // The only legal multi-statement path is SurrealQuery.Combine; an
        // inline ';' would let an arbitrary second statement smuggle through
        // the SQL body and bypass per-statement result accounting.
        var act = () => SurrealQuery.Of("SELECT * FROM wallet; DELETE FROM wallet");

        act.Should().Throw<ArgumentException>()
           .WithMessage("*single statement*")
           .WithMessage("*Combine*");
    }

    [Fact]
    public void Of_rejects_trailing_semicolon()
    {
        var act = () => SurrealQuery.Of("SELECT * FROM wallet;");

        act.Should().Throw<ArgumentException>()
           .WithMessage("*single statement*");
    }

    // ─── Validate — strict mode (default) ────────────────────────────────────

    [Fact]
    public void Validate_passes_when_all_params_supplied()
    {
        var q = SurrealQuery.Of("SELECT * FROM wallet WHERE owner = $owner AND id = $id")
                            .WithParam("owner", "avatar:1")
                            .WithParam("id", "wallet:abc");

        var act = () => q.Validate(strict: true);
        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_throws_when_param_missing()
    {
        var q = SurrealQuery.Of("SELECT * FROM wallet WHERE owner = $owner");

        var act = () => q.Validate();

        act.Should().Throw<SurrealQueryValidationException>()
           .WithMessage("*$owner*");
    }

    [Fact]
    public void Validate_excludes_LET_defined_vars_from_missing_check()
    {
        // `LET $x = ...` defines $x inside the body; it must NOT be reported
        // missing even though it isn't in the param bag. The supplied params
        // ($_t/$_cid/$_pid) feed the type::record() casts.
        var q = SurrealQuery.Combine(
            SurrealQuery.Of("BEGIN"),
            SurrealQuery.Of("LET $_child = type::record($_t, $_cid)")
                        .WithParam("_t", "quest_run").WithParam("_cid", "a"),
            SurrealQuery.Of("LET $_parent = type::record($_t, $_pid)")
                        .WithParam("_t", "quest_run").WithParam("_pid", "b"),
            SurrealQuery.Of("RELATE $_child->forked_from->$_parent"),
            SurrealQuery.Of("COMMIT"));

        var act = () => q.Validate(strict: true);
        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_still_flags_non_LET_missing_param_alongside_LET_vars()
    {
        // $_child is LET-defined (ok) but $_missing is neither LET-defined nor
        // supplied — it must still be reported.
        var q = SurrealQuery.Combine(
            SurrealQuery.Of("LET $_child = type::record($_t, $_cid)")
                        .WithParam("_t", "quest_run").WithParam("_cid", "a"),
            SurrealQuery.Of("RELATE $_child->forked_from->$_missing"));

        var act = () => q.Validate(strict: false);
        act.Should().Throw<SurrealQueryValidationException>()
           .WithMessage("*$_missing*");
    }

    [Fact]
    public void Validate_strict_throws_on_extra_param()
    {
        var q = SurrealQuery.Of("SELECT * FROM wallet WHERE owner = $owner")
                            .WithParam("owner", "avatar:1")
                            .WithParam("id", "extra_value");

        var act = () => q.Validate(strict: true);

        act.Should().Throw<SurrealQueryValidationException>()
           .WithMessage("*$id*strict*");
    }

    [Fact]
    public void Validate_lenient_tolerates_extra_param()
    {
        var q = SurrealQuery.Of("SELECT * FROM wallet WHERE owner = $owner")
                            .WithParam("owner", "avatar:1")
                            .WithParam("unused", "extra");

        var act = () => q.Validate(strict: false);
        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_catches_all_missing_params()
    {
        var q = SurrealQuery.Of("SELECT * FROM t WHERE a = $a AND b = $b AND c = $c");

        var ex = Assert.Throws<SurrealQueryValidationException>(() => q.Validate());

        ex.Message.Should().Contain("$a").And.Contain("$b").And.Contain("$c");
    }

    // ─── Typed factories ─────────────────────────────────────────────────────

    [Fact]
    public void SelectById_builds_correct_sql()
    {
        var q = SurrealQuery.SelectById("wallet", "abc123");

        // 3.x record-id addressing (a plain `WHERE id = $stringId` matches nothing).
        q.Sql.Should().Be("SELECT * FROM type::record($_t, $_id)");
        q.Params.Should().ContainKey("_t").WhoseValue.Should().Be("wallet");
        q.Params.Should().ContainKey("_id").WhoseValue.Should().Be("abc123");
    }

    [Fact]
    public void SelectAll_builds_correct_sql()
    {
        var q = SurrealQuery.SelectAll("avatar");

        q.Sql.Should().Be("SELECT * FROM avatar");
        q.Params.Should().BeEmpty();
    }

    [Fact]
    public void DeleteById_builds_correct_sql()
    {
        var q = SurrealQuery.DeleteById("wallet", "abc123");

        q.Sql.Should().Be("DELETE type::record($_t, $_id)");
        q.Params.Should().ContainKey("_t").WhoseValue.Should().Be("wallet");
        q.Params.Should().ContainKey("_id").WhoseValue.Should().Be("abc123");
    }

    [Fact]
    public void SelectById_rejects_invalid_table_name()
    {
        var act = () => SurrealQuery.SelectById("INVALID", "id");
        act.Should().Throw<ArgumentException>().WithMessage("*table*");
    }
}
