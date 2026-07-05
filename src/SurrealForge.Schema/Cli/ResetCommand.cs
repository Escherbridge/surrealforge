// SPDX-License-Identifier: UNLICENSED
// SurrealForge.Schema -- `surrealforge reset` verb.
//
// Wipes the configured SurrealDB namespace and re-applies all migrations so
// a development environment can be returned to a known-clean state in one
// command.
//
// Logic:
//   1. Connect using the standard connection helper (flags / env vars).
//   2. REMOVE NAMESPACE IF EXISTS <ns>   -- drops all databases + tables inside.
//   3. DEFINE NAMESPACE <ns>             -- recreate the empty namespace.
//   4. USE NS <ns> DB <db>               -- scope subsequent DDL.
//   5. Invoke the same two-phase apply as `up` (schemas then migrations).
//   6. Print "[reset] wiped ns=<ns>, ran <N> migrations".
//   7. Exit 0 on success, non-zero on failure.
//
// Safety:
//   This is a destructive operation intentionally scoped to dev. The
//   SURREALFORGE_SKIP_RESET=1 env var lets dev-up.ps1 (or any caller) skip it
//   without changing the launch script.

using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using SurrealForge.Schema.Migration;

namespace SurrealForge.Schema.Cli
{
    /// <summary>
    /// Implements the <c>reset</c> top-level verb: wipe the configured
    /// SurrealDB namespace and re-apply all schema + migration files.
    /// </summary>
    public static class ResetCommand
    {
        /// <summary>
        /// Entry point invoked by <c>Program.Main</c> when the command is
        /// <c>reset</c>. Returns the OS exit code.
        /// </summary>
        public static async Task<int> RunAsync(CliArgs cli)
        {
            var url  = cli.Flag("connection") ?? Environment.GetEnvironmentVariable("SURREALFORGE_URL");
            var user = cli.Flag("user")       ?? Environment.GetEnvironmentVariable("SURREALFORGE_USER");
            var pass = cli.Flag("pass")       ?? Environment.GetEnvironmentVariable("SURREALFORGE_PASS");
            var ns   = cli.Flag("namespace")  ?? Environment.GetEnvironmentVariable("SURREALFORGE_NS");
            var db   = cli.Flag("database")   ?? Environment.GetEnvironmentVariable("SURREALFORGE_DB");

            if (string.IsNullOrWhiteSpace(url))
            {
                Console.Error.WriteLine("[reset] missing connection URL. Set --connection or SURREALFORGE_URL.");
                return 64;
            }
            if (string.IsNullOrWhiteSpace(ns) || string.IsNullOrWhiteSpace(db))
            {
                Console.Error.WriteLine("[reset] missing namespace/database. Set --namespace / --database or SURREALFORGE_NS / _DB.");
                return 64;
            }

            // The reset needs a root-scoped connection (no NS/DB header) so
            // REMOVE NAMESPACE is allowed by SurrealDB. We pass empty strings
            // for ns/db during the wipe phase then re-scope for the apply phase.
            // HttpConnectionAdapter sends the NS/DB headers; for REMOVE NAMESPACE
            // we need a connection without them, so we open a second adapter
            // scoped to root (ns="" db="").
            var rootConn = new HttpConnectionAdapter(url, user ?? string.Empty, pass ?? string.Empty, string.Empty, string.Empty);
            try
            {
                // Step 1: wipe the namespace.
                Console.WriteLine($"[reset] removing namespace '{ns}' ...");
                var wipeSql = BuildWipeSql(ns!, db!);
                var wipeResult = await rootConn.ExecuteAsync(wipeSql).ConfigureAwait(false);
                if (!wipeResult.IsOk)
                {
                    Console.Error.WriteLine($"[reset] REMOVE NAMESPACE failed: {wipeResult.Detail}");
                    return 3;
                }
                Console.WriteLine($"[reset] namespace '{ns}' wiped.");
            }
            finally
            {
                rootConn.Dispose();
            }

            // Step 2: re-apply migrations using the same two-phase `up` logic,
            // scoped to the freshly-created namespace + database.
            var schemasDir    = cli.Flag("schemas-dir")    ?? "Persistence/SurrealDb/Generated/Schemas";
            var migrationsDir = cli.Flag("migrations-dir") ?? "Persistence/SurrealDb/Migrations";

            if (!Directory.Exists(schemasDir))
            {
                Console.Error.WriteLine($"[reset] schemas directory not found: {schemasDir}");
                return 1;
            }
            var schemaFiles = MigrationRunner.DiscoverFiles(schemasDir);
            if (schemaFiles.Count == 0)
            {
                Console.Error.WriteLine($"[reset] no .surql files found under {schemasDir}");
                return 1;
            }
            System.Collections.Generic.IReadOnlyList<MigrationFile> migrationFiles = Array.Empty<MigrationFile>();
            if (Directory.Exists(migrationsDir))
            {
                migrationFiles = MigrationRunner.DiscoverFiles(migrationsDir);
            }

            var conn = new HttpConnectionAdapter(url, user ?? string.Empty, pass ?? string.Empty, ns!, db!);
            try
            {
                var runner = new MigrationRunner(
                    conn,
                    cli.Flag("applied-by") ?? "surrealforge/reset",
                    ensureNamespace: ns,
                    ensureDatabase: db);

                int applied = 0, skipped = 0;

                var schemaPlan = await runner.ApplyAsync(schemaFiles, dryRun: false, force: false).ConfigureAwait(false);
                foreach (var p in schemaPlan)
                {
                    if (p.Action == MigrationAction.Apply) applied++;
                    else skipped++;
                }

                if (migrationFiles.Count > 0)
                {
                    var migrPlan = await runner.ApplyAsync(migrationFiles, dryRun: false, force: false).ConfigureAwait(false);
                    foreach (var p in migrPlan)
                    {
                        if (p.Action == MigrationAction.Apply) applied++;
                        else skipped++;
                    }
                }

                Console.WriteLine($"[reset] wiped ns={ns}, ran {applied} migration(s) ({skipped} skipped)");
                return 0;
            }
            finally
            {
                conn.Dispose();
            }
        }

        // ── helpers ─────────────────────────────────────────────────────────

        /// <summary>
        /// Builds the wipe + recreate DDL for the given namespace and database.
        /// Uses a root-scoped connection (no NS/DB header) so the REMOVE is
        /// permitted by SurrealDB.
        /// </summary>
        private static string BuildWipeSql(string ns, string db)
        {
            // Sanitize identifiers: letters/digits/underscores only.
            var safeNs = Sanitize(ns);
            var safeDb = Sanitize(db);
            var sb = new StringBuilder();
            sb.Append("REMOVE NAMESPACE IF EXISTS ").Append(safeNs).Append(";\n");
            sb.Append("DEFINE NAMESPACE ").Append(safeNs).Append(";\n");
            sb.Append("USE NS ").Append(safeNs).Append(";\n");
            sb.Append("DEFINE DATABASE ").Append(safeDb).Append(";\n");
            return sb.ToString();
        }

        private static string Sanitize(string value)
        {
            var sb = new StringBuilder(value.Length);
            foreach (var ch in value)
            {
                if (char.IsLetterOrDigit(ch) || ch == '_') sb.Append(ch);
                else sb.Append('_');
            }
            if (sb.Length > 0 && char.IsDigit(sb[0])) sb.Insert(0, "s_");
            return sb.ToString();
        }
    }
}
