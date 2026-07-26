// SPDX-License-Identifier: MIT
// SurrealForge.Vector -- AddSurrealVectorSearch configuration surface.
// See AGENTS.md §DI wiring.

using System;
using System.Collections.Generic;
using SurrealForge.Client.Query;
using SurrealForge.Client.Schema;

namespace SurrealForge.Vector;

/// <summary>Configuration collected by <see cref="SurrealVectorServiceCollectionExtensions.AddSurrealVectorSearch"/>.</summary>
public sealed class SurrealVectorOptions
{
    /// <summary>The profile name used when an [Embedded] attribute names none.</summary>
    public const string DefaultProfile = "default";

    internal Dictionary<string, Func<IServiceProvider, IVectorEncoder>> EncoderFactories { get; } =
        new Dictionary<string, Func<IServiceProvider, IVectorEncoder>>(StringComparer.Ordinal);

    internal List<EmbeddingBackfillJob> JobList { get; } = new List<EmbeddingBackfillJob>();

    /// <summary>The configured backfill jobs (read-only view; add via <see cref="AddJob"/> / <see cref="AddJobsFrom{T}"/>).</summary>
    public IReadOnlyList<EmbeddingBackfillJob> Jobs => JobList;

    /// <summary>
    /// Optional per-job executor factory (the "dedicated forge" seam). Null =
    /// every job shares the container's <see cref="ISurrealExecutor"/>.
    /// </summary>
    public Func<IServiceProvider, ISurrealExecutor>? ExecutorFactory { get; set; }

    /// <summary>Register the default-profile encoder.</summary>
    public SurrealVectorOptions AddEncoder(Func<IServiceProvider, IVectorEncoder> factory)
        => AddEncoder(DefaultProfile, factory);

    /// <summary>Register the default-profile encoder instance.</summary>
    public SurrealVectorOptions AddEncoder(IVectorEncoder encoder)
        => AddEncoder(DefaultProfile, encoder);

    /// <summary>Register a named-profile encoder instance.</summary>
    public SurrealVectorOptions AddEncoder(string profile, IVectorEncoder encoder)
    {
        if (encoder is null) throw new ArgumentNullException(nameof(encoder));
        return AddEncoder(profile, _ => encoder);
    }

    /// <summary>Register a named-profile encoder factory. Duplicate profiles throw (drift is a bug).</summary>
    public SurrealVectorOptions AddEncoder(string profile, Func<IServiceProvider, IVectorEncoder> factory)
    {
        if (string.IsNullOrWhiteSpace(profile))
            throw new ArgumentException("Encoder profile name must not be empty.", nameof(profile));
        if (factory is null) throw new ArgumentNullException(nameof(factory));
        if (EncoderFactories.ContainsKey(profile))
            throw new InvalidOperationException(
                "Encoder profile '" + profile + "' is already registered; refusing to overwrite.");
        EncoderFactories[profile] = factory;
        return this;
    }

    /// <summary>Add an explicit backfill job.</summary>
    public SurrealVectorOptions AddJob(EmbeddingBackfillJob job)
    {
        if (job is null) throw new ArgumentNullException(nameof(job));
        JobList.Add(job);
        return this;
    }

    /// <summary>
    /// Scan <typeparamref name="T"/> for [Embedded] declarations and register a
    /// backfill job per <see cref="EmbeddingMode.Batched"/> field: incremental
    /// by default, or the supplied per-definition factory's choice.
    /// </summary>
    public SurrealVectorOptions AddJobsFrom<T>(
        Func<EmbeddingJobDefinition, EmbeddingBackfillJob>? jobFactory = null)
    {
        foreach (var def in EmbeddingSchemaScanner.Scan<T>())
        {
            if (def.Mode != EmbeddingMode.Batched) continue;
            JobList.Add(jobFactory is null
                ? EmbeddingBackfillJob.CreateIncremental(def)
                : jobFactory(def));
        }
        return this;
    }
}

/// <summary>Resolves <see cref="IVectorEncoder"/>s by profile name (cached per profile).</summary>
public sealed class VectorEncoderRegistry
{
    private readonly IServiceProvider _services;
    private readonly SurrealVectorOptions _options;
    private readonly Dictionary<string, IVectorEncoder> _resolved =
        new Dictionary<string, IVectorEncoder>(StringComparer.Ordinal);
    private readonly object _gate = new object();

    public VectorEncoderRegistry(IServiceProvider services, SurrealVectorOptions options)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>Resolve a profile (null/empty = default). Throws with the known-profile list on a miss.</summary>
    public IVectorEncoder Resolve(string? profile = null)
    {
        var key = string.IsNullOrWhiteSpace(profile) ? SurrealVectorOptions.DefaultProfile : profile!;
        lock (_gate)
        {
            if (_resolved.TryGetValue(key, out var cached)) return cached;
            if (!_options.EncoderFactories.TryGetValue(key, out var factory))
                throw new InvalidOperationException(
                    "No IVectorEncoder registered for profile '" + key + "'. Register one via " +
                    "AddSurrealVectorSearch(o => o.AddEncoder(\"" + key + "\", ...)). Known profiles: " +
                    (_options.EncoderFactories.Count == 0
                        ? "(none)"
                        : string.Join(", ", _options.EncoderFactories.Keys)) + ".");
            var encoder = factory(_services);
            _resolved[key] = encoder;
            return encoder;
        }
    }
}
