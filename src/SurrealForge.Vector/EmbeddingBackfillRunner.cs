// SPDX-License-Identifier: MIT
// SurrealForge.Vector -- the backfill engine: bounded-channel producer/consumer
// draining via batch EncodeAsync, idempotent via content-hash skip.
// Cursor-vs-checkpoint semantics + SQL shapes: AGENTS.md §Backfill jobs.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using SurrealForge.Client.Query;

namespace SurrealForge.Vector;

/// <summary>Counters for one backfill run.</summary>
public sealed class EmbeddingBackfillReport
{
    /// <summary>Rows read from the source (including skipped ones).</summary>
    public int RowsScanned { get; internal set; }

    /// <summary>Rows skipped because the stored content hash already matched (or the text was empty).</summary>
    public int RowsSkipped { get; internal set; }

    /// <summary>Rows whose vector + hash columns were written.</summary>
    public int RowsEmbedded { get; internal set; }

    /// <summary>Number of flushed batches (each at most one batch EncodeAsync call).</summary>
    public int BatchesFlushed { get; internal set; }
}

/// <summary>Runs one embedding backfill job (Incremental / AdHoc / Dynamic) against an <see cref="ISurrealExecutor"/>.</summary>
public sealed class EmbeddingBackfillRunner
{
    private readonly ISurrealExecutor _executor;
    private readonly IVectorEncoder _encoder;
    private readonly IEmbeddingCache _cache;

    public EmbeddingBackfillRunner(ISurrealExecutor executor, IVectorEncoder encoder, IEmbeddingCache cache)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _encoder = encoder ?? throw new ArgumentNullException(nameof(encoder));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    /// <summary>Dispatch on the job's config kind.</summary>
    public Task<EmbeddingBackfillReport> RunAsync(EmbeddingBackfillJob job, CancellationToken ct = default)
    {
        if (job is null) throw new ArgumentNullException(nameof(job));
        if (job.Incremental is not null) return RunIncrementalAsync(job.Definition, job.Incremental, ct);
        if (job.AdHoc is not null) return RunAdHocAsync(job.Definition, job.AdHoc, ct);
        return RunDynamicAsync(job.Definition, job.Dynamic!, ct);
    }

    // ─── Incremental ─────────────────────────────────────────────────────────

    /// <summary>Checkpointed pages until caught up; the checkpoint only advances after a batch is written.</summary>
    public async Task<EmbeddingBackfillReport> RunIncrementalAsync(
        EmbeddingJobDefinition def, IncrementalBackfillConfig config, CancellationToken ct = default)
    {
        if (def is null) throw new ArgumentNullException(nameof(def));
        if (config is null) throw new ArgumentNullException(nameof(config));
        if (config.BatchSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(config), "BatchSize must be positive.");

        var checkpointTable = SurrealIdentifier.ForTable(config.CheckpointTable);
        var jobKey = def.JobName.Replace('.', '_');

        var startCursor = await ReadCheckpointAsync(checkpointTable, jobKey, ct).ConfigureAwait(false);

        Func<string, CancellationToken, Task> checkpoint = (bareKey, token) =>
            WriteCheckpointAsync(checkpointTable, jobKey, bareKey, token);

        return await RunChanneledAsync(
            def,
            config.BatchSize,
            force: false,
            checkpoint,
            (writer, token) => ProducePagesAsync(writer, def, config.BatchSize, startCursor, filter: null, token),
            ct).ConfigureAwait(false);
    }

    // ─── Ad-hoc ──────────────────────────────────────────────────────────────

    /// <summary>Run-once over an explicit id set, a filtered range, or the whole table.</summary>
    public async Task<EmbeddingBackfillReport> RunAdHocAsync(
        EmbeddingJobDefinition def, AdHocBackfillConfig config, CancellationToken ct = default)
    {
        if (def is null) throw new ArgumentNullException(nameof(def));
        if (config is null) throw new ArgumentNullException(nameof(config));
        if (config.BatchSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(config), "BatchSize must be positive.");

        Func<ChannelWriter<SourceRow>, CancellationToken, Task> produce;
        if (config.Ids is not null && config.Ids.Count > 0)
        {
            var ids = config.Ids;
            produce = (writer, token) => ProduceIdSetAsync(writer, def, ids, config.BatchSize, token);
        }
        else
        {
            produce = (writer, token) => ProducePagesAsync(
                writer, def, config.BatchSize, startCursor: null, config.Filter, token);
        }

        return await RunChanneledAsync(
            def, config.BatchSize, force: config.Reembed, checkpointAsync: null, produce, ct)
            .ConfigureAwait(false);
    }

