using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ExternalRatings.Core.Abstractions;
using Jellyfin.Plugin.ExternalRatings.Resolvers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ExternalRatings.Core;

/// <summary>
/// The rating enrichment pipeline: the §6 decision table for one item at a time (spec §6).
/// Every mutating branch backs up write-ahead before touching the item, DryRun is read fresh per
/// item, and the global circuit breaker gates every resolver call.
/// </summary>
internal sealed class RatingPipeline
{
    private readonly IRatingResolver _resolver;
    private readonly IItemWriter _writer;
    private readonly IBackupStore _backup;
    private readonly IRatingCache _cache;
    private readonly IClock _clock;
    private readonly CircuitBreaker _breaker;
    private readonly Func<PipelineOptions> _optionsAccessor;
    private readonly ILogger<RatingPipeline> _logger;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();
    private readonly ConcurrentDictionary<RatingCacheKey, DateTimeOffset> _errorCache = new();

    /// <summary>Initializes a new instance of the <see cref="RatingPipeline"/> class.</summary>
    /// <param name="resolver">The rating resolver.</param>
    /// <param name="writer">The item writer.</param>
    /// <param name="backup">The backup store.</param>
    /// <param name="cache">The rating cache.</param>
    /// <param name="clock">The clock.</param>
    /// <param name="breaker">The global circuit breaker.</param>
    /// <param name="optionsAccessor">Accessor read fresh per item so DryRun toggles apply immediately.</param>
    /// <param name="logger">The logger.</param>
    public RatingPipeline(
        IRatingResolver resolver,
        IItemWriter writer,
        IBackupStore backup,
        IRatingCache cache,
        IClock clock,
        CircuitBreaker breaker,
        Func<PipelineOptions> optionsAccessor,
        ILogger<RatingPipeline> logger)
    {
        _resolver = resolver;
        _writer = writer;
        _backup = backup;
        _cache = cache;
        _clock = clock;
        _breaker = breaker;
        _optionsAccessor = optionsAccessor;
        _logger = logger;
    }

    /// <summary>Processes a single item through the decision table.</summary>
    /// <param name="item">The work item.</param>
    /// <param name="summary">The run summary to update.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The terminal outcome.</returns>
    public async Task<RatingOutcome> ProcessItemAsync(RatingWorkItem item, RunSummary summary, CancellationToken cancellationToken)
    {
        // Single-flight per item: concurrent triggers for the same id serialize, so the second one
        // finds the first one's cached result instead of resolving again (spec §5.3).
        var gate = _locks.GetOrAdd(item.Ref.ItemId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var outcome = await EvaluateAsync(item, summary, cancellationToken).ConfigureAwait(false);
            summary.Record(outcome);
            return outcome;
        }
        finally
        {
            gate.Release();
            if (gate.CurrentCount == 1)
            {
                _locks.TryRemove(new KeyValuePair<Guid, SemaphoreSlim>(item.Ref.ItemId, gate));
            }
        }
    }

