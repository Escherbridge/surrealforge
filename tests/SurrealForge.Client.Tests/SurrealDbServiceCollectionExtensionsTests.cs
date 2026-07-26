// SPDX-License-Identifier: MIT
// AddSurrealForge registration smoke tests.

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SurrealForge.Client;
using SurrealForge.Client.Connection;
using SurrealForge.Client.Query;
using SurrealForge.Client.Transaction;

namespace SurrealForge.Client.Tests;

public sealed class SurrealDbServiceCollectionExtensionsTests
{
    [Fact]
    public void AddSurrealForge_registers_options_connection_and_executor()
    {
        var services = new ServiceCollection();
        services.AddSurrealForge(o =>
        {
            o.Endpoint = "http://localhost:9999";
            o.Namespace = "testns";
            o.Database = "testdb";
        });

        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<SurrealConnectionOptions>();
        options.Endpoint.Should().Be("http://localhost:9999");
        options.Namespace.Should().Be("testns");
        options.Database.Should().Be("testdb");

        provider.GetRequiredService<ISurrealConnection>().Should().BeOfType<HttpSurrealConnection>();
        provider.GetRequiredService<ISurrealExecutor>().Should().BeOfType<DefaultSurrealExecutor>();

        // Singletons: repeated resolution yields the same instances.
        provider.GetRequiredService<ISurrealConnection>()
            .Should().BeSameAs(provider.GetRequiredService<ISurrealConnection>());
        provider.GetRequiredService<ISurrealExecutor>()
            .Should().BeSameAs(provider.GetRequiredService<ISurrealExecutor>());
    }

    [Fact]
    public void AddSurrealForge_uses_TryAdd_so_consumer_registrations_win()
    {
        var services = new ServiceCollection();
        var custom = new FakeConnection();
        services.AddSingleton<ISurrealConnection>(custom);
        services.AddSurrealForge(o => o.Endpoint = "http://localhost:9999");

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<ISurrealConnection>().Should().BeSameAs(custom);
    }

    private sealed class FakeConnection : ISurrealConnection
    {
        public Task UseAsync(string ns, string db, CancellationToken ct = default) => Task.CompletedTask;
        public Task<SurrealResponse> ExecuteRawAsync(string sql, object? parameters = null, CancellationToken ct = default)
            => Task.FromResult(SurrealResponse.BufferedAck());
        public Task<ISurrealTransaction> BeginTransactionAsync(CancellationToken ct = default)
            => throw new NotSupportedException();
        public ValueTask DisposeAsync() => default;
        public void Dispose() { }
    }
}
