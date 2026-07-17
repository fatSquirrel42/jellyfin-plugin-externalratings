using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ExternalRatings.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ExternalRatings.Core;

/// <summary>
/// Drives a set of work items through the <see cref="RatingPipeline"/> for one run (spec §6, §7.6):
/// it owns the persistence lifecycle (initialize → process → prune → flush) and the per-run summary,
/// while the pipeline owns the per-item decision. Testable against fakes without any Jellyfin host.
/// </summary>
internal sealed class EnrichmentRunner
{
    private readonly RatingPipeline _pipeline;
    private readonly IRatingCache _cache;
    private readonly IBackupStore _backup;
    private readonly ILogger<EnrichmentRunner> _logger;

    /// <summary>Initializes a new instance of the <see cref="EnrichmentRunner"/> class.</summary>
    /// <param name="pipeline">The per-item pipeline.</param>
    /// <param name="cache">The rating cache (lifecycle driven here).</param>
    /// <param name="backup">The backup store (lifecycle driven here).</param>
    /// <param name="logger">The logger.</param>
    public EnrichmentRunner(RatingPipeline pipeline, IRatingCache cache, IBackupStore backup, ILogger<EnrichmentRunner> logger)
    {
        _pipeline = pipeline;
        _cache = cache;
        _backup = backup;
        _logger = logger;
    }

    /// <summary>Processes every item and returns the run summary.</summary>
    /// <param name="items">The work items to process.</param>
    /// <param name="liveItemIds">The ids of all items that still exist (for orphan pruning, H10).</param>
    /// <param name="progress">Optional progress reporter (0..1).</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The run summary.</returns>
    public async Task<RunSummary> RunAsync(
        IReadOnlyList<RatingWorkItem> items,
        IReadOnlySet<Guid> liveItemIds,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        await _cache.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _backup.InitializeAsync(cancellationToken).ConfigureAwait(false);

        var summary = new RunSummary();
        _logger.LogInformation("External Ratings run {RunId} starting for {Count} item(s)", summary.RunId, items.Count);

        try
        {
            for (var i = 0; i < items.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await _pipeline.ProcessItemAsync(items[i], summary, cancellationToken).ConfigureAwait(false);
                progress?.Report((double)(i + 1) / items.Count);
            }
        }
        finally
        {
            _cache.PruneExpired();
            _backup.PruneOrphans(liveItemIds);
            await _cache.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "External Ratings run {RunId} done: processed={Processed} updated={Updated} cleared={Cleared} "
            + "noMatch={NoMatch} notSupported={NotSupported} skippedNoId={SkippedNoId} skippedNoChange={SkippedNoChange} "
            + "dryRunSkipped={DryRunSkipped} errors={Errors} circuitOpenSkips={CircuitOpenSkips} cacheHits={CacheHits}",
            summary.RunId,
            summary.ItemsProcessed,
            summary.Updated,
            summary.Cleared,
            summary.NoMatch,
            summary.NotSupported,
            summary.SkippedNoId,
            summary.SkippedNoChange,
            summary.DryRunSkipped,
            summary.Errors,
            summary.CircuitOpenSkips,
            summary.CacheHits);

        return summary;
    }
}
