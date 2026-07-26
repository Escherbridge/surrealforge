// SPDX-License-Identifier: MIT

using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using FluentAssertions;
using SurrealForge.Client;
using SurrealForge.Client.Connection;
using SurrealForge.Client.Query;
using SurrealForge.Client.Schema;

namespace SurrealForge.Client.IntegrationTests;

/// <summary>
/// HIGH#3 — round-trip the homebake wire shape against a live
/// SurrealDB 3.x instance. When the database is not
/// available (sandbox without podman/docker) every test gracefully
/// early-returns; the existing pass-off gate's section-9 contract is mirrored
/// here so this suite stays green either way.
/// </summary>
[Collection("LiveSurrealDb")]
public class LiveHttpRoundTripTests
{
    private readonly LiveSurrealDbCollectionFixture _fx;

    public LiveHttpRoundTripTests(LiveSurrealDbCollectionFixture fx) => _fx = fx;

    // ─── Helpers ────────────────────────────────────────────────────────────

    private SurrealConnectionOptions MakeOptions(string? db = null) => new()
    {
        Endpoint = _fx.Endpoint,
        Namespace = _fx.Namespace,
        Database = db ?? _fx.Database,
        User = _fx.User,
        Password = _fx.Password,
        MaxRetries = 1,
    };

