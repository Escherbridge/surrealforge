// SPDX-License-Identifier: UNLICENSED
// SurrealForge.Schema.Tests -- Migration runner integration tests (Phase 4 task 25).
//
// All tests use a captured-SQL `RecordingConnection` (no live container).
// Coverage:
//   - schema_migration table DDL is emitted on first apply.
//   - Re-applying the same file with matching checksum sends zero new DEFINE / UPDATE writes.
//   - Checksum mismatch aborts the batch unless --force is set.
//   - Dry-run executes zero writes.
//   - DiscoverFiles orders by numeric prefix (ordinal sort on filename).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using SurrealForge.Schema.Migration;

namespace SurrealForge.Schema.Tests.Migration
{
    public class MigrationRunnerTests
    {
        [Fact]
        public async Task First_apply_emits_schema_migration_DDL_and_records_each_file()
        {
            var conn = new RecordingConnection();
            var runner = new MigrationRunner(conn, appliedBy: "test");
            var files = new[]
            {
                new MigrationFile("/x/010_wallet.surql", "DEFINE TABLE wallet SCHEMAFULL;"),
                new MigrationFile("/x/020_bridge_tx.surql", "DEFINE TABLE bridge_tx SCHEMAFULL;"),
            };

            var plan = await runner.ApplyAsync(files);

            plan.Should().HaveCount(2);
            plan.Select(p => p.Action).Should().AllBeEquivalentTo(MigrationAction.Apply);

            // First call: schema_migration table DDL.
            conn.SentSql.Should().NotBeEmpty();
            conn.SentSql[0].Should().Contain("DEFINE TABLE IF NOT EXISTS schema_migration SCHEMAFULL");
            conn.SentSql[0].Should().Contain("file_name");
            conn.SentSql[0].Should().Contain("checksum");

            // Then: each file's content was sent + a recording UPDATE.
            conn.SentSql.Should().Contain(s => s == "DEFINE TABLE wallet SCHEMAFULL;");
            conn.SentSql.Should().Contain(s => s == "DEFINE TABLE bridge_tx SCHEMAFULL;");
            conn.SentSql.Where(s => s.StartsWith("UPSERT schema_migration:")).Should().HaveCount(2);
        }

        [Fact]
        public async Task Reapply_with_matching_checksum_is_idempotent()
        {
            var conn = new RecordingConnection();
            var runner = new MigrationRunner(conn, appliedBy: "test");
            var files = new[] { new MigrationFile("/x/010_wallet.surql", "DEFINE TABLE wallet SCHEMAFULL;") };

            await runner.ApplyAsync(files);

            // Capture the count of DDL writes (file content + recording UPDATE).
            int writesAfterFirstApply = conn.SentSql.Count(s =>
                s.Contains("DEFINE TABLE wallet")
                || s.StartsWith("UPSERT schema_migration:"));

            // Simulate that the server now has the record. RecordingConnection
            // exposes its applied rows to itself via its in-memory store.
            conn.MarkApplied("010_wallet.surql", files[0].Checksum);

            // Second apply: no new writes for the wallet file.
            conn.ClearLog();
            var plan2 = await runner.ApplyAsync(files);

            plan2.Single().Action.Should().Be(MigrationAction.Skip);
            conn.SentSql.Should().NotContain(s => s.Contains("DEFINE TABLE wallet"));
            conn.SentSql.Should().NotContain(s => s.StartsWith("UPSERT schema_migration:"));
        }

        [Fact]
        public async Task Checksum_mismatch_aborts_batch_without_force()
        {
            var conn = new RecordingConnection();
            var runner = new MigrationRunner(conn);
            var files = new[]
            {
                new MigrationFile("/x/010_wallet.surql", "DEFINE TABLE wallet SCHEMAFULL;\n-- new comment\n"),
            };

            // Pretend a different content was previously applied.
            conn.MarkApplied("010_wallet.surql", "00deadbeef0000000000000000000000");

            var act = () => runner.ApplyAsync(files);
            var ex = await act.Should().ThrowAsync<MigrationChecksumMismatchException>();
            ex.Which.Mismatches.Should().ContainSingle(m =>
                m.File.FileName == "010_wallet.surql"
                && m.PriorChecksum == "00deadbeef0000000000000000000000");

            // Critical: no DDL was sent for the file (only the schema_migration
            // table DDL + SELECT may have been emitted before the abort).
            conn.SentSql.Should().NotContain(s => s.Contains("DEFINE TABLE wallet"));
        }

