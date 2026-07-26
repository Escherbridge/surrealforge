// SPDX-License-Identifier: MIT
// SurrealForge.Client -- DI entry point (the `AddSurrealForge` referenced by
// Query/DefaultSurrealExecutor.cs). Rationale: DESIGN.md §Package topology in
// src/SurrealForge.Vector.

using System;
using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SurrealForge.Client.Connection;
using SurrealForge.Client.Query;

namespace SurrealForge.Client;

/// <summary>Registers SurrealForge's connection + executor services in a DI container.</summary>
public static class SurrealDbServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="SurrealConnectionOptions"/> (from
    /// <paramref name="configure"/>), a singleton HTTP
    /// <see cref="ISurrealConnection"/>, and a singleton
    /// <see cref="ISurrealExecutor"/> (<see cref="DefaultSurrealExecutor"/>).
    /// Connection/executor use TryAdd so a consumer registration wins.
    /// </summary>
    public static IServiceCollection AddSurrealForge(
        this IServiceCollection services,
        Action<SurrealConnectionOptions> configure)
    {
        if (services is null) throw new ArgumentNullException(nameof(services));
        if (configure is null) throw new ArgumentNullException(nameof(configure));

        var options = new SurrealConnectionOptions();
        configure(options);
        return AddSurrealForge(services, options);
    }

    /// <summary>Overload taking an already-built options instance.</summary>
    public static IServiceCollection AddSurrealForge(
        this IServiceCollection services,
        SurrealConnectionOptions options)
    {
        if (services is null) throw new ArgumentNullException(nameof(services));
        if (options is null) throw new ArgumentNullException(nameof(options));

        services.TryAddSingleton(options);

        // Owns its HttpClient (handler ctor) so disposing the singleton at
        // container teardown releases the socket pool. See AGENTS.md pointer
        // in src/SurrealForge.Vector/AGENTS.md §DI split.
        services.TryAddSingleton<ISurrealConnection>(sp =>
            new HttpSurrealConnection(
                new HttpClientHandler(),
                sp.GetRequiredService<SurrealConnectionOptions>()));

        services.TryAddSingleton<ISurrealExecutor>(sp =>
            new DefaultSurrealExecutor(sp.GetRequiredService<ISurrealConnection>()));

        return services;
    }
}