    private async Task<RatingOutcome> EvaluateAsync(RatingWorkItem item, RunSummary summary, CancellationToken cancellationToken)
    {
        var options = _optionsAccessor();

        // 1. Choose exactly one input id. Null means "unsupported level" or "no usable id".
        var selection = InputIdSelector.Select(item.Level, item.ProviderIds);
        if (selection is null)
        {
            return await HandleUnsupportedAsync(item, options, cancellationToken).ConfigureAwait(false);
        }

        var chosen = selection.Value;
        var key = new RatingCacheKey(_resolver.Key, item.TargetSource, chosen.Provider, chosen.Id, item.Level);

        // 2. Persistent cache (positive/negative).
        if (_cache.TryGet(key, out var cached))
        {
            summary.RecordCacheHit();
            if (cached.Kind == RatingResolution.Found && cached.Score.HasValue)
            {
                return await EvaluateFoundWriteAsync(item.Ref, cached.Score.Value, options, cancellationToken).ConfigureAwait(false);
            }

            // A cached no-match: re-apply the ClearField decision on every pass (we cached the resolver
            // *result*, not whether a clear ran), so a later LeaveExisting->ClearField toggle or an
            // externally re-populated field is honored instead of waiting out the negative-cache TTL.
            if (options.NoMatchBehavior == NoMatchBehavior.ClearField)
            {
                return await ClearFieldAsync(item.Ref, options, RatingOutcome.NoMatch, cancellationToken).ConfigureAwait(false);
            }

            return RatingOutcome.NoMatch;
        }

        // 3. In-memory error cache (~1h) short-circuits without a resolver call (spec §6, M10).
        if (_errorCache.TryGetValue(key, out var errorExpiry))
        {
            if (errorExpiry > _clock.UtcNow)
            {
                return RatingOutcome.Error;
            }

            _errorCache.TryRemove(key, out _);
        }

        // 4. Circuit-breaker gate.
        if (!_breaker.AllowRequest())
        {
            return RatingOutcome.CircuitOpen;
        }

        // 5. Resolve.
        RatingResult result;
        try
        {
            var request = new RatingRequest(item.Level, chosen.Provider, chosen.Id, item.TargetSource);
            result = await _resolver.ResolveAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Release the breaker's half-open probe so a cancelled probe cannot wedge the breaker in
            // HalfOpen and refuse every future request (see CircuitBreaker.AbandonProbe).
            _breaker.AbandonProbe();
            throw;
        }
        catch (Exception ex)
        {
            // Unexpected failure — the resolver handles its own transport/parse/status errors, so this is
            // a genuine bug. Keep the fail-safe error handling, but log with the stack for diagnosability
            // and do NOT persist the raw message (it could echo a sensitive URL; mirrors the H9 masking).
            _logger.LogError(ex, "Unexpected resolver failure for {Provider}:{Id}", chosen.Provider, chosen.Id);
            result = RatingResult.ForError(ex.GetType().Name);
        }

        // 6. Map through the §6 table.
        switch (result.Resolution)
        {
            case RatingResolution.Found:
                if (!IsValidScore(result.Score))
                {
                    _logger.LogError(
                        "Resolver returned an invalid Found score {Score} for {Provider}:{Id}; treating as error (H14)",
                        result.Score,
                        chosen.Provider,
                        chosen.Id);
                    return RecordError(key, options, "H14 invariant violated");
                }

                _breaker.RecordSuccess();
                _cache.Set(key, new RatingCacheEntry(RatingResolution.Found, result.Score, _clock.UtcNow + options.CacheTtl));
                return await EvaluateFoundWriteAsync(item.Ref, result.Score!.Value, options, cancellationToken).ConfigureAwait(false);

            case RatingResolution.NoMatch:
                _breaker.RecordSuccess();
                _cache.Set(key, new RatingCacheEntry(RatingResolution.NoMatch, null, _clock.UtcNow + options.NegativeCacheTtl));
                if (chosen.HasUnusedAlternatives)
                {
                    summary.RecordNoMatchWithUnusedAlternatives();
                }

                if (options.NoMatchBehavior == NoMatchBehavior.ClearField)
                {
                    return await ClearFieldAsync(item.Ref, options, RatingOutcome.NoMatch, cancellationToken).ConfigureAwait(false);
                }

                return RatingOutcome.NoMatch;

            case RatingResolution.NotSupportedForLevel:
                _breaker.RecordSuccess();
                return await HandleUnsupportedAsync(item, options, cancellationToken).ConfigureAwait(false);

            default:
                _logger.LogWarning(
                    "Resolver error for {Provider}:{Id}: {Detail}",
                    chosen.Provider,
                    chosen.Id,
                    result.ErrorDetail);
                return RecordError(key, options, result.ErrorDetail ?? "error");
        }
    }