        [Fact]
        public async Task Force_overrides_checksum_mismatch_and_overwrites_record()
        {
            var conn = new RecordingConnection();
            var runner = new MigrationRunner(conn);
            var files = new[]
            {
                new MigrationFile("/x/010_wallet.surql", "DEFINE TABLE wallet SCHEMAFULL;\n-- new content\n"),
            };
            conn.MarkApplied("010_wallet.surql", "00deadbeef0000000000000000000000");

            var plan = await runner.ApplyAsync(files, dryRun: false, force: true);

            plan.Should().ContainSingle().Which.Action.Should().Be(MigrationAction.ChecksumMismatch);
            // The DDL was sent and the row was overwritten.
            conn.SentSql.Should().Contain(s => s.Contains("DEFINE TABLE wallet"));
            conn.SentSql.Should().Contain(s => s.StartsWith("UPSERT schema_migration:")
                && s.Contains(files[0].Checksum));
        }

        [Fact]
        public async Task Dry_run_executes_zero_writes()
        {
            var conn = new RecordingConnection();
            var runner = new MigrationRunner(conn);
            var files = new[]
            {
                new MigrationFile("/x/010_wallet.surql", "DEFINE TABLE wallet SCHEMAFULL;"),
                new MigrationFile("/x/020_bridge_tx.surql", "DEFINE TABLE bridge_tx SCHEMAFULL;"),
            };

            // Strip the schema_migration table DDL from the "writes" set so we
            // can audit *user-file* writes only. The runner is permitted to
            // create the tracking table even in dry-run (it's idempotent and
            // necessary for the plan-query to succeed).
            await runner.ApplyAsync(files, dryRun: true, force: false);

            conn.SentSql.Should().NotContain(s => s == "DEFINE TABLE wallet SCHEMAFULL;");
            conn.SentSql.Should().NotContain(s => s == "DEFINE TABLE bridge_tx SCHEMAFULL;");
            conn.SentSql.Should().NotContain(s => s.StartsWith("UPSERT schema_migration:"));
        }

        [Fact]
        public async Task Status_reads_applied_rows_back_from_tracking_table()
        {
            var conn = new RecordingConnection();
            var runner = new MigrationRunner(conn);
            conn.MarkApplied("010_wallet.surql", "abc123");
            conn.MarkApplied("020_bridge_tx.surql", "def456");

            var status = await runner.StatusAsync();
            status.Should().HaveCount(2);
            status.Select(r => r.FileName).Should().BeEquivalentTo(new[] { "010_wallet.surql", "020_bridge_tx.surql" });
        }

        [Fact]
        public void DiscoverFiles_orders_by_numeric_prefix()
        {
            var tmp = Path.Combine(Path.GetTempPath(), "surrealforge-disc-" + Guid.NewGuid());
            Directory.CreateDirectory(tmp);
            try
            {
                File.WriteAllText(Path.Combine(tmp, "020_b.surql"), "DEFINE TABLE b;");
                File.WriteAllText(Path.Combine(tmp, "010_a.surql"), "DEFINE TABLE a;");
                File.WriteAllText(Path.Combine(tmp, "100_c.surql"), "DEFINE TABLE c;");

                var files = MigrationRunner.DiscoverFiles(tmp);
                files.Select(f => f.FileName).Should().Equal("010_a.surql", "020_b.surql", "100_c.surql");
            }
            finally
            {
                Directory.Delete(tmp, recursive: true);
            }
        }

        [Fact]
        public void Sha256_is_stable_across_runs()
        {
            // Determinism guard for the checksum function -- prevents an
            // accidental "use System.Security.Cryptography.RandomNumberGenerator"
            // regression.
            var a = MigrationFile.ComputeSha256("hello world");
            var b = MigrationFile.ComputeSha256("hello world");
            a.Should().Be(b);
            a.Should().HaveLength(64).And.MatchRegex("^[0-9a-f]+$");
        }

        [Fact]
        public void BootstrapSchemaMigrationDdl_is_a_compile_time_literal()
        {
            // HIGH#6: the bootstrap DDL must not vary at runtime — it is a
            // compile-time const so SRDB0001's "no interpolation" rule
            // applies trivially. Two reads at different points in the run
            // must be identical (and identical to BuildTrackingTableDdl()'s
            // return value to preserve the backwards-compatible getter).
            var a = MigrationRunner.BootstrapSchemaMigrationDdl;
            var b = MigrationRunner.BootstrapSchemaMigrationDdl;
            a.Should().Be(b);
            a.Should().Contain("DEFINE TABLE IF NOT EXISTS schema_migration SCHEMAFULL");
            a.Should().Contain("DEFINE INDEX IF NOT EXISTS schema_migration_file_name");
            MigrationRunner.BuildTrackingTableDdl().Should().Be(a,
                "BuildTrackingTableDdl() must remain a backwards-compatible alias for the const");
        }

