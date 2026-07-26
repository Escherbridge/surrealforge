// SPDX-License-Identifier: MIT

using FluentAssertions;
using SurrealForge.Client;
using SurrealForge.Client.Query;
using SurrealForge.Client.Schema;
using Xunit;

namespace SurrealForge.Client.Tests.Query;

public sealed class TypedUpdateOnlyBuilderTests
{
    [Fact]
    public void Compound_predicate_and_multi_assignment_are_typed_and_parameterized()
    {
        const string owner = "account:source";
        const string target = "account:target";
        const string claim = "claim:123";
        var now = DateTimeOffset.Parse("2026-07-11T12:00:00Z");

        var query = SurrealWriter.UpdateOnly<ClaimRecord>("claim_record:abc")
            .Where(r => r.Kind == "exclusive"
                && r.OwnerId == owner
                && (r.ClaimKey == null || (r.ClaimKey == claim && r.TargetId == target)))
            .Set(r => r.ClaimKey, claim)
            .Set(r => r.TargetId, target)
            .Set(r => r.ClaimedAt, now)
            .Unset(r => r.CompletedAt)
            .Build();

        query.Sql.Should().StartWith("UPDATE ONLY type::record($_t, $_id) SET ");
        query.Sql.Should().Contain("claim_key = type::string($_s0_claim_key)");
        query.Sql.Should().Contain("target_id = $_s1_target_id");
        query.Sql.Should().Contain("claimed_at = $_s2_claimed_at");
        query.Sql.Should().Contain("completed_at = NONE");
        query.Sql.Should().Contain("claim_key = NONE OR");
        query.Sql.Should().Contain("claim_key = type::string($_w0_claim_key)");
        query.Sql.Should().Contain("owner_id = $_w0_owner_id");
        query.Sql.Should().Contain("target_id = $_w0_target_id");
        query.Sql.Should().NotContain("type::string($_w0_owner_id)");
        query.Sql.Should().NotContain("type::string($_w0_target_id)");
        query.Sql.Should().EndWith(" RETURN AFTER");
        query.Params["_t"].Should().Be("claim_record");
        query.Params["_id"].Should().Be("abc");
        query.Params["_s0_claim_key"].Should().Be(claim);
        query.Params["_s1_target_id"].Should().Be(target);
        query.Params["_s2_claimed_at"].Should().Be(now);
        query.Validate(strict: true);
    }

    [Fact]
    public void Multiple_where_calls_get_collision_free_parameter_names()
    {
        var query = SurrealWriter.UpdateOnly<ClaimRecord>("abc")
            .Where(r => r.Kind == "first")
            .Where(r => r.Kind != "second")
            .Set(r => r.Attempt, 2)
            .Build();

        query.Sql.Should().Contain("kind = type::string($_w0_kind)");
        query.Sql.Should().Contain("kind != type::string($_w1_kind)");
        query.Params["_w0_kind"].Should().Be("first");
        query.Params["_w1_kind"].Should().Be("second");
        query.Validate(strict: true);
    }

    [Fact]
    public void Decimal_assignment_uses_same_coercion_rule_as_typed_upsert()
    {
        var query = SurrealWriter.UpdateOnly<ClaimRecord>("abc")
            .Where(r => r.Attempt == 1)
            .Set(r => r.Amount, 12.5m)
            .Build();

        query.Sql.Should().Contain("amount = type::decimal($_s0_amount)");
        query.Params["_s0_amount"].Should().Be(12.5m);
    }

    [Fact]
    public void Enum_predicate_and_assignment_use_the_same_stored_name()
    {
        var query = SurrealWriter.UpdateOnly<ClaimRecord>("abc")
            .Where(r => r.State == ClaimState.Pending)
            .Set(r => r.State, ClaimState.Claimed)
            .Build();

        query.Params["_w0_state"].Should().Be("Pending");
        query.Params["_s0_state"].Should().Be("Claimed");
    }

