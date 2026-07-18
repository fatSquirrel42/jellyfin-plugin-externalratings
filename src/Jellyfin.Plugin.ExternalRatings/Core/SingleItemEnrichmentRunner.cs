using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ExternalRatings.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ExternalRatings.Core;

/// <summary>
/// Runs a single item through the <see cref="RatingPipeline"/> for the realtime listener path
/// (spec §15 step 9). Unlike <see cref="EnrichmentRunner"/> it does <b>not</b> initialize the stores
/// (that would clear unflushed in-memory state, hoisted to a one-time guard on the facade) and does
/// <b>not</b> prune (an orphan prune with a single-item "live" set would delete every other item's
/// backup). It processes the item and flushes the cache so a single-item resolution is persisted; the
/// backup store is already durable per write.
/// </summary>
internal sealed class SingleItemEnrichmentRunner
{
    private readonly RatingPipeline _pipeline;
    private readonly IRatingCache _cache;
    private readonly ILogger<SingleItemEnrichmentRunner> _logger;

    /// <summary>Initializes a new instance of the <see cref="SingleItemEnrichmentRunner"/> class.</summary>
    /// <param name="pipeline">The per-item pipeline (shared with the full-pass runner for single-flight).</param>
    /// <param name="cache">The rating cache (flushed after each item).</param>
    /// <param name="logger">The logger.</param>
    public SingleItemEnrichmentRunner(RatingPipeline pipeline, IRatingCache cache, ILogger<SingleItemEnrichmentRunner> logger)
    {
        _pipeline = pipeline;
        _cache = cache;
        _logger = logger;
    }

    /// <summary>Processes one item and flushes the cache.</summary>
    /// <param name="item">The work item.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The terminal outcome.</returns>
    public async Task<RatingOutcome> RunAsync(RatingWorkItem item, CancellationToken cancellationToken)
    {
        var summary = new RunSummary();
        var outcome = await _pipeline.ProcessItemAsync(item, summary, cancellationToken).ConfigureAwait(false);

        // Persist any resolution the pipeline cached (found/no-match). No prune: a single item is not a
        // complete "live" set, and no re-init: unflushed state (including a concurrent full pass's) must
        // survive. The backup store is already durable per write.
        await _cache.FlushAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogDebug("External Ratings realtime enrichment for {ItemId} ({Name}): {Outcome}", item.Ref.ItemId, item.Ref.DisplayName, outcome);
        return outcome;
    }
}