        [Fact]
        public async Task RecordAppliedAsync_escapes_appliedBy_with_embedded_quotes_and_backslashes()
        {
            // HIGH#6 safety claim: operator-supplied applied_by must be JSON-escaped
            // before being embedded in the SurrealQL UPDATE statement. A value
            // containing both a quote and a backslash must round-trip through
            // the SQL body as a valid JSON-escaped substring.
            var conn = new RecordingConnection();
            var nasty = "ci-runner\" OR 1=1 --\\";
            var runner = new MigrationRunner(conn, appliedBy: nasty);
            var files = new[] { new MigrationFile("/x/010_wallet.surql", "DEFINE TABLE wallet SCHEMAFULL;") };

            await runner.ApplyAsync(files);

            var update = conn.SentSql.Single(s => s.StartsWith("UPSERT schema_migration:"));
            // The literal quote and backslash from the operator string must NOT
            // appear unescaped — they must be \\ and \" inside the SurrealQL
            // string literal.
            update.Should().Contain("\\\"", "embedded quote must be JSON-escaped");
            update.Should().Contain("\\\\", "embedded backslash must be JSON-escaped");
            // The exploit payload (`OR 1=1`) is inside a quoted JSON string so
            // even though the text appears in the SQL body, it cannot terminate
            // the string literal early.
            update.Should().NotContain("\"" + nasty + "\"",
                "raw unescaped operator string must NOT appear in the SQL body");
        }
    }

    /// <summary>
    /// In-memory <see cref="ISurrealConnection"/> mock that captures every
    /// SQL statement sent and maintains a tiny <c>schema_migration</c>
    /// emulation so SELECT calls return realistic JSON.
    /// </summary>
    internal sealed class RecordingConnection : ISurrealConnection
    {
        public List<string> SentSql { get; } = new();
        private readonly Dictionary<string, string> _applied = new(StringComparer.Ordinal);

        public void MarkApplied(string fileName, string checksum) => _applied[fileName] = checksum;
        public void ClearLog() => SentSql.Clear();

        // The mock doesn't care about scope vs unscoped -- the recording fake
        // forwards both to the same handler so test assertions remain stable.
        public Task<SurrealExecutionResult> ExecuteUnscopedAsync(string surql, CancellationToken ct = default)
            => ExecuteAsync(surql, ct);

        public Task<SurrealExecutionResult> ExecuteAsync(string surql, CancellationToken ct = default)
        {
            SentSql.Add(surql);

            // SELECT from schema_migration: return current state as JSON.
            if (surql.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) &&
                surql.Contains("schema_migration", StringComparison.OrdinalIgnoreCase))
            {
                var rows = _applied
                    .Select(kvp => $"{{\"file_name\":\"{kvp.Key}\",\"checksum\":\"{kvp.Value}\",\"applied_at\":\"2026-01-01T00:00:00.0000000Z\",\"applied_by\":\"test\"}}");
                var body = "[" + string.Join(",", rows) + "]";
                return Task.FromResult(SurrealExecutionResult.Ok(body));
            }

            // UPSERT schema_migration:... CONTENT { file_name: "<name>", checksum: "<hash>", ... }
            if (surql.StartsWith("UPSERT schema_migration:", StringComparison.OrdinalIgnoreCase))
            {
                var (file, checksum) = ExtractFileAndChecksum(surql);
                if (file != null && checksum != null) _applied[file] = checksum;
            }

            return Task.FromResult(SurrealExecutionResult.Ok("[]"));
        }

        private static (string?, string?) ExtractFileAndChecksum(string sql)
        {
            // Cheap regex-free extraction sufficient for the test runner.
            string? file = ExtractAfter(sql, "file_name: \"");
            string? checksum = ExtractAfter(sql, "checksum: \"");
            return (file, checksum);
        }

        private static string? ExtractAfter(string sql, string marker)
        {
            int idx = sql.IndexOf(marker, StringComparison.Ordinal);
            if (idx < 0) return null;
            int start = idx + marker.Length;
            int end = sql.IndexOf('"', start);
            if (end < 0) return null;
            return sql.Substring(start, end - start);
        }
    }
}
