// SPDX-License-Identifier: UNLICENSED
// Typed graph traversal (surreal-linq-graph-query Phase 4). Asserts the arrow
// path emit:
//   Key(id)                                  -> SELECT * FROM type::record($_t,$_id)
//   .Traverse(Out<Edge>().To<Target>())      -> SELECT VALUE ->edge->target.* FROM ONLY …
//   .Traverse(In<Edge>().From<Source>())     -> SELECT VALUE <-edge<-source.* FROM ONLY …
//   .CountVia(Out<Edge>().To<Target>())      -> SELECT VALUE count(->edge->target) FROM ONLY …
// plus the direction guards (To on In throws, From on Out throws).

using System.Text.Json.Serialization;
using FluentAssertions;
using SurrealForge.Client;
using SurrealForge.Client.Query;
using Xunit;

namespace SurrealForge.Client.Tests.Query;

public class SurrealGraphTraversalTests
{
    [Fact]
    public void Key_anchors_to_a_single_record_with_bound_params()
    {
        var q = SurrealQuery<TRun>.Key("abc");
        SurrealQuery u = q;
        u.Sql.Should().Be("SELECT * FROM type::record($_t, $_id)");
        u.Params["_t"].Should().Be("quest_run");
        u.Params["_id"].Should().Be("abc");
    }

    [Fact]
    public void Key_strips_table_prefix()
    {
        SurrealQuery u = SurrealQuery<TRun>.Key("quest_run:abc");
        u.Params["_id"].Should().Be("abc");
    }

    [Fact]
    public void Traverse_outgoing_emits_forward_arrow_path_dereferenced()
    {
        var q = SurrealQuery<TRun>
            .Key("abc")
            .Traverse<TRun>(r => r.Out<TForkedFrom>().To<TRun>());
        SurrealQuery u = q;
        u.Sql.Should().Be(
            "SELECT VALUE ->forked_from->quest_run.* FROM ONLY type::record($_t, $_id)");
        u.Params["_id"].Should().Be("abc");
    }

    [Fact]
    public void Traverse_incoming_emits_backward_arrow_path()
    {
        var q = SurrealQuery<TRun>
            .Key("abc")
            .Traverse<TRun>(r => r.In<TForkedFrom>().From<TRun>());
        SurrealQuery u = q;
        u.Sql.Should().Be(
            "SELECT VALUE <-forked_from<-quest_run.* FROM ONLY type::record($_t, $_id)");
    }

    [Fact]
    public void CountVia_emits_count_over_arrow_path()
    {
        var q = SurrealQuery<THolon>
            .Key("h1")
            .CountVia(h => h.Out<TMember>().To<THolon>());
        q.Sql.Should().Be(
            "SELECT VALUE count(->member->holon) FROM ONLY type::record($_t, $_id)");
        q.Params["_t"].Should().Be("holon");
    }

    [Fact]
    public void Out_hop_closed_with_From_throws()
    {
        var act = () => SurrealQuery<TRun>.Key("abc")
            .Traverse<TRun>(r => r.Out<TForkedFrom>().From<TRun>());
        act.Should().Throw<System.InvalidOperationException>().WithMessage("*.To<T>()*");
    }

    [Fact]
    public void In_hop_closed_with_To_throws()
    {
        var act = () => SurrealQuery<TRun>.Key("abc")
            .Traverse<TRun>(r => r.In<TForkedFrom>().To<TRun>());
        act.Should().Throw<System.InvalidOperationException>().WithMessage("*.From<T>()*");
    }

    // ─── Fixtures ──────────────────────────────────────────────────────────

    public sealed class TRun : ISurrealRecord
    {
        public string SchemaName => "quest_run";
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    }

    public sealed class THolon : ISurrealRecord
    {
        public string SchemaName => "holon";
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    }

    public sealed class TForkedFrom : ISurrealRecord
    {
        public string SchemaName => "forked_from";
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    }

    public sealed class TMember : ISurrealRecord
    {
        public string SchemaName => "member";
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    }
}