    /// <summary>
    /// Skip the test (by early-return) when the live container isn't available.
    /// Mirrors <c>IntegrationTestBase.IsSurrealDbAvailableAsync</c> — we don't
    /// want missing podman/docker to colour the pass-off gate red.
    /// </summary>
    private bool TrySkip()
    {
        if (_fx.SurrealAvailable) return false;
        if (string.Equals(
            Environment.GetEnvironmentVariable("SURREALFORGE_REQUIRE_LIVE"),
            "1",
            StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Live SurrealDB is required for this verification run: {_fx.SkipReason}");
        }
        // Use the console so the skip reason shows in the test output.
        Console.WriteLine($"[SKIP] LiveSurrealDb unavailable: {_fx.SkipReason}");
        return true;
    }

    // ─── Tests ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Info_for_db_returns_ok()
    {
        if (TrySkip()) return;

        await using var conn = new HttpSurrealConnection(new HttpClientHandler(), MakeOptions());
        var resp = await conn.ExecuteRawAsync("INFO FOR DB");
        resp.Count.Should().Be(1);
        resp[0].IsOk.Should().BeTrue();
    }

    [Fact]
    public async Task Parameterized_select_round_trips()
    {
        if (TrySkip()) return;

        var db = $"client_int_{Guid.NewGuid():N}"[..30];
        _fx.EnsureDatabase(db);
        await using var conn = new HttpSurrealConnection(new HttpClientHandler(), MakeOptions(db));

        // Set up a record we can select back.
        var createResp = await conn.ExecuteRawAsync(
            "CREATE wallet:abc CONTENT { id: 'abc', amount: 100 }");
        createResp.EnsureAllOk();

        var selectResp = await conn.ExecuteRawAsync(
            "SELECT * FROM wallet WHERE id = $id",
            new { id = "wallet:abc" });
        selectResp.Count.Should().Be(1);
        selectResp[0].IsOk.Should().BeTrue();
        selectResp.GetValues<System.Text.Json.JsonElement>(0).Should().HaveCount(1,
            "the parameterized SELECT must match the record created above");
    }

    [Fact]
    public async Task Combine_three_statements_returns_three_results()
    {
        if (TrySkip()) return;

        var db = $"client_int_{Guid.NewGuid():N}"[..30];
        _fx.EnsureDatabase(db);
        await using var conn = new HttpSurrealConnection(new HttpClientHandler(), MakeOptions(db));

        var q = SurrealQuery.Combine(
            SurrealQuery.Of("CREATE wallet:def CONTENT { id: 'def', amount: 50 }"),
            SurrealQuery.Of("SELECT * FROM wallet"),
            SurrealQuery.Of("DELETE wallet:def"));

        var resp = await conn.ExecuteRawAsync(q.Build());
        resp.Count.Should().Be(3,
            "Combine of three statements must yield three per-statement slots — no silent swallow");
        resp.All(r => r.IsOk).Should().BeTrue();
    }

    [Fact]
    public async Task Transaction_commit_persists()
    {
        if (TrySkip()) return;

        var db = $"client_int_{Guid.NewGuid():N}"[..30];
        _fx.EnsureDatabase(db);
        await using (var writer = new HttpSurrealConnection(new HttpClientHandler(), MakeOptions(db)))
        {
            await using var txn = await writer.BeginTransactionAsync();
            var create = await writer.ExecuteRawAsync(
                "CREATE wallet:ghi CONTENT { id: 'ghi', amount: 1 }");
            create.EnsureAllOk();
            await txn.CommitAsync();
        }

        // Reader connection — separate instance, same DB scope.
        await using var reader = new HttpSurrealConnection(new HttpClientHandler(), MakeOptions(db));
        var resp = await reader.ExecuteRawAsync(
            "SELECT * FROM wallet WHERE id = $id",
            new { id = "wallet:ghi" });
        resp[0].IsOk.Should().BeTrue();
        resp.GetValues<System.Text.Json.JsonElement>(0).Should().HaveCountGreaterOrEqualTo(1,
            "commit must persist the CREATE so a separate connection sees it");
    }

    [Fact]
    public async Task Transaction_cancel_does_not_persist()
    {
        if (TrySkip()) return;

        var db = $"client_int_{Guid.NewGuid():N}"[..30];
        _fx.EnsureDatabase(db);

        // Define the table up front so the rollback assertion below is about
        // ROW absence, not table absence (SurrealDB 3.x errors on SELECT from an
        // undefined table rather than returning an empty result).
        await using (var setup = new HttpSurrealConnection(new HttpClientHandler(), MakeOptions(db)))
        {
            (await setup.ExecuteRawAsync("DEFINE TABLE IF NOT EXISTS wallet SCHEMALESS;")).EnsureAllOk();
        }

        await using (var writer = new HttpSurrealConnection(new HttpClientHandler(), MakeOptions(db)))
        {
            // Open the txn and CREATE — but dispose without committing. The
            // buffered statements are discarded on dispose (nothing was sent to
            // the server), so the row never reaches the database.
            await using var txn = await writer.BeginTransactionAsync();
            var create = await writer.ExecuteRawAsync(
                "CREATE wallet:jkl CONTENT { id: 'jkl', amount: 1 }");
            create.EnsureAllOk();
            // intentionally NO txn.CommitAsync()
        }

        await using var reader = new HttpSurrealConnection(new HttpClientHandler(), MakeOptions(db));
        var resp = await reader.ExecuteRawAsync(
            "SELECT * FROM wallet WHERE id = $id",
            new { id = "wallet:jkl" });
        resp[0].IsOk.Should().BeTrue();
        resp.GetValues<System.Text.Json.JsonElement>(0).Should().BeEmpty(
            "dispose-without-commit must roll back the CREATE so a separate connection sees no row");
    }

    [Fact]
    public async Task Typed_conditional_update_has_exactly_one_concurrent_winner()
    {
        if (TrySkip()) return;

        var db = $"client_int_{Guid.NewGuid():N}"[..30];
        _fx.EnsureDatabase(db);
        await using (var setup = new HttpSurrealConnection(new HttpClientHandler(), MakeOptions(db)))
        {
            (await setup.ExecuteRawAsync(
                "DEFINE TABLE IF NOT EXISTS claim_record SCHEMALESS; " +
                "CREATE claim_record:one SET status = 'pending', " +
                "claim_key = type::string('claim:one'), " +
                "optional_note = 'clear-me', required_name = 'stable';")).EnsureAllOk();
        }

        FluentActions.Invoking(() => SurrealWriter.UpdateOnly<LiveClaimRecord>("one")
                .Where(r => r.Status == "pending")
                .Set<string?>(r => r.OptionalNote, null))
            .Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => SurrealWriter.UpdateOnly<LiveClaimRecord>("one")
                .Where(r => r.Status == "pending")
                .Unset(r => r.RequiredName))
            .Should().Throw<InvalidOperationException>();

        var attempts = Enumerable.Range(0, 20).Select(async i =>
        {
            await using var connection = new HttpSurrealConnection(
                new HttpClientHandler(), MakeOptions(db));
            var executor = new DefaultSurrealExecutor(connection);
            var mutation = SurrealWriter.UpdateOnly<LiveClaimRecord>("one")
                .Where(r => r.Status == "pending" && r.ClaimKey == "claim:one")
                .Set(r => r.Status, "claimed")
                .Set(r => r.Winner, "worker-" + i)
                .Unset(r => r.OptionalNote)
                .Build();
            // Optimistic-engine write conflicts are marked retryable by the
            // server ("This transaction can be retried") — loop like a real worker.
            for (var retry = 0; ; retry++)
            {
                try
                {
                    var response = await executor.ExecuteAsync(mutation);
                    response.EnsureAllOk();
                    return response[0].AffectedCount();
                }
                catch (SurrealStatementException ex) when (
                    retry < 10 && ex.Message.Contains("retried", StringComparison.OrdinalIgnoreCase))
                {
                    await Task.Delay(25 * (retry + 1));
                }
            }
        });

        var affectedCounts = await Task.WhenAll(attempts);
        affectedCounts.Count(count => count == 1).Should().Be(1);
        affectedCounts.Count(count => count == 0).Should().Be(19);

        await using var reader = new HttpSurrealConnection(new HttpClientHandler(), MakeOptions(db));
        var saved = await new DefaultSurrealExecutor(reader).QuerySingleAsync<LiveClaimRecord>(
            SurrealQuery<LiveClaimRecord>.Key("one"));
        saved.Should().NotBeNull();
        saved!.OptionalNote.Should().BeNull();
        saved.RequiredName.Should().Be("stable");
    }