    [Fact]
    public void Build_requires_predicate_and_assignment()
    {
        var withoutPredicate = SurrealWriter.UpdateOnly<ClaimRecord>("abc")
            .Set(r => r.Attempt, 2);
        var withoutAssignment = SurrealWriter.UpdateOnly<ClaimRecord>("abc")
            .Where(r => r.Attempt == 1);

        withoutPredicate.Invoking(b => b.Build())
            .Should().Throw<InvalidOperationException>().WithMessage("*Where*");
        withoutAssignment.Invoking(b => b.Build())
            .Should().Throw<InvalidOperationException>().WithMessage("*Set or Unset*");
    }

    [Fact]
    public void Duplicate_or_protected_assignments_are_rejected()
    {
        var duplicate = SurrealWriter.UpdateOnly<ClaimRecord>("abc")
            .Where(r => r.Attempt == 1)
            .Set(r => r.Attempt, 2);

        duplicate.Invoking(b => b.Set(r => r.Attempt, 3))
            .Should().Throw<InvalidOperationException>().WithMessage("*already*");
        SurrealWriter.UpdateOnly<ClaimRecord>("abc")
            .Invoking(b => b.Set(r => r.Id, "other"))
            .Should().Throw<InvalidOperationException>().WithMessage("*id*");
        SurrealWriter.UpdateOnly<ClaimRecord>("abc")
            .Invoking(b => b.Set(r => r.CreatedAt, DateTimeOffset.UtcNow))
            .Should().Throw<InvalidOperationException>().WithMessage("*read-only*");
    }

    [Fact]
    public void Conditional_delete_is_typed_parameterized_and_predicate_required()
    {
        var query = SurrealWriter.DeleteOnly<ClaimRecord>("claim_record:abc")
            .Where(r => r.State == ClaimState.Claimed)
            .Where(r => r.ClaimKey == "claim:123")
            .Build();

        query.Sql.Should().Be(
            "DELETE ONLY type::record($_t, $_id) WHERE " +
            "(state = type::string($_w0_state)) AND " +
            "(claim_key = type::string($_w1_claim_key)) RETURN BEFORE");
        query.Params["_t"].Should().Be("claim_record");
        query.Params["_id"].Should().Be("abc");
        query.Params["_w0_state"].Should().Be("Claimed");
        query.Params["_w1_claim_key"].Should().Be("claim:123");
        query.Validate(strict: true);

        SurrealWriter.DeleteOnly<ClaimRecord>("abc")
            .Invoking(builder => builder.Build())
            .Should().Throw<InvalidOperationException>().WithMessage("*Where*");
    }

    [Fact]
    public void Null_and_required_field_removal_are_rejected()
    {
        var update = SurrealWriter.UpdateOnly<ClaimRecord>("abc")
            .Where(r => r.Attempt == 1);

        update.Invoking(builder => builder.Set<string?>(r => r.ClaimKey, null))
            .Should().Throw<ArgumentNullException>().WithMessage("*Unset*");
        update.Invoking(builder => builder.Unset(r => r.Kind))
            .Should().Throw<InvalidOperationException>().WithMessage("*required*");

        update.Unset(r => r.ClaimKey).Build().Sql
            .Should().Contain("claim_key = NONE");
        update.Unset(r => r.InferredOptional).Build().Sql
            .Should().Contain("inferred_optional = NONE");
        update.Invoking(builder => builder.Unset(r => r.InferredRequired))
            .Should().Throw<InvalidOperationException>().WithMessage("*required*");
    }

    [Fact]
    public void Mismatched_record_prefix_is_rejected_for_update_and_delete()
    {
        FluentActions.Invoking(() => SurrealWriter.UpdateOnly<ClaimRecord>("other:abc"))
            .Should().Throw<ArgumentException>().WithMessage("*other*claim_record*");
        FluentActions.Invoking(() => SurrealWriter.DeleteOnly<ClaimRecord>("other:abc"))
            .Should().Throw<ArgumentException>().WithMessage("*other*claim_record*");
    }

    [Fact]
    public void Column_name_override_is_used_by_predicates_and_assignments()
    {
        var query = SurrealWriter.UpdateOnly<ClaimRecord>("abc")
            .Where(r => r.CustomState == "pending")
            .Set(r => r.CustomState, "complete")
            .Build();

        query.Sql.Should().Contain("custom_state = type::string($_w0_custom_state)");
        query.Sql.Should().Contain("custom_state = type::string($_s0_custom_state)");
        query.Sql.Should().NotContain("customstate");
    }

