// SPDX-License-Identifier: UNLICENSED
// SurrealForge.Schema -- `surrealforge` CLI tool entry point (Phase 4 task 24).
//
// Subcommands:
//   surrealforge migrate up        Apply pending .surql files
//   surrealforge migrate down      (stub) Refuse with non-zero — manual rollback only
//   surrealforge migrate status    Read schema_migration table
//   surrealforge migrate dry-run   Plan only; zero writes
//   surrealforge generate <file>   Mermaid source -> .surql sibling
//   surrealforge validate <file>   Parse + report errors; exit non-zero on fail
//   surrealforge aggregates        Emit per-slice + master Mermaid diagrams from source/*.mermaid
//
// Connection config sources (resolution order, first wins per field):
//   1. --connection / --user / --pass / --namespace / --database flags
//   2. SURREALFORGE_URL / _USER / _PASS / _NS / _DB env vars
//   3. (no defaults — failing fast on missing fields when a command requires them)

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using SurrealForge.Client.Schema;
using SurrealForge.Schema.Cli;
using SurrealForge.Schema.Generator;
using SurrealForge.Schema.Migration;

namespace SurrealForge.Schema
{
    /// <summary>CLI entry point. Returns OS exit code.</summary>
    public static class Program
    {
        public static async Task<int> Main(string[] args)
        {
            try
            {
                var cli = CliArgs.Parse(args);
                if (string.IsNullOrEmpty(cli.Command) || cli.HasFlag("help") || cli.Command == "help")
                {
                    PrintHelp();
                    return 0;
                }

                switch (cli.Command)
                {
                    case "up":
                        return await RunUpAsync(cli).ConfigureAwait(false);
                    case "reset":
                        return await ResetCommand.RunAsync(cli).ConfigureAwait(false);
                    case "migrate":
                        return await RunMigrateAsync(cli).ConfigureAwait(false);
                    case "generate-from-assembly":
                        return RunGenerateFromAssembly(cli);
                    case "flowcharts-from-assembly":
                        return RunFlowchartsFromAssembly(cli);
                    default:
                        Console.Error.WriteLine($"unknown command: '{cli.Command}'");
                        PrintHelp();
                        return 64; // EX_USAGE
                }
            }
            catch (MigrationChecksumMismatchException ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 2;
            }
            catch (SchemaCoercionException ex)
            {
                Console.Error.WriteLine("schema evolution blocked: " + ex.Message);
                return 4;
            }
            catch (MigrationApplyException ex)
            {
                Console.Error.WriteLine("apply failed: " + ex.Message);
                return 3;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("error: " + ex.Message);
                return 70; // EX_SOFTWARE
            }
        }

        // ── migrate ────────────────────────────────────────────────────────
        private static async Task<int> RunMigrateAsync(CliArgs cli)
        {
            var sub = cli.SubCommand;
            switch (sub)
            {
                case "down":
                    Console.Error.WriteLine("migrate down: not implemented; manual rollback only.");
                    return 0;
                case "up":
                case "status":
                case "dry-run":
                    break;
                default:
                    Console.Error.WriteLine($"unknown migrate subcommand: '{sub}' (expected up|down|status|dry-run)");
                    return 64;
            }

            var schemaDir = cli.Flag("dir") ?? "Persistence/SurrealDb/Generated/Schemas";
            var conn = BuildConnection(cli);
            if (conn == null) return 64;
            var runner = new MigrationRunner(conn, cli.Flag("applied-by"));

            try
            {
                if (sub == "status")
                {
                    var rows = await runner.StatusAsync().ConfigureAwait(false);
                    if (rows.Count == 0) Console.WriteLine("(no migrations applied)");
                    foreach (var r in rows)
                    {
                        Console.WriteLine($"{r.FileName}\t{r.Checksum}\t{r.AppliedAt}\t{r.AppliedBy}");
                    }
                    return 0;
                }

                var files = MigrationRunner.DiscoverFiles(schemaDir);
                if (files.Count == 0)
                {
                    Console.Error.WriteLine($"no .surql files found under {schemaDir}");
                    return 1;
                }

                bool dryRun = sub == "dry-run" || cli.HasFlag("dry-run");
                bool force = cli.HasFlag("force");
                var plan = await runner.ApplyAsync(files, dryRun, force).ConfigureAwait(false);

                foreach (var p in plan)
                {
                    var verb = p.Action switch
                    {
                        MigrationAction.Apply => dryRun ? "WOULD APPLY" : "APPLIED",
                        MigrationAction.Skip => "SKIP (checksum match)",
                        MigrationAction.ChecksumMismatch => "MISMATCH",
                        _ => p.Action.ToString(),
                    };
                    Console.WriteLine($"{verb}\t{p.File.FileName}\t{p.File.Checksum}");
                }
                return 0;
            }
            finally
            {
                (conn as IDisposable)?.Dispose();
            }
        }

