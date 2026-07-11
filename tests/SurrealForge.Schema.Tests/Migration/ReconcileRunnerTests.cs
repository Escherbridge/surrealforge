// SPDX-License-Identifier: UNLICENSED
// ReconcileRunner tests -- end-to-end (in-memory) of introspect -> diff ->
// OVERWRITE apply. A scripted connection returns INFO FOR DB / TABLE bodies and
// captures the DDL the runner sends, so we assert the exact OVERWRITE that
// evolves a field type, destructive gating, and coercion-error surfacing.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using SurrealForge.Schema.Migration;
using static SurrealForge.Schema.Tests.Migration.SchemaModelTestFactory;

namespace SurrealForge.Schema.Tests.Migration
{
    public class ReconcileRunnerTests
    {
        [Fact]
        public async Task Field_type_change_applies_DEFINE_FIELD_OVERWRITE()
        {
            // Live: api_key.scopes is option<string>. Desired: array<string>.
            var conn = new ScriptedConnection()
                .WithDbTables("api_key")
                .WithTableFields("api_key",
                    ("scopes", "DEFINE FIELD scopes ON api_key TYPE option<string>"));

            var desired = NewModel(Table("api_key", Field("scopes", "array<string>")));

            var plan = await new ReconcileRunner(conn).ReconcileAsync(
                desired, dryRun: false, allowDestructive: true);

            plan.ChangeSet.Fields.Single().Kind.Should().Be(FieldChangeKind.TypeChanged);
            conn.Sent.Should().Contain(s =>
                s.Contains("DEFINE FIELD OVERWRITE scopes ON TABLE api_key TYPE array<string>"));
            plan.Applied.Should().ContainSingle();
        }

        [Fact]
        public async Task Dry_run_sends_zero_DDL()
        {
            var conn = new ScriptedConnection()
                .WithDbTables("api_key")
                .WithTableFields("api_key", ("scopes", "DEFINE FIELD scopes ON api_key TYPE option<string>"));
            var desired = NewModel(Table("api_key", Field("scopes", "array<string>")));

            var plan = await new ReconcileRunner(conn).ReconcileAsync(desired, dryRun: true, allowDestructive: true);

            plan.Statements.Should().ContainSingle();
            conn.Sent.Should().NotContain(s => s.Contains("OVERWRITE"));
        }

        [Fact]
        public async Task No_drift_applies_nothing()
        {
            var conn = new ScriptedConnection()
                .WithDbTables("api_key")
                .WithTableFields("api_key", ("scopes", "DEFINE FIELD scopes ON api_key TYPE array<string>"));
            var desired = NewModel(Table("api_key", Field("scopes", "array<string>")));

            var plan = await new ReconcileRunner(conn).ReconcileAsync(desired);
            plan.HasChanges.Should().BeFalse();
            plan.Applied.Should().BeEmpty();
        }

        [Fact]
        public async Task Destructive_change_is_skipped_without_allowDestructive()
        {
            // Removing a live field is destructive; must not run unless opted in.
            var conn = new ScriptedConnection()
                .WithDbTables("t")
                .WithTableFields("t",
                    ("a", "DEFINE FIELD a ON t TYPE string"),
                    ("stale", "DEFINE FIELD stale ON t TYPE int"));
            var desired = NewModel(Table("t", Field("a", "string")));

            var plan = await new ReconcileRunner(conn).ReconcileAsync(desired, dryRun: false, allowDestructive: false);

            plan.SkippedDestructive.Should().ContainSingle(s => s.Ddl.Contains("REMOVE FIELD"));
            conn.Sent.Should().NotContain(s => s.Contains("REMOVE FIELD"));
        }

        [Fact]
        public async Task Destructive_change_runs_with_allowDestructive()
        {
            var conn = new ScriptedConnection()
                .WithDbTables("t")
                .WithTableFields("t",
                    ("a", "DEFINE FIELD a ON t TYPE string"),
                    ("stale", "DEFINE FIELD stale ON t TYPE int"));
            var desired = NewModel(Table("t", Field("a", "string")));

            await new ReconcileRunner(conn).ReconcileAsync(desired, dryRun: false, allowDestructive: true);

            conn.Sent.Should().Contain(s => s.Contains("REMOVE FIELD IF EXISTS stale ON TABLE t"));
        }

