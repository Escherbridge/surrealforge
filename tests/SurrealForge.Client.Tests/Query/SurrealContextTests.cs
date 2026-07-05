// SPDX-License-Identifier: UNLICENSED
// SurrealContext unit-of-work (surreal-linq-graph-query Phase 3). Covers:
//   * Set<T>() returns a deferred SurrealQueryable<T>
//   * Add/Update/Remove emit CREATE/UPSERT/DELETE with bare-id type::record()
//   * SaveChangesAsync flushes inside one BEGIN..COMMIT (commit called once,
//     tracker cleared, affected count returned)
//   * identity-map dedup: tracking the same id twice collapses to one entry,
//     latest intent wins
//   * SaveChangesAsync with nothing pending is a no-op (no transaction)

using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using SurrealForge.Client;
using SurrealForge.Client.Connection;
using SurrealForge.Client.Query;
using SurrealForge.Client.Schema;
using SurrealForge.Client.Transaction;
using Xunit;

namespace SurrealForge.Client.Tests.Query;

public class SurrealContextTests
{
    private sealed class FakeTransaction : ISurrealTransaction
    {
        public int CommitCalls { get; private set; }
        public bool IsCommitted { get; private set; }
        public bool IsDisposed { get; private set; }
        public Task CommitAsync(CancellationToken ct = default)
        {
            CommitCalls++;
            IsCommitted = true;
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() { IsDisposed = true; return default; }
    }

    private sealed class FakeConnection : ISurrealConnection
    {
        public readonly FakeTransaction Txn = new();
        public int BeginCalls { get; private set; }
        public Task UseAsync(string ns, string db, CancellationToken ct = default) => Task.CompletedTask;
        public Task<SurrealResponse> ExecuteRawAsync(
            string sql, object? parameters = null, CancellationToken ct = default)
            => Task.FromResult(SurrealResponse.BufferedAck());
        public Task<ISurrealTransaction> BeginTransactionAsync(CancellationToken ct = default)
        {
            BeginCalls++;
            return Task.FromResult<ISurrealTransaction>(Txn);
        }
        public ValueTask DisposeAsync() => default;
        public void Dispose() { }
    }

    // Records the statements the context emits during SaveChanges.
    private sealed class RecordingExecutor : ISurrealExecutor
    {
        public readonly List<SurrealQuery> Executed = new();
        public Task<IReadOnlyList<T>> QueryAsync<T>(SurrealQuery q, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<T>>(new List<T>());
        public Task<T?> QuerySingleAsync<T>(SurrealQuery q, CancellationToken ct = default) where T : class
            => Task.FromResult<T?>(null);
        public Task<SurrealResponse> ExecuteAsync(SurrealQuery q, CancellationToken ct = default)
        {
            q.Validate(strict: false);
            Executed.Add(q);
            return Task.FromResult(SurrealResponse.BufferedAck());
        }
    }

    private static (SurrealContext ctx, FakeConnection conn, RecordingExecutor exec) NewContext()
    {
        var conn = new FakeConnection();
        var exec = new RecordingExecutor();
        return (new SurrealContext(conn, exec), conn, exec);
    }

    [Fact]
    public void Set_returns_deferred_queryable()
    {
        var (ctx, _, _) = NewContext();
        ctx.Set<TWallet>().Should().BeOfType<SurrealQueryable<TWallet>>();
    }

    [Fact]
    public async Task SaveChanges_emits_CREATE_for_Added()
    {
        var (ctx, _, exec) = NewContext();
        ctx.Add(new TWallet { Id = "abc", Status = WalletStatus.Active });

        var n = await ctx.SaveChangesAsync();

        n.Should().Be(1);
        exec.Executed.Should().HaveCount(1);
        // SET-based CREATE (coercion-safe), not CONTENT $body. The id addresses
        // the record (not a SET column); status is a string-valued column so it
        // is wrapped in type::string() to defeat record coercion.
        exec.Executed[0].Sql.Should().Be(
            "CREATE type::record($_t, $_id) SET status = type::string($_f_status) RETURN AFTER");
        exec.Executed[0].Params["_t"].Should().Be("wallet");
        exec.Executed[0].Params["_id"].Should().Be("abc");
        // Enum bound as its NAME (PascalCase) — matches the schema INSIDE sets;
        // a SET-bound param wouldn't pick up a per-property [JsonConverter].
        exec.Executed[0].Params["_f_status"].Should().Be("Active");
    }

    [Fact]
    public async Task SaveChanges_emits_UPSERT_for_Modified_and_DELETE_for_Deleted()
    {
        var (ctx, _, exec) = NewContext();
        ctx.Update(new TWallet { Id = "u1" });
        ctx.Remove(new TWallet { Id = "d1" });

        await ctx.SaveChangesAsync();

        exec.Executed.Select(e => e.Sql).Should().Contain(s => s.StartsWith("UPSERT type::record($_t, $_id) SET "));
        exec.Executed.Select(e => e.Sql).Should().Contain("DELETE type::record($_t, $_id)");
    }

    [Fact]
    public async Task SaveChanges_strips_table_prefix_from_id()
    {
        var (ctx, _, exec) = NewContext();
        ctx.Add(new TWallet { Id = "wallet:xyz" });   // link form
        await ctx.SaveChangesAsync();
        exec.Executed[0].Params["_id"].Should().Be("xyz");
    }

    [Fact]
    public async Task SaveChanges_flushes_in_one_transaction_and_clears_tracker()
    {
        var (ctx, conn, _) = NewContext();
        ctx.Add(new TWallet { Id = "a" });
        ctx.Add(new TWallet { Id = "b" });

        var n = await ctx.SaveChangesAsync();

        n.Should().Be(2);
        conn.BeginCalls.Should().Be(1, "all pending writes share ONE transaction");
        conn.Txn.CommitCalls.Should().Be(1);
        conn.Txn.IsDisposed.Should().BeTrue("the txn is awaited-using");
        ctx.ChangeTracker.Entries.Should().BeEmpty("a successful save clears the tracker");
    }

    [Fact]
    public async Task Identity_map_dedups_same_id_latest_intent_wins()
    {
        var (ctx, _, exec) = NewContext();
        var w = new TWallet { Id = "same" };
        ctx.Add(w);      // Added
        ctx.Remove(w);   // collapses to Deleted on the same entry

        ctx.ChangeTracker.Entries.Should().HaveCount(1);
        await ctx.SaveChangesAsync();

        exec.Executed.Should().HaveCount(1);
        exec.Executed[0].Sql.Should().StartWith("DELETE");
    }

    [Fact]
    public async Task SaveChanges_with_nothing_pending_is_a_no_op()
    {
        var (ctx, conn, exec) = NewContext();
        var n = await ctx.SaveChangesAsync();
        n.Should().Be(0);
        conn.BeginCalls.Should().Be(0, "no transaction opens when there is nothing to flush");
        exec.Executed.Should().BeEmpty();
    }

    [Fact]
    public void Tracking_empty_id_throws()
    {
        var (ctx, _, _) = NewContext();
        var act = () => ctx.Add(new TWallet { Id = "" });
        act.Should().Throw<System.InvalidOperationException>().WithMessage("*empty*Id*");
    }

    // ─── Fixture ────────────────────────────────────────────────────────────

    public sealed class TWallet : ISurrealRecord
    {
        public string SchemaName => "wallet";
        [Id] [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("status")]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public WalletStatus Status { get; set; }
    }

    public enum WalletStatus { Active, Pending, Disabled }
}