    private async Task<RatingOutcome> EvaluateFoundWriteAsync(RatingItemRef itemRef, float targetScore, PipelineOptions options, CancellationToken cancellationToken)
    {
        var current = _writer.GetCommunityRating(itemRef);
        if (current.HasValue && Math.Abs(current.Value - targetScore) <= options.Epsilon)
        {
            return RatingOutcome.SkippedNoChange;
        }

        // DryRun is checked immediately before the write (spec §6, K3).
        if (options.DryRun)
        {
            return RatingOutcome.SkippedDryRun;
        }

        try
        {
            // Write-ahead: the backup must be durable before the write. If the backup persist fails the
            // write never runs, so the original is never lost; if the write fails, the backup is durable
            // and the next run retries from the cache. A per-item I/O fault must not fault the whole run.
            await _backup.EnsureBackedUpAsync(itemRef.ItemId, current, cancellationToken).ConfigureAwait(false);
            await _writer.WriteAsync(itemRef, targetScore, ItemWriteReason.RatingUpdated, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "External Ratings could not persist the rating for {ItemId} (backup/write I/O)", itemRef.ItemId);
            return RatingOutcome.Error;
        }

        return RatingOutcome.Updated;
    }

    private async Task<RatingOutcome> HandleUnsupportedAsync(RatingWorkItem item, PipelineOptions options, CancellationToken cancellationToken)
    {
        // "NotSupportedForLevel / no id" share one row: skip by default, clear on opt-in, never cache.
        var levelSupported = InputIdSelector.ProviderPriority(item.Level).Count > 0;
        var baseOutcome = levelSupported ? RatingOutcome.SkippedNoId : RatingOutcome.NotSupported;

        if (options.UnsupportedLevelBehavior == UnsupportedLevelBehavior.ClearField)
        {
            return await ClearFieldAsync(item.Ref, options, baseOutcome, cancellationToken).ConfigureAwait(false);
        }

        return baseOutcome;
    }

    private async Task<RatingOutcome> ClearFieldAsync(RatingItemRef itemRef, PipelineOptions options, RatingOutcome baseOutcome, CancellationToken cancellationToken)
    {
        var current = _writer.GetCommunityRating(itemRef);
        if (!current.HasValue)
        {
            // Nothing to clear.
            return baseOutcome;
        }

        if (options.DryRun)
        {
            return RatingOutcome.SkippedDryRun;
        }

        try
        {
            await _backup.EnsureBackedUpAsync(itemRef.ItemId, current, cancellationToken).ConfigureAwait(false);
            await _writer.WriteAsync(itemRef, null, ItemWriteReason.RatingCleared, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "External Ratings could not clear the rating for {ItemId} (backup/write I/O)", itemRef.ItemId);
            return RatingOutcome.Error;
        }

        return RatingOutcome.Cleared;
    }

    private RatingOutcome RecordError(RatingCacheKey key, PipelineOptions options, string detail)
    {
        _breaker.RecordError();
        _errorCache[key] = _clock.UtcNow + options.ErrorCacheTtl;
        _logger.LogError("Rating error cached for {InputProvider}:{InputId}: {Detail}", key.InputProvider, key.InputId, detail);
        return RatingOutcome.Error;
    }

    /// <summary>
    /// Removes expired entries from the in-memory error cache. Entries are otherwise only evicted lazily
    /// when the same id is re-queried, so on the long-lived shared pipeline the cache would keep expired
    /// entries for ids that are never re-processed. Called once per run (run-end, next to the cache prune).
    /// </summary>
    public void PruneExpiredErrors()
    {
        var now = _clock.UtcNow;
        foreach (var pair in _errorCache)
        {
            if (pair.Value <= now)
            {
                _errorCache.TryRemove(pair.Key, out _);
            }
        }
    }

    private static bool IsValidScore(float? score) => score is >= 0f and <= 10f;
}
