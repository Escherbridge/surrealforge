using System;
using System.Net.Http;

namespace SurrealForge.Client.IntegrationTests;

/// <summary>
/// xUnit collection fixture for live-SurrealDB integration tests.
///
/// Tests run against the developer's local SurrealDB instance — same endpoint
/// and credentials as <c>appsettings.Development.json</c>'s <c>SurrealDb</c>
/// section. No container scripts are invoked; start your local instance before
/// running this collection.
///
/// If the local instance is unreachable, <see cref="SurrealAvailable"/> is
/// <c>false</c> and every test in the collection early-returns (counted as
/// passing), matching the pass-off gate contract.
/// </summary>
public sealed class LiveSurrealDbCollectionFixture : IDisposable
{
    /// <summary>SurrealDB HTTP endpoint for tests in this collection.</summary>
    public string Endpoint { get; } = "http://127.0.0.1:8000";

    /// <summary>Root user for the local SurrealDB instance.</summary>
    public string User { get; } = "root";

    /// <summary>Root password for the local SurrealDB instance.</summary>
    public string Password { get; } = "root";

    /// <summary>Default namespace for the fixture's live tests.</summary>
    public string Namespace { get; } = "test";

    /// <summary>Default database for the fixture's live tests.</summary>
    public string Database { get; } = "client_integration";

    /// <summary>
    /// True iff the local SurrealDB instance is reachable and <c>/health</c>
    /// returns 200. Tests gracefully early-return when this is false rather
    /// than failing — mirrors the pass-off gate's section 9 contract.
    /// </summary>
    public bool SurrealAvailable { get; }

    /// <summary>Human-readable reason when <see cref="SurrealAvailable"/> is false.</summary>
    public string? SkipReason { get; }

    public LiveSurrealDbCollectionFixture()
    {
        SurrealAvailable = ProbeHealth(Endpoint);
        if (!SurrealAvailable)
        {
            SkipReason = $"local SurrealDB at {Endpoint} unreachable — start your local instance and retry.";
            return;
        }

        // SurrealDB 3.x strictly rejects scoped queries against a namespace
        // that does not exist yet (1.5.x auto-materialized it). Bootstrap the
        // fixture's namespace/database once so the per-test CREATE/SELECT
        // round-trips have a scope to target.
        EnsureNamespace(Endpoint, User, Password, Namespace, Database);
    }

    public void Dispose()
    {
        // Nothing to tear down — tests run against the developer's local instance
        // which persists across runs independently of this fixture.
    }

    /// <summary>
    /// Define an ad-hoc database under the fixture namespace. SurrealDB 3.x
    /// rejects scoped CREATE/SELECT against a database that does not exist yet
    /// (1.5.x auto-created it on first write), so per-test random databases
    /// must be defined before use.
    /// </summary>
    public void EnsureDatabase(string db) =>
        EnsureNamespace(Endpoint, User, Password, Namespace, db);

    private static void EnsureNamespace(string baseUrl, string user, string pass, string ns, string db)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var basic = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{user}:{pass}"));
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", basic);
            client.DefaultRequestHeaders.Add("Accept", "application/json");

            var sql =
                $"DEFINE NAMESPACE IF NOT EXISTS {ns}; " +
                $"USE NS {ns}; DEFINE DATABASE IF NOT EXISTS {db};";
            using var content = new StringContent(sql, System.Text.Encoding.UTF8, "text/plain");
            client.PostAsync(baseUrl.TrimEnd('/') + "/sql", content).GetAwaiter().GetResult();
        }
        catch
        {
            // Best-effort: if the bootstrap fails the scoped tests will surface
            // the real error, and the probe already confirmed reachability.
        }
    }

    private static bool ProbeHealth(string baseUrl)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var resp = client.GetAsync(baseUrl.TrimEnd('/') + "/health").GetAwaiter().GetResult();
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>xUnit collection marker for the live-SurrealDB fixture.</summary>
[CollectionDefinition("LiveSurrealDb")]
public sealed class LiveSurrealDbCollection : ICollectionFixture<LiveSurrealDbCollectionFixture>
{
    // Marker only — xUnit supplies the runtime collection identity.
}
