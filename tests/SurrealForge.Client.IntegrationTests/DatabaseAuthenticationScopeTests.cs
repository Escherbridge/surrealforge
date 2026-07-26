// SPDX-License-Identifier: MIT
using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using SurrealForge.Client;
using SurrealForge.Client.Connection;

namespace SurrealForge.Client.IntegrationTests;

/// <summary>Live coverage for database-scoped HTTP Basic authentication.</summary>
[Collection("LiveSurrealDb")]
public sealed class DatabaseAuthenticationScopeTests
{
    private readonly LiveSurrealDbCollectionFixture _fixture;

    public DatabaseAuthenticationScopeTests(LiveSurrealDbCollectionFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Database_auth_scope_authenticates_a_database_system_user()
    {
        if (!_fixture.SurrealAvailable)
        {
            Console.WriteLine($"[SKIP] LiveSurrealDb unavailable: {_fixture.SkipReason}");
            return;
        }

        var suffix = Guid.NewGuid().ToString("N");
        var namespaceName = $"authscope{suffix}";
        const string database = "runtime";
        const string user = "runtime";
        var password = $"runtime{suffix}";

        try
        {
            // raw: live isolated system-user bootstrap; DDL identifiers cannot be parameters.
            await EnsureRootSqlAsync($"DEFINE NAMESPACE {namespaceName}");
            await EnsureRootSqlAsync($"DEFINE DATABASE {database}", namespaceName, database);
            await EnsureRootSqlAsync(
                $"DEFINE USER {user} ON DATABASE PASSWORD '{password}' ROLES EDITOR",
                namespaceName,
                database);

            await using var connection = new HttpSurrealConnection(new HttpClientHandler(), new SurrealConnectionOptions
            {
                Endpoint = _fixture.Endpoint,
                Namespace = namespaceName,
                Database = database,
                User = user,
                Password = password,
                AuthenticationScope = SurrealAuthenticationScope.Database,
                MaxRetries = 1,
            });

            var response = await connection.ExecuteRawAsync(
                "DEFINE TABLE runtime_probe SCHEMALESS; " +
                "CREATE runtime_probe:one SET state = 'created'; " +
                "SELECT state FROM runtime_probe:one");
            response.EnsureAllOk();
            response.GetValues<System.Text.Json.JsonElement>(2).Should().ContainSingle();
        }
        finally
        {
            // raw: cleanup identifier is server-generated and cannot refer to a shared namespace.
            await RemoveNamespaceIfPresentAsync(namespaceName);
        }
    }

    private async Task EnsureRootSqlAsync(string sql, string? namespaceName = null, string? database = null)
    {
        using var client = CreateRootClient(namespaceName, database);
        using var response = await client.PostAsync("/sql", new StringContent(sql, Encoding.UTF8, "text/plain"));
        var payload = await response.Content.ReadAsStringAsync();
        response.IsSuccessStatusCode.Should().BeTrue(payload);
        payload.Should().NotContain("\"status\":\"ERR\"", payload);
    }

    private async Task RemoveNamespaceIfPresentAsync(string namespaceName)
    {
        using var client = CreateRootClient();
        using var response = await client.PostAsync(
            "/sql",
            new StringContent($"REMOVE NAMESPACE IF EXISTS {namespaceName}", Encoding.UTF8, "text/plain"));
        var payload = await response.Content.ReadAsStringAsync();
        response.IsSuccessStatusCode.Should().BeTrue(payload);
        payload.Should().NotContain("\"status\":\"ERR\"", payload);
    }

    private HttpClient CreateRootClient(string? namespaceName = null, string? database = null)
    {
        var client = new HttpClient { BaseAddress = new Uri(_fixture.Endpoint) };
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_fixture.User}:{_fixture.Password}"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        if (namespaceName is not null)
            client.DefaultRequestHeaders.Add("Surreal-NS", namespaceName);
        if (database is not null)
            client.DefaultRequestHeaders.Add("Surreal-DB", database);
        return client;
    }
}
