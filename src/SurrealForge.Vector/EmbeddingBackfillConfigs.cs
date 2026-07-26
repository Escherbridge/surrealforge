// SPDX-License-Identifier: MIT
// SurrealForge.Vector -- per-job backfill config conventions (Incremental /
// AdHoc / Dynamic). Semantics + rationale: AGENTS.md §Backfill jobs.

using System;
using System.Collections.Generic;
using System.Threading;
using SurrealForge.Client.Query;

namespace SurrealForge.Vector;

/// <summary>Checkpointed pages over the whole table; resumable; runs until caught up.</summary>
public sealed class IncrementalBackfillConfig
{
    /// <summary>Rows per page / rows per batch-encode. Default 64.</summary>
    public int BatchSize { get; set; } = 64;

    /// <summary>Ledger table persisting the per-job cursor. Default <c>surrealforge_embedding_checkpoint</c>.</summary>
    public string CheckpointTable { get; set; } = "surrealforge_embedding_checkpoint";
}

/// <summary>Run-once pass over an explicit set (<see cref="Ids"/>) or range (<see cref="Filter"/>); whole table when both are null.</summary>
public sealed class AdHocBackfillConfig
{
    /// <summary>Rows per page / rows per batch-encode. Default 64.</summary>
    public int BatchSize { get; set; } = 64;

    /// <summary>Explicit record ids (bare keys or <c>table:key</c> strings). Takes precedence over <see cref="Filter"/>.</summary>
    public IReadOnlyList<string>? Ids { get; set; }

    /// <summary>Optional predicate fragment scoping the range (params merged; must not bind <c>$_tbl</c>/<c>$_cursor</c>).</summary>
    public SurrealQuery? Filter { get; set; }

    /// <summary>Force re-embedding even when the stored content hash matches (model-upgrade path). Default false.</summary>
    public bool Reembed { get; set; }
}

/// <summary>One changed source row surfaced by a live feed to a Dynamic job.</summary>
public sealed class EmbeddingSourceChange
{
    /// <summary>Record id (<c>table:key</c> or bare key).</summary>
    public string Id { get; }

    /// <summary>Current source text; null clears nothing and is skipped.</summary>
    public string? Text { get; }

    public EmbeddingSourceChange(string id, string? text)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Id must not be empty.", nameof(id));
        Id = id;
        Text = text;
    }
}

/// <summary>LIVE-query-driven: embeds rows as they change. Requires a <see cref="LiveSource"/> feed.</summary>
public sealed class DynamicBackfillConfig
{
    /// <summary>Max rows per batch-encode when the feed bursts. Default 16.</summary>
    public int BatchSize { get; set; } = 16;

    /// <summary>
    /// The change feed — typically an adapter over
    /// <c>WebSocketSurrealConnection.LiveAsync</c>. When null the runner
    /// throws <see cref="NotSupportedException"/>; first-class LIVE wiring
    /// ships with Phase 2. See AGENTS.md §Backfill jobs / Dynamic.
    /// </summary>
    public Func<CancellationToken, IAsyncEnumerable<EmbeddingSourceChange>>? LiveSource { get; set; }
}

/// <summary>A job definition paired with exactly one backfill config (one job = one config = one forge).</summary>
public sealed class EmbeddingBackfillJob
{
    /// <summary>The schema-derived job description.</summary>
    public EmbeddingJobDefinition Definition { get; }

    /// <summary>Set when this is an incremental job.</summary>
    public IncrementalBackfillConfig? Incremental { get; }

    /// <summary>Set when this is an ad-hoc job.</summary>
    public AdHocBackfillConfig? AdHoc { get; }

    /// <summary>Set when this is a dynamic job.</summary>
    public DynamicBackfillConfig? Dynamic { get; }

    private EmbeddingBackfillJob(
        EmbeddingJobDefinition definition,
        IncrementalBackfillConfig? incremental,
        AdHocBackfillConfig? adHoc,
        DynamicBackfillConfig? dynamic)
    {
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        Incremental = incremental;
        AdHoc = adHoc;
        Dynamic = dynamic;
    }

    /// <summary>Wrap a definition as a checkpointed incremental job.</summary>
    public static EmbeddingBackfillJob CreateIncremental(
        EmbeddingJobDefinition definition, IncrementalBackfillConfig? config = null)
        => new EmbeddingBackfillJob(definition, config ?? new IncrementalBackfillConfig(), null, null);

    /// <summary>Wrap a definition as a run-once ad-hoc job.</summary>
    public static EmbeddingBackfillJob CreateAdHoc(
        EmbeddingJobDefinition definition, AdHocBackfillConfig? config = null)
        => new EmbeddingBackfillJob(definition, null, config ?? new AdHocBackfillConfig(), null);

    /// <summary>Wrap a definition as a live-feed dynamic job.</summary>
    public static EmbeddingBackfillJob CreateDynamic(
        EmbeddingJobDefinition definition, DynamicBackfillConfig? config = null)
        => new EmbeddingBackfillJob(definition, null, null, config ?? new DynamicBackfillConfig());
}
