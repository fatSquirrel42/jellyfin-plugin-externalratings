using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ExternalRatings.Core.Abstractions;
using Jellyfin.Plugin.ExternalRatings.Persistence;
using Jellyfin.Plugin.ExternalRatings.Resolvers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ExternalRatings.Core;

/// <summary>
/// Batch prefetch (spec §6.4). Before the per-item loop, the run's cache-misses are grouped by
/// (level × input provider × target source), resolved in chunks of at most
/// <see cref="IBatchRatingResolver.MaxBatchSize"/> via the batch resolver, and seeded into the
/// persistent cache. The unchanged item pipeline then hits cache instead of resolving one item at a
/// time. A batch POST costs one request (V1-measured), so the daily budget and the circuit breaker
/// gate per chunk. Only <see cref="RatingResolution.Found"/> and <see cref="RatingResolution.NoMatch"/>
/// are seeded; errors stay uncached (the pipeline owns the in-memory error cache).
/// </summary>
internal sealed class RatingPrefetcher
{
    private readonly IBatchRatingResolver _resolver;
    private readonly IRatingCache _cache;
    private readonly IClock _clock;
    private readonly CircuitBreaker _breaker;
    private readonly DailyRequestCounter _counter;
    private readonly Func<PipelineOptions> _optionsAccessor;
    private readonly Func<int> _dailyLimitAccessor;
    private readonly ILogger<RatingPrefetcher> _logger;

    /// <summary>Initializes a new instance of the <see cref="RatingPrefetcher"/> class.</summary>
    /// <param name="resolver">The batch resolver.</param>
    /// <param name="cache">The rating cache to seed.</param>
    /// <param name="clock">The clock (for cache TTLs).</param>
    /// <param name="breaker">The shared circuit breaker.</param>
    /// <param name="counter">The shared daily request counter.</param>
    /// <param name="optionsAccessor">Accessor for the cache TTLs (shared with the pipeline).</param>
    /// <param name="dailyLimitAccessor">Accessor for the configured daily request limit.</param>
    /// <param name="logger">The logger.</param>
    public RatingPrefetcher(
        IBatchRatingResolver resolver,
        IRatingCache cache,
        IClock clock,
        CircuitBreaker breaker,
        DailyRequestCounter counter,
        Func<PipelineOptions> optionsAccessor,
        Func<int> dailyLimitAccessor,
        ILogger<RatingPrefetcher> logger)
    {
        _resolver = resolver;
        _cache = cache;
        _clock = clock;
        _breaker = breaker;
        _counter = counter;
        _optionsAccessor = optionsAccessor;
        _dailyLimitAccessor = dailyLimitAccessor;
        _logger = logger;
    }

    /// <summary>Prefetches ratings for the run's cache-misses and seeds the cache.</summary>
    /// <param name="items">The work items about to be processed.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when prefetch finishes (or stops early on budget/breaker).</returns>
    public async Task PrefetchAsync(IReadOnlyList<RatingWorkItem> items, CancellationToken cancellationToken)
    {
        var groups = CollectMisses(items);
        if (groups.Count == 0)
        {
            return;
        }

        var options = _optionsAccessor();
        var seeded = 0;

        foreach (var (groupKey, ids) in groups)
        {
            foreach (var chunk in ids.Chunk(_resolver.MaxBatchSize))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (_counter.IsExhausted(_dailyLimitAccessor()))
                {
                    _logger.LogInformation("Batch prefetch stopped: daily request budget exhausted (seeded {Count})", seeded);
                    return;
                }

                if (_breaker.IsOpen)
                {
                    _logger.LogInformation("Batch prefetch skipped: circuit breaker open (seeded {Count})", seeded);
                    return;
                }

                var results = await _resolver
                    .ResolveBatchAsync(groupKey.Level, groupKey.Provider, chunk, groupKey.TargetSource, cancellationToken)
                    .ConfigureAwait(false);

                seeded += Seed(groupKey, results, options);
            }
        }

        _logger.LogInformation("Batch prefetch seeded {Count} cache entries across {Groups} group(s)", seeded, groups.Count);
    }

    private Dictionary<GroupKey, List<string>> CollectMisses(IReadOnlyList<RatingWorkItem> items)
    {
        var groups = new Dictionary<GroupKey, List<string>>();
        var seenPerGroup = new Dictionary<GroupKey, HashSet<string>>();

        foreach (var item in items)
        {
            var selection = InputIdSelector.Select(item.Level, item.ProviderIds);
            if (selection is null)
            {
                continue;
            }

            var sel = selection.Value;
            var cacheKey = new RatingCacheKey(_resolver.Key, item.TargetSource, sel.Provider, sel.Id, item.Level);
            if (_cache.TryGet(cacheKey, out _))
            {
                // Already cached (positive or negative); the pipeline will hit it.
                continue;
            }

            var groupKey = new GroupKey(item.Level, sel.Provider, item.TargetSource);
            if (!groups.TryGetValue(groupKey, out var ids))
            {
                ids = new List<string>();
                groups[groupKey] = ids;
                seenPerGroup[groupKey] = new HashSet<string>(StringComparer.Ordinal);
            }

            if (seenPerGroup[groupKey].Add(sel.Id))
            {
                ids.Add(sel.Id);
            }
        }

        return groups;
    }

    private int Seed(GroupKey groupKey, IReadOnlyDictionary<string, RatingResult> results, PipelineOptions options)
    {
        var count = 0;
        foreach (var (id, result) in results)
        {
            var key = new RatingCacheKey(_resolver.Key, groupKey.TargetSource, groupKey.Provider, id, groupKey.Level);
            switch (result.Resolution)
            {
                case RatingResolution.Found when result.Score.HasValue:
                    _cache.Set(key, new RatingCacheEntry(RatingResolution.Found, result.Score, _clock.UtcNow + options.CacheTtl));
                    count++;
                    break;

                case RatingResolution.NoMatch:
                    _cache.Set(key, new RatingCacheEntry(RatingResolution.NoMatch, null, _clock.UtcNow + options.NegativeCacheTtl));
                    count++;
                    break;

                default:
                    // Error / NotSupportedForLevel: leave uncached, the pipeline handles those.
                    break;
            }
        }

        return count;
    }

    private readonly record struct GroupKey(ItemLevel Level, string Provider, string TargetSource);
}