        [Fact]
        public async Task Coercion_failure_surfaces_SchemaCoercionException()
        {
            var conn = new ScriptedConnection()
                .WithDbTables("api_key")
                .WithTableFields("api_key", ("scopes", "DEFINE FIELD scopes ON api_key TYPE option<string>"))
                .FailOn("OVERWRITE", "Couldn't coerce value for field `scopes`: Expected `array<string>`");
            var desired = NewModel(Table("api_key", Field("scopes", "array<string>")));

            var act = () => new ReconcileRunner(conn).ReconcileAsync(desired, dryRun: false, allowDestructive: true);

            var ex = await act.Should().ThrowAsync<SchemaCoercionException>();
            ex.Which.Message.Should().Contain("cannot coerce");
            ex.Which.Change.Should().Contain("scopes");
        }

        [Fact]
        public async Task Tracking_table_is_excluded_from_introspection()
        {
            // schema_migration must never appear as drift.
            var conn = new ScriptedConnection()
                .WithDbTables("schema_migration", "api_key")
                .WithTableFields("api_key", ("scopes", "DEFINE FIELD scopes ON api_key TYPE array<string>"));
            var desired = NewModel(Table("api_key", Field("scopes", "array<string>")));

            var plan = await new ReconcileRunner(conn).ReconcileAsync(desired);
            plan.ChangeSet.Tables.Should().NotContain(t => t.Table == "schema_migration");
        }
    }

    /// <summary>
    /// In-memory ISurrealConnection that answers INFO FOR DB / INFO FOR TABLE
    /// from a scripted table map and records every other statement sent. Used to
    /// drive ReconcileRunner without a live SurrealDB.
    /// </summary>
    internal sealed class ScriptedConnection : ISurrealConnection
    {
        private readonly List<string> _tables = new();
        private readonly Dictionary<string, List<(string field, string ddl)>> _fields =
            new(StringComparer.Ordinal);
        private string? _failMarker;
        private string? _failDetail;

        public List<string> Sent { get; } = new();

        public ScriptedConnection WithDbTables(params string[] names)
        {
            _tables.AddRange(names);
            return this;
        }

        public ScriptedConnection WithTableFields(string table, params (string field, string ddl)[] fields)
        {
            _fields[table] = fields.ToList();
            return this;
        }

        public ScriptedConnection FailOn(string marker, string detail)
        {
            _failMarker = marker;
            _failDetail = detail;
            return this;
        }

        public Task<SurrealExecutionResult> ExecuteUnscopedAsync(string surql, CancellationToken ct = default)
            => ExecuteAsync(surql, ct);

        public Task<SurrealExecutionResult> ExecuteAsync(string surql, CancellationToken ct = default)
        {
            if (surql.StartsWith("INFO FOR DB", StringComparison.OrdinalIgnoreCase))
            {
                var tableJson = string.Join(",", _tables.Select(t => $"\"{t}\":\"DEFINE TABLE {t}\""));
                return Ok($"[{{\"status\":\"OK\",\"result\":{{\"tables\":{{{tableJson}}}}}}}]");
            }
            if (surql.StartsWith("INFO FOR TABLE", StringComparison.OrdinalIgnoreCase))
            {
                var table = surql.Substring("INFO FOR TABLE".Length).Trim().TrimEnd(';').Trim();
                var fields = _fields.TryGetValue(table, out var f) ? f : new List<(string field, string ddl)>();
                var fieldJson = string.Join(",", fields.Select(x => $"\"{x.field}\":\"{x.ddl}\""));
                return Ok($"[{{\"status\":\"OK\",\"result\":{{\"fields\":{{{fieldJson}}},\"indexes\":{{}}}}}}]");
            }

            Sent.Add(surql);
            if (_failMarker != null && surql.IndexOf(_failMarker, StringComparison.OrdinalIgnoreCase) >= 0)
                return Task.FromResult(SurrealExecutionResult.Error(_failDetail ?? "error"));
            return Ok("[]");
        }

        private static Task<SurrealExecutionResult> Ok(string body)
            => Task.FromResult(SurrealExecutionResult.Ok(body));
    }
}
