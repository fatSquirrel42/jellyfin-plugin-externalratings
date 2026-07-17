using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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

            // A cached no-match leaves the existing value in place; the clear (if any) already ran.
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
            throw;
        }
        catch (Exception ex)
        {
            result = RatingResult.ForError(ex.Message);
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

        await _backup.EnsureBackedUpAsync(itemRef.ItemId, current, cancellationToken).ConfigureAwait(false);
        await _writer.WriteAsync(itemRef, targetScore, ItemWriteReason.RatingUpdated, cancellationToken).ConfigureAwait(false);
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

        await _backup.EnsureBackedUpAsync(itemRef.ItemId, current, cancellationToken).ConfigureAwait(false);
        await _writer.WriteAsync(itemRef, null, ItemWriteReason.RatingCleared, cancellationToken).ConfigureAwait(false);
        return RatingOutcome.Cleared;
    }

    private RatingOutcome RecordError(RatingCacheKey key, PipelineOptions options, string detail)
    {
        _breaker.RecordError();
        _errorCache[key] = _clock.UtcNow + options.ErrorCacheTtl;
        _logger.LogError("Rating error cached for {InputProvider}:{InputId}: {Detail}", key.InputProvider, key.InputId, detail);
        return RatingOutcome.Error;
    }

    private static bool IsValidScore(float? score) => score is >= 0f and <= 10f;
}
