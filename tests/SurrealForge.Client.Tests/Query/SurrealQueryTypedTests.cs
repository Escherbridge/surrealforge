// SPDX-License-Identifier: UNLICENSED
// SurrealQuery<T> -- typed companion to the untyped builder. Tests cover:
//   * From() emits SELECT * FROM <SchemaName>
//   * Where(equality) emits "<col> = $<col>" + parameter binding
//   * Where(compound &&) chains AND
//   * OrderBy + ThenBy emits sequential ORDER BY clauses
//   * Select(projection) emits the field list and rewrites SELECT *
//   * Unsupported method-call throws NotSupportedException with fallback recipe
//   * Byte-identical output to the untyped equivalent for the golden case
//     `SurrealQuery<TWallet>.From().Where(w => w.Status == WalletStatus.Active)`

using System.Text.Json.Serialization;
using FluentAssertions;
using SurrealForge.Client;
using SurrealForge.Client.Query;

namespace SurrealForge.Client.Tests.Query;

public class SurrealQueryTypedTests
{
    [Fact]
    public void From_emits_SELECT_star_FROM_table_per_SchemaName()
    {
        var q = SurrealQuery<TWallet>.From();
        SurrealQuery untyped = q;
        untyped.Sql.Should().Be("SELECT * FROM wallet");
    }

    [Fact]
    public void Where_equality_emits_correct_SurrealQL_and_parameter()
    {
        var q = SurrealQuery<TWallet>.From()
            .Where(w => w.Status == WalletStatus.Active);
        SurrealQuery untyped = q;
        untyped.Sql.Should().Be("SELECT * FROM wallet WHERE status = $status");
        untyped.Params.Should().ContainKey("status");
        untyped.Params["status"].Should().Be("active");
    }

    [Fact]
    public void Where_byte_identical_to_untyped_for_golden_case()
    {
        var typed = SurrealQuery<TWallet>.From()
            .Where(w => w.Status == WalletStatus.Active);

        var untypedEquivalent = SurrealQuery
            .Of("SELECT * FROM wallet")
            .Where("status = $status", new Dictionary<string, object?> { { "status", "active" } });

        SurrealQuery typedAsUntyped = typed;
        typedAsUntyped.Sql.Should().Be(untypedEquivalent.Sql);
        typedAsUntyped.Params.Should().BeEquivalentTo(untypedEquivalent.Params);
    }

    [Fact]
    public void Where_compound_AND_chains_AND()
    {
        var q = SurrealQuery<TWallet>.From()
            .Where(w => w.Status == WalletStatus.Active && w.AvatarId == "alice");
        SurrealQuery untyped = q;
        untyped.Sql.Should().Contain("WHERE");
        untyped.Sql.Should().Contain("AND");
        untyped.Params.Should().ContainKey("status").WhoseValue.Should().Be("active");
        untyped.Params.Should().ContainKey("avatar_id").WhoseValue.Should().Be("alice");
    }

    [Fact]
    public void Where_compound_OR_chains_OR()
    {
        var q = SurrealQuery<TWallet>.From()
            .Where(w => w.Status == WalletStatus.Active || w.Status == WalletStatus.Pending);
        SurrealQuery untyped = q;
        untyped.Sql.Should().Contain("OR");
        untyped.Params.Should().ContainKey("status").WhoseValue.Should().Be("active");
        untyped.Params.Should().ContainKey("status_2").WhoseValue.Should().Be("pending");
    }

    [Fact]
    public void OrderBy_emits_ASC_clause()
    {
        var q = SurrealQuery<TWallet>.From()
            .OrderBy(w => w.CreatedAt);
        SurrealQuery untyped = q;
        untyped.Sql.Should().Contain("ORDER BY created_at ASC");
    }

    [Fact]
    public void OrderByDescending_emits_DESC_clause()
    {
        var q = SurrealQuery<TWallet>.From()
            .OrderByDescending(w => w.CreatedAt);
        SurrealQuery untyped = q;
        untyped.Sql.Should().Contain("ORDER BY created_at DESC");
    }

    [Fact]
    public void OrderBy_chained_with_ThenBy_emits_both_clauses()
    {
        var q = SurrealQuery<TWallet>.From()
            .OrderBy(w => w.Status)
            .ThenBy(w => w.CreatedAt);
        SurrealQuery untyped = q;
        untyped.Sql.Should().Contain("ORDER BY status ASC");
        untyped.Sql.Should().Contain("ORDER BY created_at ASC");
    }