        // ── up ─────────────────────────────────────────────────────────────
        // Composite apply: Schemas/ first (DDL), then Migrations/ (hand-authored
        // data + one-shot fixes), then a model-driven RECONCILE pass (Phase 3)
        // that evolves drifted field types/defaults via DEFINE ... OVERWRITE.
        // Phases 1-2 flow through the MigrationRunner ledger keyed by file name
        // (idempotent when unchanged); Phase 3 introspects the live schema and
        // applies only the OVERWRITE DDL that IF NOT EXISTS cannot express.
        // This is the canonical "bring the DB in sync with the model" entry
        // point. See Migration/AGENTS.md.
        //
        //   surrealforge up [--schemas-dir <path>] [--migrations-dir <path>]
        //                    [--dry-run] [--force] [--no-reconcile]
        //                    [--allow-destructive] [--assembly <dll>]
        private static async Task<int> RunUpAsync(CliArgs cli)
        {
            var schemasDir = cli.Flag("schemas-dir") ?? "Persistence/SurrealDb/Generated/Schemas";
            var migrationsDir = cli.Flag("migrations-dir") ?? "Persistence/SurrealDb/Migrations";

            // Discover both directories up-front so a typo in either fails
            // before any DDL is sent. A missing migrations directory is
            // tolerated (a fresh repo has no hand-authored migrations yet).
            if (!Directory.Exists(schemasDir))
            {
                Console.Error.WriteLine($"schemas directory not found: {schemasDir}");
                return 1;
            }
            var schemaFiles = MigrationRunner.DiscoverFiles(schemasDir);
            if (schemaFiles.Count == 0)
            {
                Console.Error.WriteLine($"no .surql files found under {schemasDir}");
                return 1;
            }
            IReadOnlyList<MigrationFile> migrationFiles = Array.Empty<MigrationFile>();
            if (Directory.Exists(migrationsDir))
            {
                migrationFiles = MigrationRunner.DiscoverFiles(migrationsDir);
            }

            var conn = BuildConnection(cli);
            if (conn == null) return 64;

            // The runner bootstraps the configured namespace + database on
            // the first ExecuteAsync so the CLI can target a fresh server
            // without an out-of-band setup step. Pass the same NS/DB the
            // HTTP adapter is scoped to so the bootstrap matches.
            var nsForBootstrap = cli.Flag("namespace") ?? Environment.GetEnvironmentVariable("SURREALFORGE_NS");
            var dbForBootstrap = cli.Flag("database") ?? Environment.GetEnvironmentVariable("SURREALFORGE_DB");
            var runner = new MigrationRunner(
                conn,
                cli.Flag("applied-by"),
                ensureNamespace: nsForBootstrap,
                ensureDatabase: dbForBootstrap);

            bool dryRun = cli.HasFlag("dry-run");
            bool force = cli.HasFlag("force");
            // Reconcile (model-driven field evolution) runs by default -- it is
            // the fix for "a field TYPE change silently never applies". Skip it
            // only when the operator explicitly opts out (--no-reconcile) for
            // the legacy additive-only behaviour. --force still guarantees a
            // reconcile even if --no-reconcile is (contradictorily) also passed.
            bool reconcile = force || !cli.HasFlag("no-reconcile");
            bool allowDestructive = cli.HasFlag("allow-destructive");

            try
            {
                int applied = 0, skipped = 0, mismatches = 0;

                // Phase 1: schemas. DDL must exist before any data migration
                // touches a column.
                Console.WriteLine($"== Phase 1: schemas ({schemaFiles.Count} file(s) from {schemasDir})");
                var schemaPlan = await runner.ApplyAsync(schemaFiles, dryRun, force).ConfigureAwait(false);
                LogPlan(schemaPlan, dryRun, ref applied, ref skipped, ref mismatches);

                // Phase 2: hand-authored data migrations.
                if (migrationFiles.Count > 0)
                {
                    Console.WriteLine();
                    Console.WriteLine($"== Phase 2: migrations ({migrationFiles.Count} file(s) from {migrationsDir})");
                    var migrPlan = await runner.ApplyAsync(migrationFiles, dryRun, force).ConfigureAwait(false);
                    LogPlan(migrPlan, dryRun, ref applied, ref skipped, ref mismatches);
                }
                else if (Directory.Exists(migrationsDir))
                {
                    Console.WriteLine();
                    Console.WriteLine($"== Phase 2: migrations -- 0 files found under {migrationsDir}");
                }

                // Phase 3: reconcile. Introspect the live DB, diff against the
                // desired model (parsed from the emitted .surql schema files or
                // from --assembly), and apply DEFINE ... OVERWRITE for any
                // drifted field/index -- the statements IF NOT EXISTS cannot
                // express. This is what makes a field-TYPE change actually take
                // effect on deploy.
                int evolved = 0, evoSkipped = 0;
                if (reconcile)
                {
                    Console.WriteLine();
                    Console.WriteLine("== Phase 3: reconcile (model-driven field evolution)");
                    var (e, s) = await RunReconcileAsync(
                        cli, conn, schemasDir, dryRun, allowDestructive).ConfigureAwait(false);
                    evolved = e; evoSkipped = s;
                }

                Console.WriteLine();
                Console.WriteLine(dryRun
                    ? $"DRY RUN: would apply {applied}, skip {skipped}, {mismatches} mismatch(es), {evolved} evolve(s), {evoSkipped} destructive-skipped"
                    : $"applied {applied}, skipped {skipped}, {mismatches} mismatch(es), evolved {evolved}, destructive-skipped {evoSkipped}");
                return mismatches > 0 ? 2 : 0;
            }
            finally
            {
                (conn as IDisposable)?.Dispose();
            }
        }

