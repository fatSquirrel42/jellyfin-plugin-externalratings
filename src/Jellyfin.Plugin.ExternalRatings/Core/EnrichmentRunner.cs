using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ExternalRatings.Core.Abstractions;
using Jellyfin.Plugin.ExternalRatings.Persistence;
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
    private readonly RatingPrefetcher? _prefetcher;
    private readonly IRatingCache _cache;
    private readonly IBackupStore _backup;
    private readonly DailyRequestCounter _counter;
    private readonly Func<int> _dailyLimitAccessor;
    private readonly ILogger<EnrichmentRunner> _logger;

    /// <summary>Initializes a new instance of the <see cref="EnrichmentRunner"/> class.</summary>
    /// <param name="pipeline">The per-item pipeline.</param>
    /// <param name="prefetcher">Optional batch prefetcher run as phase 0 (null disables prefetch).</param>
    /// <param name="cache">The rating cache (lifecycle driven here).</param>
    /// <param name="backup">The backup store (lifecycle driven here).</param>
    /// <param name="counter">The shared daily request counter (budget gate).</param>
    /// <param name="dailyLimitAccessor">Accessor for the configured daily request limit.</param>
    /// <param name="logger">The logger.</param>
    public EnrichmentRunner(
        RatingPipeline pipeline,
        RatingPrefetcher? prefetcher,
        IRatingCache cache,
        IBackupStore backup,
        DailyRequestCounter counter,
        Func<int> dailyLimitAccessor,
        ILogger<EnrichmentRunner> logger)
    {
        _pipeline = pipeline;
        _prefetcher = prefetcher;
        _cache = cache;
        _backup = backup;
        _counter = counter;
        _dailyLimitAccessor = dailyLimitAccessor;
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
        // The stores are initialized once, up front, by the facade (RatingEnrichmentService.
        // EnsureInitializedAsync); re-initializing here would clear unflushed in-memory state that a
        // concurrent single-item (listener) enrichment may have written (spec §15 step 9).
        var summary = new RunSummary();
        _logger.LogInformation("External Ratings run {RunId} starting for {Count} item(s)", summary.RunId, items.Count);

        // Phase 0: batch-prefetch cache-misses so the per-item loop below hits cache (§6.4).
        if (_prefetcher is not null)
        {
            await _prefetcher.PrefetchAsync(items, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            for (var i = 0; i < items.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Stop cleanly when the daily budget is exhausted; the run resumes incrementally next
                // time (§7.3). The finally block still prunes and flushes what was done so far.
                if (_counter.IsExhausted(_dailyLimitAccessor()))
                {
                    summary.MarkStoppedForBudget();
                    _logger.LogInformation(
                        "External Ratings run {RunId} stopped early: daily request budget exhausted after {Processed} item(s)",
                        summary.RunId,
                        summary.ItemsProcessed);
                    break;
                }

                await _pipeline.ProcessItemAsync(items[i], summary, cancellationToken).ConfigureAwait(false);
                progress?.Report((double)(i + 1) / items.Count);
            }
        }
        finally
        {
            // In-memory prunes are cheap and cannot fail.
            _pipeline.PruneExpiredErrors();
            _cache.PruneExpired();

            // Persist cleanup with CancellationToken.None so it still runs when the run was cancelled,
            // and guard the I/O so a flush/prune failure neither masks an in-flight exception nor faults
            // the run — the DB writes already stand and the next run re-flushes/re-prunes.
            try
            {
                await _backup.PruneOrphansAsync(liveItemIds, CancellationToken.None).ConfigureAwait(false);
                await _cache.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogError(ex, "External Ratings run {RunId}: end-of-run persistence failed", summary.RunId);
            }
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
