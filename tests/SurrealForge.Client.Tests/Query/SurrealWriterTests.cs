// SPDX-License-Identifier: UNLICENSED
// SurrealWriter — the package's coercion-safe SET-based write path. Covers:
//   * CREATE/UPSERT emit `SET col = …` per field (not CONTENT $body)
//   * the [Id] column addresses the record and is NOT a SET assignment
//   * string-valued NON-record columns are wrapped in type::string()
//     (defeats SurrealDB-3.x `table:id` record coercion — e.g. "ASA:123")
//   * record/FK columns ([References] / Column Type record<>) are NOT wrapped
//   * null values are OMITTED (absent => NONE; 3.x rejects explicit null)
//   * non-string scalars bind as-is

using FluentAssertions;
using SurrealForge.Client;
using SurrealForge.Client.Query;
using SurrealForge.Client.Schema;
using Xunit;

namespace SurrealForge.Client.Tests.Query;

public class SurrealWriterTests
{
    [Fact]
    public void Create_emits_SET_assignments_not_content()
    {
        var q = SurrealWriter.Create(new TBridge { Id = "b1", SourceTokenId = "ASA:123" });
        q.Sql.Should().StartWith("CREATE type::record($_t, $_id) SET ");
        q.Sql.Should().EndWith(" RETURN AFTER");
        q.Sql.Should().NotContain("CONTENT");
    }

    [Fact]
    public void Id_is_addressed_not_set()
    {
        var q = SurrealWriter.Create(new TBridge { Id = "b1", SourceTokenId = "x" });
        // The id is not assigned as a SET column (it addresses the record).
        q.Sql.Should().NotContain("SET id = ");
        q.Sql.Should().NotContain(", id = ");
        q.Params.Should().NotContainKey("_f_id");
        q.Params["_t"].Should().Be("bridge_tx");
        q.Params["_id"].Should().Be("b1");
    }

    [Fact]
    public void String_column_value_is_wrapped_in_type_string()
    {
        // ASA:123 looks like a record id; type::string() keeps it a string.
        var q = SurrealWriter.Create(new TBridge { Id = "b1", SourceTokenId = "ASA:123" });
        q.Sql.Should().Contain("source_token_id = type::string($_f_source_token_id)");
        q.Params["_f_source_token_id"].Should().Be("ASA:123");
    }

    [Fact]
    public void Record_fk_column_is_NOT_wrapped()
    {
        // avatar_id is a [References] FK -> record<avatar>; must bind raw so the
        // record coercion (which is DESIRED here) happens.
        var q = SurrealWriter.Create(new TBridge { Id = "b1", AvatarId = "avatar:abc" });
        q.Sql.Should().Contain("avatar_id = $_f_avatar_id");
        q.Sql.Should().NotContain("type::string($_f_avatar_id)");
    }

    [Fact]
    public void Null_values_are_omitted()
    {
        // CompletedNote is null -> omitted entirely (NONE, not explicit null).
        var q = SurrealWriter.Create(new TBridge { Id = "b1", SourceTokenId = "t", CompletedNote = null });
        q.Sql.Should().NotContain("completed_note");
        q.Params.Should().NotContainKey("_f_completed_note");
    }

    [Fact]
    public void Non_string_scalar_binds_as_is()
    {
        var q = SurrealWriter.Create(new TBridge { Id = "b1", SourceTokenId = "t", Attempt = 3 });
        q.Sql.Should().Contain("attempt = $_f_attempt");
        q.Sql.Should().NotContain("type::string($_f_attempt)");
        q.Params["_f_attempt"].Should().Be(3);
    }

    [Fact]
    public void Upsert_uses_UPSERT_verb()
    {
        var q = SurrealWriter.Upsert(new TBridge { Id = "b1", SourceTokenId = "t" });
        q.Sql.Should().StartWith("UPSERT type::record($_t, $_id) SET ");
    }

    // ─── Fixture ────────────────────────────────────────────────────────────

    [SurrealTable("avatar")]
    public sealed class TAvatar : ISurrealRecord
    {
        public string SchemaName => "avatar";
        [Id] public string Id { get; set; } = "";
    }

    [SurrealTable("bridge_tx")]
    public sealed class TBridge : ISurrealRecord
    {
        public string SchemaName => "bridge_tx";

        [Id] [Column(Order = 1, Type = "string")] public string Id { get; set; } = "";
        // Genuine string column whose value can look like a record id.
        [Column(Order = 2, Type = "string")] public string SourceTokenId { get; set; } = "";
        // FK -> record<avatar>: must NOT be type::string-wrapped.
        [Column(Order = 3)] [References(typeof(TAvatar))] public string AvatarId { get; set; } = "";
        [Column(Order = 4, Type = "int")] public int Attempt { get; set; }
        [Column(Order = 5, Type = "option<string>")] public string? CompletedNote { get; set; }
    }
}