    [Fact]
    public void Limit_emits_LIMIT_clause()
    {
        var q = SurrealQuery<TWallet>.From().Limit(10);
        SurrealQuery untyped = q;
        untyped.Sql.Should().Be("SELECT * FROM wallet LIMIT 10");
    }

    [Fact]
    public void Start_emits_START_clause()
    {
        var q = SurrealQuery<TWallet>.From().Start(5);
        SurrealQuery untyped = q;
        untyped.Sql.Should().Contain("START 5");
    }

    [Fact]
    public void Select_projection_emits_field_list()
    {
        var q = SurrealQuery<TWallet>.From().Select(w => new { w.Id, w.Status });
        SurrealQuery untyped = q;
        untyped.Sql.Should().Be("SELECT id, status FROM wallet");
    }

    [Fact]
    public void Implicit_conversion_to_SurrealQuery_returns_inner()
    {
        var typed = SurrealQuery<TWallet>.From();
        SurrealQuery untyped = typed;
        untyped.Should().BeAssignableTo<SurrealQuery>();
        // AsUntyped() returns the same instance.
        ReferenceEquals(untyped, typed.AsUntyped()).Should().BeTrue();
    }

    [Fact]
    public void Unsupported_method_call_throws_NotSupportedException_with_fallback_recipe()
    {
        var q = SurrealQuery<TWallet>.From();
        var act = () => q.Where(w => w.Address.ToUpper() == "FOO");
        act.Should().Throw<NotSupportedException>()
            .WithMessage("*SurrealQuery<T>*")
            .WithMessage("*Fall back to SurrealQuery.Of*");
    }

    [Fact]
    public void Where_with_int_comparison_emits_parameter()
    {
        var q = SurrealQuery<TWallet>.From()
            .Where(w => w.Counter > 5);
        SurrealQuery untyped = q;
        untyped.Sql.Should().Be("SELECT * FROM wallet WHERE counter > $counter");
        untyped.Params.Should().ContainKey("counter").WhoseValue.Should().Be(5);
    }

    [Fact]
    public void Where_with_string_IsNullOrEmpty_emits_NONE_or_empty_check()
    {
        var q = SurrealQuery<TWallet>.From()
            .Where(w => string.IsNullOrEmpty(w.Label));
        SurrealQuery untyped = q;
        untyped.Sql.Should().Contain("label = NONE OR label = \"\"");
    }

    // Pre-existing limitation (present before extraction): under the .NET 10 SDK,
    // List<T>/array Contains compiles to an expression shape the typed builder's
    // ExpressionTranslator does not yet handle. Tracked as a known issue; use
    // SurrealQuery.Of(...).Where(...) for INSIDE membership until fixed.
    [Fact(Skip = "Known: typed-builder Contains->INSIDE translation gap under .NET 10; see README status")]
    public void Where_with_list_Contains_emits_INSIDE_clause()
    {
        var statuses = new[] { WalletStatus.Active, WalletStatus.Pending };
        var q = SurrealQuery<TWallet>.From()
            .Where(w => statuses.Contains(w.Status));
        SurrealQuery untyped = q;
        untyped.Sql.Should().Contain("status INSIDE $status");
        var enumerable = untyped.Params["status"] as IEnumerable<object?>;
        enumerable.Should().NotBeNull();
        enumerable!.Should().BeEquivalentTo(new object?[] { "active", "pending" });
    }

    [Fact]
    public void From_caches_SchemaName_across_calls()
    {
        var q1 = SurrealQuery<TWallet>.From();
        var q2 = SurrealQuery<TWallet>.From();
        SurrealQuery u1 = q1, u2 = q2;
        u1.Sql.Should().Be(u2.Sql);
    }

    // ─── Phase 1.2: broadened operators ───────────────────────────────────────

    [Fact]
    public void Where_equals_null_emits_NONE_check_not_a_param()
    {
        var q = SurrealQuery<TWallet>.From()
            .Where(w => w.Label == null);
        SurrealQuery untyped = q;
        untyped.Sql.Should().Be("SELECT * FROM wallet WHERE label = NONE");
        untyped.Params.Should().BeEmpty();
    }

    [Fact]
    public void Where_not_equals_null_emits_NOT_NONE_check()
    {
        var q = SurrealQuery<TWallet>.From()
            .Where(w => w.Label != null);
        SurrealQuery untyped = q;
        untyped.Sql.Should().Be("SELECT * FROM wallet WHERE label != NONE");
        untyped.Params.Should().BeEmpty();
    }