    [Fact]
    public void Predicates_reject_non_persisted_members()
    {
        SurrealWriter.UpdateOnly<ClaimRecord>("abc")
            .Invoking(builder => builder.Where(r => r.SchemaName == "claim_record"))
            .Should().Throw<InvalidOperationException>().WithMessage("*not a persisted*field*");
        SurrealWriter.DeleteOnly<ClaimRecord>("abc")
            .Invoking(builder => builder.Where(r => r.Transient == "ignored"))
            .Should().Throw<InvalidOperationException>().WithMessage("*not a persisted*field*");
    }

    [Fact]
    public void Typed_record_ids_preserve_opaque_colons()
    {
        var id = new RecordId<ClaimRecord>("opaque:segment");

        var update = SurrealWriter.UpdateOnly(id)
            .Where(r => r.Attempt == 1)
            .Set(r => r.Attempt, 2)
            .Build();
        var delete = SurrealWriter.DeleteOnly(id)
            .Where(r => r.Attempt == 2)
            .Build();

        update.Params["_id"].Should().Be("opaque:segment");
        delete.Params["_id"].Should().Be("opaque:segment");
    }

    [Fact]
    public void Fluent_branches_are_immutable_and_independent()
    {
        var updateRoot = SurrealWriter.UpdateOnly<ClaimRecord>("abc")
            .Where(r => r.Attempt == 1);
        var attemptBranch = updateRoot.Set(r => r.Attempt, 2).Build();
        var amountBranch = updateRoot.Set(r => r.Amount, 3m).Build();

        attemptBranch.Sql.Should().Contain("attempt = ").And.NotContain("amount = ");
        amountBranch.Sql.Should().Contain("amount = ").And.NotContain("attempt = $_s");

        var deleteRoot = SurrealWriter.DeleteOnly<ClaimRecord>("abc");
        var stateBranch = deleteRoot.Where(r => r.State == ClaimState.Claimed).Build();
        var keyBranch = deleteRoot.Where(r => r.ClaimKey == "claim:123").Build();

        stateBranch.Sql.Should().Contain("state = ").And.NotContain("claim_key = ");
        keyBranch.Sql.Should().Contain("claim_key = ").And.NotContain("state = ");
    }

    [SurrealTable("account")]
    public sealed class AccountRecord : ISurrealRecord
    {
        public string SchemaName => "account";
        [Id] public string Id { get; set; } = string.Empty;
    }

    [SurrealTable("claim_record")]
    public sealed class ClaimRecord : ISurrealRecord
    {
        public string SchemaName => "claim_record";

        [Id, Column(Type = "string")]
        public string Id { get; set; } = string.Empty;

        [Column(Type = "string")]
        public string Kind { get; set; } = string.Empty;

        [References(typeof(AccountRecord))]
        public string OwnerId { get; set; } = string.Empty;

        [Column(Type = "option<string>")]
        public string? ClaimKey { get; set; }

        [References(typeof(AccountRecord), Optional = true)]
        public string? TargetId { get; set; }

        [Column(Type = "option<datetime>")]
        public DateTimeOffset? ClaimedAt { get; set; }

        [Column(Type = "option<datetime>")]
        public DateTimeOffset? CompletedAt { get; set; }

        [Column(Type = "int")]
        public int Attempt { get; set; }

        [Column(Type = "decimal")]
        public decimal Amount { get; set; }

        [Column(Type = "string")]
        public ClaimState State { get; set; }

        [Column(Name = "custom_state", Type = "string")]
        public string CustomState { get; set; } = string.Empty;

        [Column]
        public string? InferredOptional { get; set; }

        [Column]
        public string InferredRequired { get; set; } = string.Empty;

        [ReadOnly]
        public DateTimeOffset CreatedAt { get; set; }

        [NotMapped]
        public string Transient { get; set; } = string.Empty;
    }

    public enum ClaimState
    {
        Pending,
        Claimed,
    }
}
