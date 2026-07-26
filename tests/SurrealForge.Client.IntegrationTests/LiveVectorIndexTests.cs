// SPDX-License-Identifier: MIT
// Live HNSW verification — the one link unit tests cannot cover: real INFO
// output flowing through ParseVectorIndexClauses. Reconcile a scanned
// [HnswIndex] model into a running SurrealDB, KNN-query it, introspect the
// index back, and prove a second reconcile sees zero phantom drift.
// See src/SurrealForge.Schema/Migration/AGENTS.md §Vector indexes.

using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using SurrealForge.Client.Schema;
using SurrealForge.Schema.Cli;
using SurrealForge.Schema.Generator;
using SurrealForge.Schema.Migration;
using SurrealForge.Schema.Model;

namespace SurrealForge.Client.IntegrationTests;

[Collection("LiveSurrealDb")]
public class LiveVectorIndexTests
{
    private readonly LiveSurrealDbCollectionFixture _fx;

    public LiveVectorIndexTests(LiveSurrealDbCollectionFixture fx) => _fx = fx;

    private bool TrySkip()
    {
        if (_fx.SurrealAvailable) return false;
        Console.WriteLine($"[SKIP] LiveSurrealDb unavailable: {_fx.SkipReason}");
        return true;
    }

    private HttpConnectionAdapter MakeAdapter(string db)
        => new HttpConnectionAdapter(_fx.Endpoint, _fx.User, _fx.Password, _fx.Namespace, db);

    // Emitter output minus the (multi-line) DEFINE INDEX statements — the
    // reconcile under test is what must create those.
    private static string SeedWithoutIndexes(SchemaModel desired) =>
        string.Join(";\n", SurqlEmitter.Emit(desired)
            .Split(new[] { ";\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Where(s => !s.Contains("DEFINE INDEX"))) + ";";

    // ── fixture POCOs ──────────────────────────────────────────────────────

    [SurrealTable("vec_doc")]
    private sealed class VecDocPoco
    {
        [Column(Order = 1)]
        public string? Title { get; set; }

        [Column(Order = 2, Type = "array<float>")]
        [HnswIndex("hnsw_vec_doc_embedding", Dimension = 4, Distance = "COSINE", Type = "F32", Efc = 150, M = 12)]
        public float[]? Embedding { get; set; }
    }

    // Tuning params unspecified — exercises the desired-null-as-wildcard path
    // against whatever defaults the live server reports back in INFO.
    [SurrealTable("vec_min")]
    private sealed class VecMinPoco
    {
        [Column(Order = 1, Type = "array<float>")]
        [HnswIndex("hnsw_vec_min_embedding", Dimension = 4)]
        public float[]? Embedding { get; set; }
    }

    [Fact]
    public async Task Hnsw_reconcile_knn_query_and_zero_drift_live()
    {
        if (TrySkip()) return;

        var db = $"vec_{Guid.NewGuid():N}"[..30];
        _fx.EnsureDatabase(db);
        var conn = MakeAdapter(db);

        // 1. Seed table + fields (reconcile evolves existing tables; it does
        //    not create them) — emitter output minus the index lines.
        var desired = AttributeSchemaScanner.ScanType(typeof(VecDocPoco));
        var seedResult = await conn.ExecuteAsync(SeedWithoutIndexes(desired));
        seedResult.IsOk.Should().BeTrue(seedResult.Detail);

        // 2. Reconcile must add exactly the HNSW index.
        var plan = await new ReconcileRunner(conn).ReconcileAsync(
            desired, dryRun: false, allowDestructive: true);
        plan.Applied.Should().ContainSingle().Which.Ddl.Should()
            .Contain("hnsw_vec_doc_embedding").And.Contain("HNSW").And.Contain("DIMENSION 4");

        // 3. Seed vectors: alpha is the query itself, beta is near, gamma/delta orthogonal.
        (await conn.ExecuteAsync(
            "CREATE vec_doc SET title = 'alpha', embedding = [1.0, 0.0, 0.0, 0.0];" +
            "CREATE vec_doc SET title = 'beta',  embedding = [0.9, 0.1, 0.0, 0.0];" +
            "CREATE vec_doc SET title = 'gamma', embedding = [0.0, 1.0, 0.0, 0.0];" +
            "CREATE vec_doc SET title = 'delta', embedding = [0.0, 0.0, 1.0, 0.0];"))
            .IsOk.Should().BeTrue();

        // 4. Indexed KNN (<|K,EF|> targets the HNSW index) returns the two nearest.
        var knn = await conn.ExecuteAsync(
            "SELECT title FROM vec_doc WHERE embedding <|2,40|> [1.0, 0.0, 0.0, 0.0];");
        knn.IsOk.Should().BeTrue(knn.Detail);
        knn.RawBody.Should().Contain("alpha").And.Contain("beta");
        knn.RawBody.Should().NotContain("gamma").And.NotContain("delta");

        // 5. Introspect the live index back — real INFO output through the parser.
        var live = await new LiveSchemaIntrospector(conn).IntrospectTableAsync("vec_doc");
        var idx = live.Indexes.Should().ContainSingle(i => i.Name == "hnsw_vec_doc_embedding").Subject;
        idx.VectorKind.Should().Be(VectorIndexKind.Hnsw);
        idx.Dimension.Should().Be(4);
        idx.Distance.Should().Be("COSINE");
        idx.VectorType.Should().Be("F32");
        idx.Efc.Should().Be(150);
        idx.M.Should().Be(12);

        // 6. Second reconcile must see no drift (derived INFO tokens ignored).
        var plan2 = await new ReconcileRunner(conn).ReconcileAsync(desired);
        plan2.HasChanges.Should().BeFalse(
            "live INFO output must round-trip through ParseVectorIndexClauses without phantom drift");

        await conn.ExecuteAsync("REMOVE TABLE IF EXISTS vec_doc;");
        conn.Dispose();
    }

    [Fact]
    public async Task Hnsw_unspecified_tuning_params_do_not_drift_live()
    {
        if (TrySkip()) return;

        var db = $"vec_{Guid.NewGuid():N}"[..30];
        _fx.EnsureDatabase(db);
        var conn = MakeAdapter(db);

        var desired = AttributeSchemaScanner.ScanType(typeof(VecMinPoco));
        var seedResult = await conn.ExecuteAsync(SeedWithoutIndexes(desired));
        seedResult.IsOk.Should().BeTrue(seedResult.Detail);

        var plan = await new ReconcileRunner(conn).ReconcileAsync(
            desired, dryRun: false, allowDestructive: true);
        plan.Applied.Should().Contain(s => s.Ddl.Contains("hnsw_vec_min_embedding"));

        // Server fills EFC/M/TYPE with defaults and reports them in INFO —
        // an unspecified desired value is a wildcard, never drift.
        var plan2 = await new ReconcileRunner(conn).ReconcileAsync(desired);
        plan2.HasChanges.Should().BeFalse(
            "server-default tuning tokens in live INFO must not register as drift");

        await conn.ExecuteAsync("REMOVE TABLE IF EXISTS vec_min;");
        conn.Dispose();
    }
}
