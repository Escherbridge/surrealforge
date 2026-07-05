// SPDX-License-Identifier: UNLICENSED
// Deferred IQueryable<T> surface (surreal-linq-graph-query Phase 2). Covers:
//   * deferral — no round-trip until a materializer runs
//   * composability — chained Where ANDs into one statement
//   * single-statement emission of the folded Where/OrderBy/Skip/Take/Select
//   * each materializer maps to the right SurrealQL (LIMIT 1/2, count() GROUP ALL)
//   * unsupported operator throws NotSupportedException with the fall-back recipe

using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using SurrealForge.Client;
using SurrealForge.Client.Query;
using Xunit;

namespace SurrealForge.Client.Tests.Query;

public class SurrealQueryableTests
{
    // Recording fake: captures every query it is asked to run so tests can
    // assert both the COUNT of round-trips (deferral) and the emitted SurrealQL.
    private sealed class RecordingExecutor : ISurrealExecutor
    {
        public readonly List<SurrealQuery> Queries = new();
        public int QueryAsyncCalls { get; private set; }

        // Rows handed back per QueryAsync<T> call (FIFO). Each entry is a row
        // count for the aggregate path or concrete rows for the list path; we
        // resolve by the requested element type T at call time so tests need
        // not name the materializer's internal projection type.
        private readonly Queue<object> _results = new();
        public void EnqueueResult(object rows) => _results.Enqueue(rows);
        // For the count path: the next CountAsync returns this value.
        private long? _nextCount;
        public void EnqueueCount(long n) => _nextCount = n;

        public Task<IReadOnlyList<T>> QueryAsync<T>(SurrealQuery query, CancellationToken ct = default)
        {
            QueryAsyncCalls++;
            Queries.Add(query);
            query.Validate(strict: false); // exercise the validator on the folded SQL

            // Aggregate path: the materializer deserializes into an internal
            // count-projection type with a single long. Detect it structurally
            // (a parameterless type with one settable long property) and inject
            // the queued count.
            if (_nextCount is { } c && TryMakeCountRow<T>(c, out var countRow))
            {
                _nextCount = null;
                return Task.FromResult<IReadOnlyList<T>>(new List<T> { countRow });
            }

            IReadOnlyList<T> rows = _results.Count > 0 ? (IReadOnlyList<T>)_results.Dequeue() : new List<T>();
            return Task.FromResult(rows);
        }

        private static bool TryMakeCountRow<T>(long value, out T row)
        {
            row = default!;
            var type = typeof(T);
            var prop = type.GetProperties()
                .FirstOrDefault(p => p.CanWrite && p.PropertyType == typeof(long));
            if (prop is null || type.GetConstructor(System.Type.EmptyTypes) is null)
                return false;
            var instance = System.Activator.CreateInstance<T>();
            prop.SetValue(instance, value);
            row = instance;
            return true;
        }

        public Task<T?> QuerySingleAsync<T>(SurrealQuery query, CancellationToken ct = default) where T : class
            => Task.FromResult<T?>(null);

        public Task<SurrealResponse> ExecuteAsync(SurrealQuery query, CancellationToken ct = default)
            => Task.FromResult(SurrealResponse.BufferedAck());
    }

    private static (SurrealQueryable<TWallet> q, RecordingExecutor exec) NewQueryable()
    {
        var exec = new RecordingExecutor();
        var provider = new SurrealQueryProvider(exec);
        return (new SurrealQueryable<TWallet>(provider), exec);
    }

    [Fact]
    public void Composing_operators_does_NOT_round_trip_until_materialized()
    {
        var (q, exec) = NewQueryable();

        // Build a chain but never materialize.
        var composed = q.Where(w => w.Status == WalletStatus.Active)
                        .OrderBy(w => w.CreatedAt)
                        .Take(5);

        exec.QueryAsyncCalls.Should().Be(0, "composition is deferred");
        composed.Should().BeAssignableTo<IQueryable<TWallet>>();
    }

    [Fact]
    public async Task ToListAsync_folds_whole_chain_into_one_statement()
    {
        var (q, exec) = NewQueryable();

        await q.Where(w => w.Status == WalletStatus.Active)
               .OrderByDescending(w => w.CreatedAt)
               .Skip(10)
               .Take(5)
               .ToListAsync();

        exec.QueryAsyncCalls.Should().Be(1, "the whole chain materializes in ONE round-trip");
        var sql = exec.Queries[0].Sql;
        sql.Should().Be(
            "SELECT * FROM wallet WHERE status = $status ORDER BY created_at DESC START 10 LIMIT 5");
        exec.Queries[0].Params.Should().ContainKey("status").WhoseValue.Should().Be("active");
    }