        // Phase 3 helper: load the desired model, reconcile the live schema to
        // it via OVERWRITE DDL, and log the plan. Returns (evolvedCount,
        // destructiveSkippedCount). Desired-model source: --assembly <dll> when
        // provided (scans [SurrealTable] POCOs); otherwise the emitted .surql
        // files under schemasDir.
        private static async Task<(int evolved, int destructiveSkipped)> RunReconcileAsync(
            CliArgs cli,
            ISurrealConnection conn,
            string schemasDir,
            bool dryRun,
            bool allowDestructive)
        {
            Model.SchemaModel desired;
            var assemblyPath = cli.Flag("assembly");
            if (!string.IsNullOrWhiteSpace(assemblyPath))
            {
                if (!File.Exists(assemblyPath))
                {
                    Console.Error.WriteLine($"  reconcile: --assembly not found: {assemblyPath} -- skipping reconcile");
                    return (0, 0);
                }
                var asm = Assembly.LoadFrom(Path.GetFullPath(assemblyPath!));
                desired = AttributeSchemaScanner.ScanTypes(LoadTableTypes(asm));
            }
            else
            {
                desired = SurqlSchemaReader.ReadDirectory(schemasDir);
            }

            var reconciler = new ReconcileRunner(conn);
            var plan = await reconciler.ReconcileAsync(desired, dryRun, allowDestructive).ConfigureAwait(false);

            if (!plan.HasChanges)
            {
                Console.WriteLine("  no schema drift -- live matches model.");
                return (0, 0);
            }

            foreach (var stmt in plan.Statements)
            {
                string verb;
                if (dryRun) verb = stmt.IsDestructive && !allowDestructive ? "WOULD SKIP (destructive)" : "WOULD EVOLVE";
                else if (stmt.IsDestructive && !allowDestructive) verb = "SKIP (destructive)";
                else verb = "EVOLVED";
                Console.WriteLine($"  {verb}\t{stmt.Reason}");
            }

            int applied = dryRun
                ? CountApplicable(plan, allowDestructive)
                : plan.Applied.Count;
            int destructiveSkipped = dryRun
                ? CountDestructiveSkipped(plan, allowDestructive)
                : plan.SkippedDestructive.Count;

            if (destructiveSkipped > 0)
            {
                Console.Error.WriteLine(
                    $"  {destructiveSkipped} destructive change(s) NOT applied. " +
                    "Re-run with --allow-destructive to apply drops / narrowing type changes.");
            }
            return (applied, destructiveSkipped);
        }

        private static int CountApplicable(ReconcilePlan plan, bool allowDestructive)
        {
            int n = 0;
            foreach (var s in plan.Statements)
                if (allowDestructive || !s.IsDestructive) n++;
            return n;
        }

