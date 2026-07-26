// SPDX-License-Identifier: MIT
// SurrealForge.Vector -- hosted-service shell running every configured
// backfill job on its own worker task. See AGENTS.md §Backfill jobs / Hosting.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SurrealForge.Client.Query;

namespace SurrealForge.Vector;

/// <summary>Runs the configured <see cref="EmbeddingBackfillJob"/>s in the background (one worker per job).</summary>
public sealed class EmbeddingBackfillService : IHostedService, IDisposable
{
    private readonly IServiceProvider _services;
    private readonly SurrealVectorOptions _options;
    private readonly ILogger<EmbeddingBackfillService>? _logger;
    private readonly CancellationTokenSource _stopping = new CancellationTokenSource();
    private readonly List<Task> _workers = new List<Task>();

    public EmbeddingBackfillService(
        IServiceProvider services,
        SurrealVectorOptions options,
        ILogger<EmbeddingBackfillService>? logger = null)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
    }

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var registry = _services.GetRequiredService<VectorEncoderRegistry>();
        var cache = _services.GetRequiredService<IEmbeddingCache>();

        foreach (var job in _options.Jobs)
        {
            var executor = _options.ExecutorFactory is not null
                ? _options.ExecutorFactory(_services)
                : _services.GetRequiredService<ISurrealExecutor>();
            var encoder = registry.Resolve(job.Definition.Profile);
            var runner = new EmbeddingBackfillRunner(executor, encoder, cache);
            _workers.Add(RunJobAsync(runner, job, _stopping.Token));
        }
        return Task.CompletedTask;
    }

    private async Task RunJobAsync(EmbeddingBackfillRunner runner, EmbeddingBackfillJob job, CancellationToken ct)
    {
        // Yield so StartAsync never blocks on a job's first page.
        await Task.Yield();
        try
        {
            var report = await runner.RunAsync(job, ct).ConfigureAwait(false);
            _logger?.LogInformation(
                "Embedding job {Job} finished: {Embedded} embedded, {Skipped} skipped, {Scanned} scanned.",
                job.Definition.JobName, report.RowsEmbedded, report.RowsSkipped, report.RowsScanned);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Graceful shutdown.
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Embedding job {Job} failed.", job.Definition.JobName);
        }
    }

    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping.Cancel();
        if (_workers.Count == 0) return;
        var all = Task.WhenAll(_workers);
        var timeout = Task.Delay(Timeout.Infinite, cancellationToken);
        await Task.WhenAny(all, timeout).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public void Dispose() => _stopping.Dispose();
}