    [Fact]
    public async Task Chained_Where_ANDs_into_one_predicate()
    {
        var (q, exec) = NewQueryable();

        await q.Where(w => w.Status == WalletStatus.Active)
               .Where(w => w.AvatarId == "alice")
               .ToListAsync();

        var sql = exec.Queries[0].Sql;
        sql.Should().Contain("status = $status");
        sql.Should().Contain("avatar_id = $avatar_id");
        sql.Should().Contain("AND");
        exec.Queries[0].Params.Should().ContainKey("status");
        exec.Queries[0].Params.Should().ContainKey("avatar_id");
    }

    [Fact]
    public async Task FirstOrDefaultAsync_appends_LIMIT_1_and_returns_first()
    {
        var (q, exec) = NewQueryable();
        exec.EnqueueResult(new List<TWallet> { new() { Id = "wallet:1" } });

        var first = await q.Where(w => w.Status == WalletStatus.Active).FirstOrDefaultAsync();

        first.Should().NotBeNull();
        first!.Id.Should().Be("wallet:1");
        exec.Queries[0].Sql.Should().EndWith("LIMIT 1");
    }

    [Fact]
    public async Task FirstOrDefaultAsync_returns_default_on_empty()
    {
        var (q, exec) = NewQueryable();
        var first = await q.FirstOrDefaultAsync();
        first.Should().BeNull();
        exec.Queries[0].Sql.Should().EndWith("LIMIT 1");
    }

    [Fact]
    public async Task SingleOrDefaultAsync_limits_to_2_and_throws_on_over_count()
    {
        var (q, exec) = NewQueryable();
        exec.EnqueueResult(new List<TWallet> { new() { Id = "wallet:1" }, new() { Id = "wallet:2" } });

        var act = async () => await q.SingleOrDefaultAsync();

        await act.Should().ThrowAsync<System.InvalidOperationException>()
            .WithMessage("*more than one*");
        exec.Queries[0].Sql.Should().EndWith("LIMIT 2");
    }

    [Fact]
    public async Task CountAsync_rewrites_to_count_group_all_and_preserves_predicate()
    {
        var (q, exec) = NewQueryable();
        exec.EnqueueCount(7);

        var n = await q.Where(w => w.Status == WalletStatus.Active)
                       .OrderBy(w => w.CreatedAt)   // dropped under aggregate
                       .Take(3)                     // dropped under aggregate
                       .CountAsync();

        n.Should().Be(7);
        var sql = exec.Queries[0].Sql;
        sql.Should().Be("SELECT count() AS c FROM wallet WHERE status = $status GROUP ALL");
        sql.Should().NotContain("ORDER BY");
        sql.Should().NotContain("LIMIT");
        exec.Queries[0].Params.Should().ContainKey("status").WhoseValue.Should().Be("active");
    }

    [Fact]
    public async Task AnyAsync_true_when_count_positive()
    {
        var (q, exec) = NewQueryable();
        exec.EnqueueCount(1);
        (await q.AnyAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task AnyAsync_false_when_count_zero()
    {
        var (q, exec) = NewQueryable();
        exec.EnqueueCount(0);
        (await q.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task SelectFields_projection_rewrites_field_list_keeping_T()
    {
        // Column projection keeps the element type T (SurrealDB returns rows
        // with only the projected fields populated). The anonymous-shape
        // Queryable.Select that changes the element type is out of scope —
        // the deferred surface stays ISurrealRecord-typed end to end.
        var (q, exec) = NewQueryable();
        await q.SelectFields(w => new { w.Id, w.Status }).ToListAsync();
        exec.Queries[0].Sql.Should().Be("SELECT id, status FROM wallet");
    }

    [Fact]
    public async Task Unsupported_operator_throws_with_fallback_recipe()
    {
        var (q, _) = NewQueryable();
        // Reverse() is not a routed operator.
        var act = async () => await q.Reverse().ToListAsync();
        await act.Should().ThrowAsync<System.NotSupportedException>()
            .WithMessage("*Fall back to SurrealQuery*");
    }

    // ─── Fixtures ──────────────────────────────────────────────────────────

    public sealed class TWallet : ISurrealRecord
    {
        public string SchemaName => "wallet";

        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("avatar_id")] public string AvatarId { get; set; } = string.Empty;
        [JsonPropertyName("status")]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public WalletStatus Status { get; set; }
        [JsonPropertyName("created_at")] public System.DateTimeOffset CreatedAt { get; set; }
    }

    public enum WalletStatus { Active, Pending, Disabled }
}