        private static int CountDestructiveSkipped(ReconcilePlan plan, bool allowDestructive)
        {
            if (allowDestructive) return 0;
            int n = 0;
            foreach (var s in plan.Statements) if (s.IsDestructive) n++;
            return n;
        }

        private static void LogPlan(
            IReadOnlyList<MigrationPlanItem> plan,
            bool dryRun,
            ref int applied,
            ref int skipped,
            ref int mismatches)
        {
            foreach (var p in plan)
            {
                var verb = p.Action switch
                {
                    MigrationAction.Apply => dryRun ? "WOULD APPLY" : "APPLIED",
                    MigrationAction.Skip => "SKIP",
                    MigrationAction.ChecksumMismatch => "MISMATCH",
                    _ => p.Action.ToString(),
                };
                Console.WriteLine($"  {verb}\t{p.File.FileName}\t{p.File.Checksum.Substring(0, 8)}…");
                switch (p.Action)
                {
                    case MigrationAction.Apply: applied++; break;
                    case MigrationAction.Skip: skipped++; break;
                    case MigrationAction.ChecksumMismatch: mismatches++; break;
                }
            }
        }

        // ── generate-from-assembly ─────────────────────────────────────────
        // C#-first authoring path (DESIGN-mermaid-portfolio.md).
        //   surrealforge generate-from-assembly <assembly.dll> --out-dir <dir>
        // Loads the assembly, scans every type with [SurrealTable], and writes
        // one .surql per table to <out-dir>. The emitted bytes are produced by
        // the same SurqlEmitter the Mermaid pipeline uses -- the only thing
        // that flips is the input source.
        private static int RunGenerateFromAssembly(CliArgs cli)
        {
            var assemblyPath = cli.Positionals.Count > 0
                ? cli.Positionals[0]
                : cli.SubCommand;
            if (string.IsNullOrEmpty(assemblyPath))
            {
                Console.Error.WriteLine("usage: surrealforge generate-from-assembly <assembly.dll> [--out-dir <dir>] [--filename-prefix-from <annotation>]");
                return 64;
            }
            if (!File.Exists(assemblyPath))
            {
                Console.Error.WriteLine($"assembly not found: {assemblyPath}");
                return 1;
            }
            // Default matches SurrealForgeOptions.GenerationOptions.GeneratedPath
            // + "Schemas/" so CLI invocations and source-gen consumers agree on the
            // location without per-invocation flags.
            var outDir = cli.Flag("out-dir") ?? "Persistence/SurrealDb/Generated/Schemas";
            Directory.CreateDirectory(outDir);

            // Default to idempotent DEFINE ... IF NOT EXISTS so re-applying the
            // generated scripts is safe. --strict opts into the legacy shape
            // (DEFINE without IF NOT EXISTS).
            var emitOptions = cli.HasFlag("strict")
                ? SurqlEmitter.EmitOptions.Strict
                : SurqlEmitter.EmitOptions.Default;

            var asm = Assembly.LoadFrom(Path.GetFullPath(assemblyPath));
            var pocoTypes = LoadTableTypes(asm);
            if (pocoTypes.Count == 0)
            {
                Console.Error.WriteLine($"warning: no types with [SurrealTable] found in {assemblyPath}");
                return 0;
            }

            int wrote = 0;
            foreach (var t in pocoTypes)
            {
                var model = AttributeSchemaScanner.ScanType(t);
                var surql = SurqlEmitter.Emit(model, emitOptions);
                var table = model.Entities[0].Name;
                var outPath = Path.Combine(outDir, table + ".surql");
                File.WriteAllText(outPath, surql);
                Console.WriteLine($"wrote {outPath} ({surql.Length} bytes)");
                wrote++;
            }
            Console.WriteLine($"emitted {wrote} .surql file(s) to {outDir}");
            return 0;
        }