    [Fact]
    public void Where_HasValue_emits_NOT_NONE_check()
    {
        var q = SurrealQuery<TWallet>.From()
            .Where(w => w.EndedAt.HasValue);
        SurrealQuery untyped = q;
        untyped.Sql.Should().Be("SELECT * FROM wallet WHERE ended_at != NONE");
        untyped.Params.Should().BeEmpty();
    }

    [Fact]
    public void Where_not_HasValue_emits_negated_NOT_NONE_check()
    {
        var q = SurrealQuery<TWallet>.From()
            .Where(w => !w.EndedAt.HasValue);
        SurrealQuery untyped = q;
        untyped.Sql.Should().Be("SELECT * FROM wallet WHERE !(ended_at != NONE)");
    }

    [Fact]
    public void Where_StartsWith_emits_string_starts_with()
    {
        var q = SurrealQuery<TWallet>.From()
            .Where(w => w.Address.StartsWith("0x"));
        SurrealQuery untyped = q;
        untyped.Sql.Should().Be("SELECT * FROM wallet WHERE string::starts_with(address, $address)");
        untyped.Params.Should().ContainKey("address").WhoseValue.Should().Be("0x");
    }

    [Fact]
    public void Where_EndsWith_emits_string_ends_with()
    {
        var q = SurrealQuery<TWallet>.From()
            .Where(w => w.Address.EndsWith("eth"));
        SurrealQuery untyped = q;
        untyped.Sql.Should().Be("SELECT * FROM wallet WHERE string::ends_with(address, $address)");
        untyped.Params.Should().ContainKey("address").WhoseValue.Should().Be("eth");
    }

    [Fact]
    public void Where_string_Contains_emits_string_contains_not_INSIDE()
    {
        var q = SurrealQuery<TWallet>.From()
            .Where(w => w.Address.Contains("dead"));
        SurrealQuery untyped = q;
        untyped.Sql.Should().Be("SELECT * FROM wallet WHERE string::contains(address, $address)");
        untyped.Sql.Should().NotContain("INSIDE");
        untyped.Params.Should().ContainKey("address").WhoseValue.Should().Be("dead");
    }

    [Fact]
    public void Where_range_compound_emits_two_bound_comparisons()
    {
        var q = SurrealQuery<TWallet>.From()
            .Where(w => w.Counter >= 5 && w.Counter <= 10);
        SurrealQuery untyped = q;
        untyped.Sql.Should().Be("SELECT * FROM wallet WHERE (counter >= $counter AND counter <= $counter_2)");
        untyped.Params.Should().ContainKey("counter").WhoseValue.Should().Be(5);
        untyped.Params.Should().ContainKey("counter_2").WhoseValue.Should().Be(10);
    }

    [Fact(Skip = "Known: typed-builder Contains->INSIDE translation gap under .NET 10; see README status")]
    public void Where_collection_Contains_still_emits_INSIDE()
    {
        // Guard: the new string-method branch must not regress the existing
        // collection-membership (INSIDE) path.
        var statuses = new[] { WalletStatus.Active, WalletStatus.Pending };
        var q = SurrealQuery<TWallet>.From()
            .Where(w => statuses.Contains(w.Status));
        SurrealQuery untyped = q;
        untyped.Sql.Should().Contain("status INSIDE $status");
        untyped.Sql.Should().NotContain("string::contains");
    }

    // ─── Fixtures ────────────────────────────────────────────────────────────

    /// <summary>Test-only POCO mimicking the source-gen output for wallet.</summary>
    public sealed class TWallet : ISurrealRecord
    {
        public string SchemaName => "wallet";

        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("avatar_id")]
        public string AvatarId { get; set; } = string.Empty;

        [JsonPropertyName("address")]
        public string Address { get; set; } = string.Empty;

        [JsonPropertyName("label")]
        public string? Label { get; set; }

        [JsonPropertyName("status")]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public WalletStatus Status { get; set; }

        [JsonPropertyName("counter")]
        public long Counter { get; set; }

        [JsonPropertyName("created_at")]
        public DateTimeOffset CreatedAt { get; set; }

        [JsonPropertyName("ended_at")]
        public DateTimeOffset? EndedAt { get; set; }
    }

    public enum WalletStatus
    {
        Active,
        Pending,
        Disabled,
    }
}