    // ─── Dynamic ─────────────────────────────────────────────────────────────

    /// <summary>Drains a live change feed; completes when the feed does. Throws when no feed is configured.</summary>
    public async Task<EmbeddingBackfillReport> RunDynamicAsync(
        EmbeddingJobDefinition def, DynamicBackfillConfig config, CancellationToken ct = default)
    {
        if (def is null) throw new ArgumentNullException(nameof(def));
        if (config is null) throw new ArgumentNullException(nameof(config));
        if (config.BatchSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(config), "BatchSize must be positive.");
        if (config.LiveSource is null)
            throw new NotSupportedException(
                "Dynamic embedding jobs need a live change feed. Set DynamicBackfillConfig.LiveSource " +
                "(e.g. an adapter over WebSocketSurrealConnection.LiveAsync). First-class LIVE-query " +
                "wiring ships with Phase 2 of the SurrealForge.Vector plan (see DESIGN.md).");

        var source = config.LiveSource;
        return await RunChanneledAsync(
            def, config.BatchSize, force: false, checkpointAsync: null,
            async (writer, token) =>
            {
                await foreach (var change in source(token).ConfigureAwait(false))
                {
                    var row = new SourceRow(change.Id, change.Text, storedHash: null);
                    await writer.WriteAsync(row, token).ConfigureAwait(false);
                }
            },
            ct).ConfigureAwait(false);
    }

    // ─── Channel plumbing ────────────────────────────────────────────────────

    private sealed class SourceRow
    {
        public string Id { get; }
        public string? Text { get; }
        public string? StoredHash { get; }
        public string BareKey
        {
            get
            {
                int colon = Id.IndexOf(':');
                return colon >= 0 ? Id.Substring(colon + 1) : Id;
            }
        }

        public SourceRow(string id, string? text, string? storedHash)
        {
            Id = id;
            Text = text;
            StoredHash = storedHash;
        }
    }

    private sealed class PendingRow
    {
        public string BareKey { get; }
        public string Text { get; }
        public string Hash { get; }

        public PendingRow(string bareKey, string text, string hash)
        {
            BareKey = bareKey;
            Text = text;
            Hash = hash;
        }
    }