        // ── flowcharts-from-assembly ──────────────────────────────────────
        // C#-first flowchart path. Loads an assembly, scans for [SurrealTable]
        // types, projects them onto the schema model, and emits per-slice +
        // master flowcharts. The output exactly matches `flowcharts` but
        // sources from attribute decoration rather than .mermaid files.
        private static int RunFlowchartsFromAssembly(CliArgs cli)
        {
            var assemblyPath = cli.Positionals.Count > 0
                ? cli.Positionals[0]
                : cli.SubCommand;
            if (string.IsNullOrEmpty(assemblyPath))
            {
                Console.Error.WriteLine("usage: surrealforge flowcharts-from-assembly <assembly.dll> [--out <dir>]");
                return 64;
            }
            if (!File.Exists(assemblyPath))
            {
                Console.Error.WriteLine($"assembly not found: {assemblyPath}");
                return 1;
            }
            // Default base lands at Persistence/SurrealDb/Generated; the emitter
            // writes Flowcharts/<slice>.flowchart.mermaid + domain.flowchart.mermaid
            // underneath. Aligned with SurrealForgeOptions.GenerationOptions.
            var output = cli.Flag("out") ?? "Persistence/SurrealDb/Generated";
            var flowchartDir = Path.Combine(output, "Flowcharts");
            Directory.CreateDirectory(flowchartDir);

            var asm = Assembly.LoadFrom(Path.GetFullPath(assemblyPath));
            var pocoTypes = LoadTableTypes(asm);
            if (pocoTypes.Count == 0)
            {
                Console.Error.WriteLine($"warning: no types with [SurrealTable] found in {assemblyPath}");
                return 0;
            }

            // ScanTypes -> one SchemaModel with every entity; the
            // flowchart emitter takes IEnumerable<SchemaModel>, so wrap.
            var combined = AttributeSchemaScanner.ScanTypes(pocoTypes);
            var result = MermaidFlowchartEmitter.EmitFromAttributeScan(new[] { combined });

            foreach (var kvp in result.SliceFiles)
            {
                File.WriteAllText(Path.Combine(flowchartDir, kvp.Key), kvp.Value);
            }
            var masterPath = Path.Combine(flowchartDir, "domain.flowchart.mermaid");
            File.WriteAllText(masterPath, result.MasterFlowchart);

            Console.WriteLine($"wrote {result.SliceFiles.Count} slice flowchart(s) to {flowchartDir}");
            foreach (var slice in result.SliceNames)
            {
                Console.WriteLine($"  - {slice}.flowchart.mermaid");
            }
            Console.WriteLine($"wrote master flowchart to {masterPath}");
            if (result.UnassignedEntities.Count > 0)
            {
                Console.Error.WriteLine($"warning: {result.UnassignedEntities.Count} entit(y/ies) without [Slice] -- clustered under '_unassigned':");
                foreach (var name in result.UnassignedEntities)
                {
                    Console.Error.WriteLine($"  - {name}");
                }
            }
            return 0;
        }

        private static List<Type> LoadTableTypes(Assembly asm)
        {
            Type[] all;
            try { all = asm.GetTypes(); }
            catch (ReflectionTypeLoadException rtle)
            {
                // Partial load: take the types that did resolve.
                all = rtle.Types.Where(t => t != null).Cast<Type>().ToArray();
            }
            var found = new List<Type>();
            foreach (var t in all)
            {
                if (t.GetCustomAttribute<SurrealTableAttribute>(inherit: false) != null)
                {
                    found.Add(t);
                }
            }
            // Stable order: by table name (which the scanner re-checks).
            found.Sort((a, b) =>
            {
                var an = a.GetCustomAttribute<SurrealTableAttribute>(inherit: false)!.Name;
                var bn = b.GetCustomAttribute<SurrealTableAttribute>(inherit: false)!.Name;
                return string.CompareOrdinal(an, bn);
            });
            return found;
        }

        // ── helpers ────────────────────────────────────────────────────────
        private static ISurrealConnection? BuildConnection(CliArgs cli)
        {
            var url = cli.Flag("connection") ?? Environment.GetEnvironmentVariable("SURREALFORGE_URL");
            var user = cli.Flag("user") ?? Environment.GetEnvironmentVariable("SURREALFORGE_USER");
            var pass = cli.Flag("pass") ?? Environment.GetEnvironmentVariable("SURREALFORGE_PASS");
            var ns = cli.Flag("namespace") ?? Environment.GetEnvironmentVariable("SURREALFORGE_NS");
            var db = cli.Flag("database") ?? Environment.GetEnvironmentVariable("SURREALFORGE_DB");

            if (string.IsNullOrWhiteSpace(url))
            {
                Console.Error.WriteLine("missing connection URL. Set --connection or SURREALFORGE_URL.");
                return null;
            }
            if (string.IsNullOrWhiteSpace(ns) || string.IsNullOrWhiteSpace(db))
            {
                Console.Error.WriteLine("missing namespace/database. Set --namespace / --database or SURREALFORGE_NS / _DB.");
                return null;
            }

            return new HttpConnectionAdapter(url, user ?? "", pass ?? "", ns!, db!);
        }

