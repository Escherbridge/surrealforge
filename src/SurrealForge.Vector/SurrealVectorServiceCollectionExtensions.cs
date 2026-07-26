// SPDX-License-Identifier: MIT
// SurrealForge.Vector -- DI entry point. See AGENTS.md §DI wiring.

using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace SurrealForge.Vector;

/// <summary>Registers vector-search services: encoder profiles, embedding cache, and backfill jobs.</summary>
public static class SurrealVectorServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="SurrealVectorOptions"/>, an in-memory
    /// <see cref="IEmbeddingCache"/> (TryAdd — register your own first to
    /// override), the <see cref="VectorEncoderRegistry"/>, and the
    /// <see cref="EmbeddingBackfillService"/> hosted worker. Pair with
    /// <c>AddSurrealForge</c> from SurrealForge.Client for the executor.
    /// </summary>
    public static IServiceCollection AddSurrealVectorSearch(
        this IServiceCollection services,
        Action<SurrealVectorOptions> configure)
    {
        if (services is null) throw new ArgumentNullException(nameof(services));
        if (configure is null) throw new ArgumentNullException(nameof(configure));

        var options = new SurrealVectorOptions();
        configure(options);

        services.AddSingleton(options);
        services.TryAddSingleton<IEmbeddingCache, InMemoryEmbeddingCache>();
        services.TryAddSingleton<VectorEncoderRegistry>();
        services.AddHostedService<EmbeddingBackfillService>();

        return services;
    }
}