    /// <summary>Bounded-channel producer/consumer shell shared by all three job kinds.</summary>
    private async Task<EmbeddingBackfillReport> RunChanneledAsync(
        EmbeddingJobDefinition def,
        int batchSize,
        bool force,
        Func<string, CancellationToken, Task>? checkpointAsync,
        Func<ChannelWriter<SourceRow>, CancellationToken, Task> produceAsync,
        CancellationToken ct)
    {
        var channel = Channel.CreateBounded<SourceRow>(new BoundedChannelOptions(Math.Max(2, batchSize * 2))
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var producer = Task.Run(async () =>
        {
            try
            {
                await produceAsync(channel.Writer, linked.Token).ConfigureAwait(false);
                channel.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                channel.Writer.TryComplete(ex);
                throw;
            }
        });

        Exception? consumeError = null;
        EmbeddingBackfillReport report = new EmbeddingBackfillReport();
        try
        {
            report = await ConsumeAsync(channel.Reader, def, batchSize, force, checkpointAsync, linked.Token)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            consumeError = ex;
            linked.Cancel(); // unblock a producer stuck on a full channel
        }

        try
        {
            await producer.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (consumeError is not null) { }
        catch (Exception) when (consumeError is not null) { /* consumer already observed it */ }

        if (consumeError is not null)
            ExceptionDispatchInfo.Capture(consumeError).Throw();
        return report;
    }

    private async Task<EmbeddingBackfillReport> ConsumeAsync(
        ChannelReader<SourceRow> reader,
        EmbeddingJobDefinition def,
        int batchSize,
        bool force,
        Func<string, CancellationToken, Task>? checkpointAsync,
        CancellationToken ct)
    {
        var report = new EmbeddingBackfillReport();
        var pending = new List<PendingRow>(batchSize);
        string? lastSeenKey = null;
        string? lastCheckpointedKey = null;

        while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
        {
            while (reader.TryRead(out var row))
            {
                report.RowsScanned++;
                lastSeenKey = row.BareKey;

                if (string.IsNullOrEmpty(row.Text))
                {
                    report.RowsSkipped++;
                    continue;
                }

                var hash = EmbeddingContentHash.Compute(row.Text!);
                if (!force && string.Equals(row.StoredHash, hash, StringComparison.Ordinal))
                {
                    report.RowsSkipped++;
                    continue;
                }

                pending.Add(new PendingRow(row.BareKey, row.Text!, hash));
                if (pending.Count >= batchSize)
                {
                    await FlushAsync(def, pending, report, ct).ConfigureAwait(false);
                    pending.Clear();
                    if (checkpointAsync is not null && lastSeenKey is not null)
                    {
                        await checkpointAsync(lastSeenKey, ct).ConfigureAwait(false);
                        lastCheckpointedKey = lastSeenKey;
                    }
                }
            }
        }

        if (pending.Count > 0)
        {
            await FlushAsync(def, pending, report, ct).ConfigureAwait(false);
            pending.Clear();
        }

        if (checkpointAsync is not null && lastSeenKey is not null &&
            !string.Equals(lastSeenKey, lastCheckpointedKey, StringComparison.Ordinal))
        {
            await checkpointAsync(lastSeenKey, ct).ConfigureAwait(false);
        }

        return report;
    }

    /// <summary>Cache-first batch encode, then one (possibly Combine'd) parameterized UPDATE round-trip.</summary>
    private async Task FlushAsync(
        EmbeddingJobDefinition def,
        List<PendingRow> batch,
        EmbeddingBackfillReport report,
        CancellationToken ct)
    {
        var vectors = new float[batch.Count][];
        var missTexts = new List<string>();
        var missIndexes = new List<int>();

        for (int i = 0; i < batch.Count; i++)
        {
            var cached = await _cache.GetAsync(batch[i].Hash, ct).ConfigureAwait(false);
            if (cached is not null)
            {
                vectors[i] = cached;
            }
            else
            {
                missIndexes.Add(i);
                missTexts.Add(batch[i].Text);
            }
        }

        if (missTexts.Count > 0)
        {
            var encoded = await _encoder.EncodeAsync(missTexts, ct).ConfigureAwait(false);
            if (encoded is null || encoded.Length != missTexts.Count)
                throw new InvalidOperationException(
                    "IVectorEncoder batch EncodeAsync returned " + (encoded?.Length.ToString(CultureInfo.InvariantCulture) ?? "null") +
                    " vectors for " + missTexts.Count.ToString(CultureInfo.InvariantCulture) + " texts.");

            for (int j = 0; j < missIndexes.Count; j++)
            {
                int i = missIndexes[j];
                vectors[i] = encoded[j];
                await _cache.SetAsync(batch[i].Hash, encoded[j], ct).ConfigureAwait(false);
            }
        }

        var queries = new SurrealQuery[batch.Count];
        for (int i = 0; i < batch.Count; i++)
        {
            var n = i.ToString(CultureInfo.InvariantCulture);
            queries[i] = SurrealQuery
                .Of("UPDATE type::record($_tbl, $_r" + n + ") SET " +
                    def.TargetColumn + " = $_v" + n + ", " + def.HashColumn + " = $_h" + n)
                .WithParam("_tbl", def.Table)
                .WithParam("_r" + n, batch[i].BareKey)
                .WithParam("_v" + n, vectors[i])
                .WithParam("_h" + n, batch[i].Hash);
        }

        var write = queries.Length == 1 ? queries[0] : SurrealQuery.Combine(queries);
        var resp = await _executor.ExecuteAsync(write, ct).ConfigureAwait(false);
        resp.EnsureAllOk();

        report.RowsEmbedded += batch.Count;
        report.BatchesFlushed++;
    }

    // ─── Producers ───────────────────────────────────────────────────────────

    /// <summary>Cursor-pages the source table (paging cursor is independent of the durable checkpoint).</summary>
    private async Task ProducePagesAsync(
        ChannelWriter<SourceRow> writer,
        EmbeddingJobDefinition def,
        int batchSize,
        string? startCursor,
        SurrealQuery? filter,
        CancellationToken ct)
    {
        if (filter is not null)
        {
            if (filter.IsMultiStatement)
                throw new ArgumentException("Backfill Filter must be a single predicate fragment.");
            if (filter.Params.ContainsKey("_tbl") || filter.Params.ContainsKey("_cursor"))
                throw new ArgumentException("Backfill Filter must not bind $_tbl or $_cursor — reserved for paging.");
        }

        var cursor = startCursor;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var page = await FetchPageAsync(def, batchSize, cursor, filter, ct).ConfigureAwait(false);
            foreach (var row in page)
            {
                await writer.WriteAsync(row, ct).ConfigureAwait(false);
            }
            if (page.Count < batchSize) break;
            cursor = page[page.Count - 1].BareKey;
        }
    }

    private async Task<IReadOnlyList<SourceRow>> FetchPageAsync(
        EmbeddingJobDefinition def,
        int batchSize,
        string? cursor,
        SurrealQuery? filter,
        CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.Append("SELECT id, ").Append(def.SourceColumn).Append(" AS source_text, ")
          .Append(def.HashColumn).Append(" AS stored_hash FROM ").Append(def.Table);

        bool hasWhere = false;
        if (cursor is not null)
        {
            sb.Append(" WHERE id > type::record($_tbl, $_cursor)");
            hasWhere = true;
        }
        if (filter is not null)
        {
            sb.Append(hasWhere ? " AND (" : " WHERE (").Append(filter.Sql).Append(')');
        }
        sb.Append(" ORDER BY id LIMIT ").Append(batchSize.ToString(CultureInfo.InvariantCulture));

        var q = SurrealQuery.Of(sb.ToString());
        if (cursor is not null)
            q = q.WithParam("_tbl", def.Table).WithParam("_cursor", cursor);
        if (filter is not null)
            q = q.WithParams(filter.Params);

        var rows = await _executor.QueryAsync<JsonElement>(q, ct).ConfigureAwait(false);
        var page = new List<SourceRow>(rows.Count);
        foreach (var el in rows)
        {
            var row = ParseRow(el);
            if (row is not null) page.Add(row);
        }
        return page;
    }

    /// <summary>Fetches an explicit id set in batches via comma-joined <c>type::record($_tbl, $_rN)</c> sources.</summary>
    private async Task ProduceIdSetAsync(
        ChannelWriter<SourceRow> writer,
        EmbeddingJobDefinition def,
        IReadOnlyList<string> ids,
        int batchSize,
        CancellationToken ct)
    {
        for (int offset = 0; offset < ids.Count; offset += batchSize)
        {
            ct.ThrowIfCancellationRequested();
            int count = Math.Min(batchSize, ids.Count - offset);

            var sb = new StringBuilder();
            sb.Append("SELECT id, ").Append(def.SourceColumn).Append(" AS source_text, ")
              .Append(def.HashColumn).Append(" AS stored_hash FROM ");

            var q0 = (SurrealQuery?)null;
            var paramBag = new Dictionary<string, object?>(StringComparer.Ordinal) { ["_tbl"] = def.Table };
            for (int i = 0; i < count; i++)
            {
                var n = i.ToString(CultureInfo.InvariantCulture);
                if (i > 0) sb.Append(", ");
                sb.Append("type::record($_tbl, $_r").Append(n).Append(')');

                var raw = ids[offset + i];
                int colon = raw.IndexOf(':');
                paramBag["_r" + n] = colon >= 0 ? raw.Substring(colon + 1) : raw;
            }

            q0 = SurrealQuery.Of(sb.ToString()).WithParams(paramBag);
            var rows = await _executor.QueryAsync<JsonElement>(q0, ct).ConfigureAwait(false);
            foreach (var el in rows)
            {
                var row = ParseRow(el);
                if (row is not null)
                    await writer.WriteAsync(row, ct).ConfigureAwait(false);
            }
        }
    }

    private static SourceRow? ParseRow(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (!el.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String) return null;
        var id = idEl.GetString();
        if (string.IsNullOrEmpty(id)) return null;

        string? text = null;
        if (el.TryGetProperty("source_text", out var t) && t.ValueKind == JsonValueKind.String)
            text = t.GetString();

        string? storedHash = null;
        if (el.TryGetProperty("stored_hash", out var h) && h.ValueKind == JsonValueKind.String)
            storedHash = h.GetString();

        return new SourceRow(id!, text, storedHash);
    }

    // ─── Checkpoint ledger ───────────────────────────────────────────────────

    private async Task<string?> ReadCheckpointAsync(string checkpointTable, string jobKey, CancellationToken ct)
    {
        var q = SurrealQuery.Of("SELECT * FROM type::record($_t, $_id)")
            .WithParam("_t", checkpointTable)
            .WithParam("_id", jobKey);
        var rows = await _executor.QueryAsync<JsonElement>(q, ct).ConfigureAwait(false);
        if (rows.Count == 0) return null;
        if (rows[0].ValueKind == JsonValueKind.Object &&
            rows[0].TryGetProperty("cursor", out var c) &&
            c.ValueKind == JsonValueKind.String)
        {
            return c.GetString();
        }
        return null;
    }

    private async Task WriteCheckpointAsync(
        string checkpointTable, string jobKey, string cursor, CancellationToken ct)
    {
        var q = SurrealQuery.Of("UPSERT type::record($_t, $_id) SET cursor = $_cursor")
            .WithParam("_t", checkpointTable)
            .WithParam("_id", jobKey)
            .WithParam("_cursor", cursor);
        var resp = await _executor.ExecuteAsync(q, ct).ConfigureAwait(false);
        resp.EnsureAllOk();
    }
}