        private static void PrintHelp()
        {
            Console.WriteLine("surrealforge -- SurrealDB schema CLI (C#-first)");
            Console.WriteLine();
            Console.WriteLine("  surrealforge up                 Apply Generated/Schemas/ then Migrations/");
            Console.WriteLine("                                   (canonical 'bring DB in sync' entry point)");
            Console.WriteLine("  surrealforge reset              Wipe namespace + re-apply all migrations");
            Console.WriteLine("                                   (dev only; destructive -- use SURREALFORGE_SKIP_RESET=1 to skip)");
            Console.WriteLine("  surrealforge migrate up         Same as up, but only one directory (--dir)");
            Console.WriteLine("  surrealforge migrate down       (stub) Refuses; manual rollback only");
            Console.WriteLine("  surrealforge migrate status     Read schema_migration table");
            Console.WriteLine("  surrealforge migrate dry-run    Plan only; zero writes");
            Console.WriteLine("  surrealforge generate-from-assembly <dll>");
            Console.WriteLine("                                   Scan [SurrealTable] POCOs in <dll>; emit .surql per table");
            Console.WriteLine("  surrealforge flowcharts-from-assembly <dll>");
            Console.WriteLine("                                   Emit per-slice + master graph-LR flowcharts");
            Console.WriteLine();
            Console.WriteLine("generate-from-assembly flags:");
            Console.WriteLine("  --out-dir <dir>  .surql output dir (default: Persistence/SurrealDb/Generated/Schemas)");
            Console.WriteLine("  --strict         Emit DEFINE without IF NOT EXISTS (default: idempotent)");
            Console.WriteLine();
            Console.WriteLine("flowcharts-from-assembly flags:");
            Console.WriteLine("  --out <dir>      Base output dir (default: Persistence/SurrealDb/Generated; emits Flowcharts/ subdirectory)");
            Console.WriteLine();
            Console.WriteLine("Connection flags / env vars (env in parens):");
            Console.WriteLine("  --connection  (SURREALFORGE_URL)   http(s)://host:port");
            Console.WriteLine("  --user        (SURREALFORGE_USER)");
            Console.WriteLine("  --pass        (SURREALFORGE_PASS)");
            Console.WriteLine("  --namespace   (SURREALFORGE_NS)");
            Console.WriteLine("  --database    (SURREALFORGE_DB)");
            Console.WriteLine();
            Console.WriteLine("Up / reset flags:");
            Console.WriteLine("  --schemas-dir <p>    Generated schemas dir (default: Persistence/SurrealDb/Generated/Schemas)");
            Console.WriteLine("  --migrations-dir <p> Hand-authored migrations dir (default: Persistence/SurrealDb/Migrations)");
            Console.WriteLine("  --dry-run            Plan without writing -- prints the reconcile diff plan (up only)");
            Console.WriteLine("  --force              Overwrite recorded checksum on mismatch AND force a reconcile pass (up only)");
            Console.WriteLine("  --no-reconcile       Skip Phase 3 model-driven field evolution (legacy additive-only apply)");
            Console.WriteLine("  --allow-destructive  Apply destructive reconcile changes (field/table/index drops, narrowing type changes)");
            Console.WriteLine("  --assembly <dll>     Reconcile desired-model source: scan [SurrealTable] POCOs (default: parse schemas-dir .surql)");
            Console.WriteLine("  --applied-by <s>     Identity recorded in schema_migration.applied_by");
            Console.WriteLine();
            Console.WriteLine("  Phase 3 reconcile: after applying schema files + migrations, `up` introspects the");
            Console.WriteLine("  live DB, diffs it against the desired model, and emits DEFINE ... OVERWRITE for any");
            Console.WriteLine("  drifted field TYPE/DEFAULT/ASSERT/index -- the change IF NOT EXISTS cannot express.");
            Console.WriteLine();
            Console.WriteLine("Migrate flags:");
            Console.WriteLine("  --dir <path>     Dir of .surql files (default: Persistence/SurrealDb/Generated/Schemas)");
            Console.WriteLine("  --force          Overwrite recorded checksum on mismatch");
            Console.WriteLine("  --dry-run        Plan without writing");
            Console.WriteLine("  --applied-by <s> Identity recorded in schema_migration.applied_by");
        }
    }
}