    [Fact]
    public async Task Typed_conditional_delete_is_match_required_and_replay_safe()
    {
        if (TrySkip()) return;

        var db = $"client_int_{Guid.NewGuid():N}"[..30];
        _fx.EnsureDatabase(db);
        await using var connection = new HttpSurrealConnection(
            new HttpClientHandler(), MakeOptions(db));
        var executor = new DefaultSurrealExecutor(connection);
        (await connection.ExecuteRawAsync(
            "DEFINE TABLE IF NOT EXISTS claim_record SCHEMALESS; " +
            "CREATE claim_record:delete_me SET status = 'claimed', " +
            "claim_key = type::string('claim:delete');")).EnsureAllOk();

        var wrongPredicate = SurrealWriter.DeleteOnly<LiveClaimRecord>("delete_me")
            .Where(r => r.Status == "pending")
            .Build();
        var wrongResponse = await executor.ExecuteAsync(wrongPredicate);
        wrongResponse.EnsureAllOk();
        wrongResponse[0].AffectedCount().Should().Be(0);

        var delete = SurrealWriter.DeleteOnly<LiveClaimRecord>("delete_me")
            .Where(r => r.Status == "claimed" && r.ClaimKey == "claim:delete")
            .Build();
        var first = await executor.ExecuteAsync(delete);
        first.EnsureAllOk();
        first[0].AffectedCount().Should().Be(1);

        var replay = await executor.ExecuteAsync(delete);
        replay.EnsureAllOk();
        replay[0].AffectedCount().Should().Be(0);
    }

    [SurrealTable("claim_record")]
    private sealed class LiveClaimRecord : ISurrealRecord
    {
        public string SchemaName => "claim_record";

        [Id]
        public string Id { get; set; } = string.Empty;

        [Column(Type = "string")]
        public string Status { get; set; } = string.Empty;

        [Column(Type = "option<string>")]
        public string? Winner { get; set; }

        [Column(Type = "option<string>")]
        public string? ClaimKey { get; set; }

        [Column(Type = "option<string>")]
        public string? OptionalNote { get; set; }

        [Column(Type = "string")]
        public string RequiredName { get; set; } = string.Empty;
    }
}
