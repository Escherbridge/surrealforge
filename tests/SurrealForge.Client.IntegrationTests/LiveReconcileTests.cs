// SPDX-License-Identifier: UNLICENSED
// Model-driven reconcile regression test -- the EXACT AZOA production scenario.
//
// Create a table with a field TYPE string, then reconcile to a model where that
// field is array<string>, and assert the LIVE field type actually changed.
// Under the old IF-NOT-EXISTS emit this silently no-op'd (the production bug);
// the OVERWRITE reconcile path must make it stick.
//
// Gracefully early-returns when no local SurrealDB is reachable (mirrors the
// rest of this suite's pass-off contract).

using System;
using System.Threading.Tasks;
using FluentAssertions;
using SurrealForge.Schema.Cli;
using SurrealForge.Schema.Migration;
using SurrealForge.Schema.Model;

namespace SurrealForge.Client.IntegrationTests;

[Collection("LiveSurrealDb")]
public class LiveReconcileTests
{
    private readonly LiveSurrealDbCollectionFixture _fx;

    public LiveReconcileTests(LiveSurrealDbCollectionFixture fx) => _fx = fx;

    private bool TrySkip()
    {
        if (_fx.SurrealAvailable) return false;
        Console.WriteLine($"[SKIP] LiveSurrealDb unavailable: {_fx.SkipReason}");
        return true;
    }

    private HttpConnectionAdapter MakeAdapter(string db)
        => new HttpConnectionAdapter(_fx.Endpoint, _fx.User, _fx.Password, _fx.Namespace, db);

    [Fact]
    public async Task Reconcile_evolves_field_type_string_to_array_string_live()
    {
        if (TrySkip()) return;

        var db = $"reconcile_{Guid.NewGuid():N}"[..30];
        _fx.EnsureDatabase(db);
        var conn = MakeAdapter(db);

        // 1. Seed a table whose `scopes` field is a plain string (the pre-change
        //    shape). Use IF NOT EXISTS exactly like the old generated schema.
        var seed =
            "DEFINE TABLE IF NOT EXISTS api_key SCHEMAFULL;" +
            "DEFINE FIELD IF NOT EXISTS scopes ON TABLE api_key TYPE option<string>;";
        (await conn.ExecuteAsync(seed)).IsOk.Should().BeTrue();

        // 1b. Re-applying the SAME IF-NOT-EXISTS DDL with the NEW type must NOT
        //     change the live field -- this reproduces the production bug.
        var oldStyle = "DEFINE FIELD IF NOT EXISTS scopes ON TABLE api_key TYPE array<string>;";
        (await conn.ExecuteAsync(oldStyle)).IsOk.Should().BeTrue();
        (await LiveFieldType(conn, "api_key", "scopes"))
            .Should().Be("option<string>", "IF NOT EXISTS must NOT alter an existing field (the bug)");

        // 2. Reconcile to the desired model where scopes is array<string>.
        var desired = new SchemaModel("(test)", new[]
        {
            new SchemaEntity("api_key", new[]
            {
                new SchemaAttribute("scopes", "array<string>", false, null,
                    Array.Empty<SchemaAnnotation>(), 0),
            }, Array.Empty<SchemaAnnotation>(), Array.Empty<SchemaIndex>(), 0),
        }, Array.Empty<SchemaRelationship>());

        var plan = await new ReconcileRunner(conn).ReconcileAsync(
            desired, dryRun: false, allowDestructive: true);

        // 3. The OVERWRITE reconcile must have actually changed the live type.
        plan.Applied.Should().ContainSingle(s => s.Ddl.Contains("DEFINE FIELD OVERWRITE scopes"));
        (await LiveFieldType(conn, "api_key", "scopes"))
            .Should().Be("array<string>", "OVERWRITE reconcile must evolve the live field type");

        // 4. Idempotent: a second reconcile sees no drift.
        var plan2 = await new ReconcileRunner(conn).ReconcileAsync(desired);
        plan2.HasChanges.Should().BeFalse();

        // Cleanup.
        await conn.ExecuteAsync("REMOVE TABLE IF EXISTS api_key;");
        conn.Dispose();
    }

    [Fact]
    public async Task Reconcile_dry_run_reports_drift_without_applying_live()
    {
        if (TrySkip()) return;

        var db = $"reconcile_{Guid.NewGuid():N}"[..30];
        _fx.EnsureDatabase(db);
        var conn = MakeAdapter(db);

        await conn.ExecuteAsync(
            "DEFINE TABLE IF NOT EXISTS t SCHEMAFULL;" +
            "DEFINE FIELD IF NOT EXISTS n ON TABLE t TYPE option<string>;");

        var desired = new SchemaModel("(test)", new[]
        {
            new SchemaEntity("t", new[]
            {
                new SchemaAttribute("n", "array<string>", false, null,
                    Array.Empty<SchemaAnnotation>(), 0),
            }, Array.Empty<SchemaAnnotation>(), Array.Empty<SchemaIndex>(), 0),
        }, Array.Empty<SchemaRelationship>());

        var plan = await new ReconcileRunner(conn).ReconcileAsync(desired, dryRun: true, allowDestructive: true);

        plan.Statements.Should().ContainSingle(s => s.Ddl.Contains("OVERWRITE"));
        plan.Applied.Should().BeEmpty();
        // Field must be untouched.
        (await LiveFieldType(conn, "t", "n")).Should().Be("option<string>");

        await conn.ExecuteAsync("REMOVE TABLE IF EXISTS t;");
        conn.Dispose();
    }

    /// <summary>Read the live field's normalized TYPE token via INFO FOR TABLE.</summary>
    private static async Task<string?> LiveFieldType(ISurrealConnection conn, string table, string field)
    {
        var entity = await new LiveSchemaIntrospector(conn).IntrospectTableAsync(table);
        foreach (var a in entity.Attributes)
            if (a.Name == field) return a.Type;
        return null;
    }
}
