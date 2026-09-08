using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ExternalRatings.Api;
using Jellyfin.Plugin.ExternalRatings.Configuration;
using Jellyfin.Plugin.ExternalRatings.Core;
using Jellyfin.Plugin.ExternalRatings.Core.Abstractions;
using Jellyfin.Plugin.ExternalRatings.Infrastructure;
using Jellyfin.Plugin.ExternalRatings.Persistence;
using Jellyfin.Plugin.ExternalRatings.Resolvers;
using Jellyfin.Plugin.ExternalRatings.Resolvers.Imdb;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ExternalRatings;

/// <summary>
/// The composition root and shared-state owner for rating enrichment. Registered as a single DI
/// singleton so every trigger (scheduled task now; post-scan/listener/restore later) shares one
/// circuit breaker, cache, and backup store. It is the only public surface over the internal core:
/// its constructor takes public host services, and it builds the internal graph itself (which is why
/// no internal type ever appears in a public signature).
/// </summary>
public sealed class RatingEnrichmentService : ISingleItemEnricher, IDisposable
{
    private const string ApiKeySettingKey = "mdblist.apiKey";
    private const int DefaultDailyLimit = 1000;

    // How long a plugin write suppresses the change event it raises (self-write guard, §15 step 9).
    private static readonly TimeSpan SelfWriteWindow = TimeSpan.FromSeconds(30);

    // The realtime path flushes the cache at most once per interval instead of rewriting the whole
    // cache file after every single item (the cache is write-behind, so coalescing is safe; L).
    private static readonly TimeSpan CacheFlushMinInterval = TimeSpan.FromSeconds(30);

    private readonly ILibraryManager _libraryManager;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<RatingEnrichmentService> _logger;

    // Shared, run-spanning state.
    private readonly SystemClock _clock;
    private readonly FileRatingCache _cache;
    private readonly BackupStore _backup;
    // One circuit breaker per resolver key. A shared one would let a failing backend stop a healthy
    // one: an invalid mdblist key trips it, and the offline dataset's items get skipped alongside.
    private readonly ConcurrentDictionary<string, CircuitBreaker> _breakers = new(StringComparer.Ordinal);
    private readonly SelfWriteTracker _selfWriteTracker;
    private readonly IItemWriter _writer;
    private readonly DailyRequestCounter _requestCounter;
    private readonly HttpClient _httpClient;

    // The IMDb dataset download is a plain file fetch from datasets.imdbws.com: it must not go
    // through the mdblist budget handler, which would count it against that quota and reconcile the
    // counter from headers the IMDb CDN never sends.
    private readonly HttpClient _datasetHttpClient;
    private readonly ImdbRatingsDataset _imdbDataset;
    private readonly ImdbEpisodeDataset _imdbEpisodes;
    private readonly object _statusGate = new();
    private readonly object _flushGate = new();

    // One-time persistence + cold-start init guard (shared by full runs and the listener).
    private readonly SemaphoreSlim _initGate = new(1, 1);

    // Guards building of the shared resolver + per-item pipeline (built once and reused across events,
    // preserving the pipeline's per-id single-flight and error cache; rebuilt only when the key changes).
    private readonly object _pipelineLock = new();

    // Serializes the full pass, restore-all, and cache clear so they never overlap (spec §15 step 10).
    // The listener reads its InProgress flags to skip items a full pass/restore will cover anyway
    // (avoids the double-resolve window and the orphan-prune / restore-undo races between the paths).
    private readonly ExclusiveOperationGate _gate = new();

    private volatile bool _initialized;
    private RatingRouter? _sharedRouter;
    private RatingPipeline? _sharedPipeline;

    // Identity of the graph currently built. Only the mdblist key can change which resolvers are
    // available (no key = mdblist is not a candidate); everything else is read live via accessors.
    private string? _pipelineApiKey;

    private RunSummary? _lastSummary;
    private DateTimeOffset? _lastRunUtc;
    private DateTimeOffset _lastCacheFlushUtc = DateTimeOffset.MinValue;

    /// <summary>Initializes a new instance of the <see cref="RatingEnrichmentService"/> class.</summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="applicationPaths">The application paths (for the plugin data directory).</param>
    /// <param name="loggerFactory">The logger factory.</param>
    public RatingEnrichmentService(
        ILibraryManager libraryManager,
        IApplicationPaths applicationPaths,
        ILoggerFactory loggerFactory)
    {
        _libraryManager = libraryManager;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<RatingEnrichmentService>();

        var dataDir = System.IO.Path.Combine(applicationPaths.DataPath, "ExternalRatings");
        _clock = new SystemClock();
        _cache = new FileRatingCache(new FileCacheFileStore(System.IO.Path.Combine(dataDir, "cache.json")), _clock);
        _backup = new BackupStore(new FileCacheFileStore(System.IO.Path.Combine(dataDir, "backup.jsonl")), _clock);

        _selfWriteTracker = new SelfWriteTracker(_clock, SelfWriteWindow);
        _writer = new SelfWriteTrackingItemWriter(new JellyfinItemWriter(libraryManager), _selfWriteTracker);
        _requestCounter = new DailyRequestCounter(_clock);

        // Long-lived HTTP stack (§10 HTTP seam). The budget handler counts every mdblist request,
        // reconciles the counter from X-RateLimit-* headers, and trips the breaker on 429. Built once
        // here (composition root) and shared across runs; the API key rides in the query string, so
        // the client itself is key-agnostic.
        var primaryHandler = new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All };
        var budgetHandler = new BudgetTrackingHandler(
            _requestCounter,
            BreakerFor(MdblistResolver.ResolverKey),
            () => Plugin.Instance?.Configuration.DailyRequestLimit ?? DefaultDailyLimit,
            _loggerFactory.CreateLogger<BudgetTrackingHandler>())
        {
            InnerHandler = primaryHandler
        };
        // Cap the buffered response size (a batch returns <=200 objects) so a malfunctioning or hostile
        // upstream cannot balloon a single response into memory; exceeding it throws HttpRequestException,
        // which the resolver already maps to a graceful error (J).
        _httpClient = new HttpClient(budgetHandler)
        {
            BaseAddress = new Uri("https://api.mdblist.com/"),
            MaxResponseContentBufferSize = 8L * 1024 * 1024
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Jellyfin-Plugin-ExternalRatings");

        // The dataset is tens of megabytes and streams straight to disk, so no response buffer cap
        // and a generous timeout. Construction touches neither disk nor network: nothing is fetched
        // until the imdb-dataset resolver actually asks for the index.
        _datasetHttpClient = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All })
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
        _datasetHttpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Jellyfin-Plugin-ExternalRatings");
        Func<TimeSpan> datasetRefresh = () => Plugin.Instance is { } plugin
            ? PluginConfigurationMapper.ToDatasetRefreshInterval(plugin.Configuration)
            : TimeSpan.FromDays(1);
        _imdbDataset = new ImdbRatingsDataset(
            _datasetHttpClient, dataDir, datasetRefresh, _clock, new Logger<ImdbRatingsDataset>(_loggerFactory));
        _imdbEpisodes = new ImdbEpisodeDataset(
            _datasetHttpClient, dataDir, datasetRefresh, _clock, new Logger<ImdbEpisodeDataset>(_loggerFactory));
    }

    /// <summary>Gets a value indicating whether any resolver's circuit breaker is currently open.</summary>
    public bool IsCircuitOpen => _breakers.Values.Any(breaker => breaker.IsOpen);

    /// <summary>The circuit breaker for one resolver, created on first use.</summary>
    /// <param name="resolverKey">The resolver key.</param>
    /// <returns>That resolver's breaker.</returns>
    private CircuitBreaker BreakerFor(string resolverKey)
        => _breakers.GetOrAdd(resolverKey, _ => new CircuitBreaker(_clock));

    /// <summary>
    /// Runs a full enrichment pass over the enabled libraries. Shared by every trigger (scheduled
    /// task and post-scan task), so all runs use the same cache, breaker, budget counter, and HTTP
    /// stack.
    /// </summary>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the run finishes.</returns>
    public async Task RunAsync(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null)
        {
            return;
        }

        var apiKey = PluginConfigurationMapper.GetResolverSetting(config, ApiKeySettingKey);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            // Not a reason to skip the run any more: the offline IMDb dataset needs no key. Only the
            // mdblist-backed sources become unreachable, and those items route nowhere.
            _logger.LogInformation("External Ratings: no mdblist API key configured, so only IMDb (offline dataset) is available");
        }

        if (config.EnabledLibraries.Length == 0)
        {
            _logger.LogInformation("External Ratings run skipped: no libraries enabled");
            return;
        }

        // Initialize the stores + cold-start budget once (shared with the listener).
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        var (router, pipeline) = GetOrBuildPipeline(apiKey ?? string.Empty);

        Func<PipelineOptions> optionsAccessor = () => PluginConfigurationMapper.ToPipelineOptions(Plugin.Instance!.Configuration);
        Func<int> dailyLimitAccessor = () => Plugin.Instance?.Configuration.DailyRequestLimit ?? DefaultDailyLimit;

        // Phase 0 only exists to amortise HTTP round-trips. A resolver that answers from local data
        // is not an IBatchRatingResolver, and prefetching it would just walk the library twice.
        var prefetcher = router.TryGetBatchResolver() is { } batchResolver
            ? new RatingPrefetcher(
                batchResolver,
                item => router.Route(item).Resolver?.Key == batchResolver.Key,
                _cache,
                _clock,
                BreakerFor(batchResolver.Key),
                _requestCounter,
                optionsAccessor,
                dailyLimitAccessor,
                new Logger<RatingPrefetcher>(_loggerFactory))
            : null;

        var runner = new EnrichmentRunner(
            pipeline,
            prefetcher,
            _cache,
            _backup,
            _requestCounter,
            dailyLimitAccessor,
            new Logger<EnrichmentRunner>(_loggerFactory));

        // Refuse to run while a restore or cache clear holds the gate (mutual exclusion, §15 step 10).
        if (!_gate.TryBeginFullRun())
        {
            _logger.LogInformation("External Ratings run skipped: another operation (restore/clear/run) is in progress");
            return;
        }

        try
        {
            var (items, liveIds, seriesIds) = BuildWorkItems(config);

            // Filter the episode map to this library's series before the pass, so a season resolves
            // on its first item instead of triggering a rebuild mid-run.
            if (seriesIds.Count > 0)
            {
                await _imdbEpisodes.EnsureCoversAsync(seriesIds, cancellationToken).ConfigureAwait(false);
            }

            var summary = await runner.RunAsync(items, liveIds, progress, cancellationToken).ConfigureAwait(false);

            lock (_statusGate)
            {
                _lastSummary = summary;
                _lastRunUtc = _clock.UtcNow;
            }
        }
        finally
        {
            _gate.End();
        }
    }

    /// <summary>
    /// Enriches a single item on the realtime path (spec §15 step 9). Reuses the shared cache, breaker,
    /// budget counter, and per-item pipeline; skips when a full pass is running, the budget is exhausted,
    /// the item is gone, or the item is not in an enabled library / not a supported level.
    /// </summary>
    /// <param name="itemId">The Jellyfin item id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the item has been processed.</returns>
    public async Task EnrichItemAsync(Guid itemId, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || !config.EnableRealtimeListener)
        {
            return;
        }

        var apiKey = PluginConfigurationMapper.GetResolverSetting(config, ApiKeySettingKey);
        if (config.EnabledLibraries.Length == 0)
        {
            return;
        }

        // A full pass or restore covers/overwrites this item; skip to avoid a double resolve, the
        // orphan-prune race, and re-applying an external rating over a value being restored.
        if (_gate.IsFullRunInProgress || _gate.IsRestoreInProgress)
        {
            return;
        }

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        var baseItem = _libraryManager.GetItemById(itemId);
        if (baseItem is null)
        {
            return;
        }

        // A locked item is user-protected; never overwrite its rating (spec §15 step 10). Defensive:
        // the listener gate already drops locked items, but the facade is the enforcement point.
        if (baseItem.IsLocked)
        {
            return;
        }

        if (!IsInEnabledLibrary(baseItem, config.EnabledLibraries))
        {
            return;
        }

        var level = ToLevel(baseItem.GetBaseItemKind());
        if (level is null)
        {
            return;
        }

        var (router, pipeline) = GetOrBuildPipeline(apiKey ?? string.Empty);

        // Mirror the full pass exactly: it enumerates ProcessedLevels, so the realtime path must
        // filter by the same thing -- capability *and* the user's opt-in. Without it an Episode or
        // Season reaches the pipeline as an unsupported level, and UnsupportedLevelBehavior=ClearField
        // then wipes a rating the scheduled task would never have touched -- and every such write
        // re-triggers other plugins listening on ItemUpdated (media-segment analysis, for example).
        if (!ProcessedLevels(config).Contains(level.Value))
        {
            return;
        }

        var source = PluginConfigurationMapper.ResolveSource(
            config,
            _libraryManager.GetCollectionFolders(baseItem).Select(f => f.Id));

        // "none" (default or per-library) means: leave this item's community rating untouched.
        if (PluginConfigurationMapper.IsNoSource(source))
        {
            return;
        }

        var providerIds = ExtractProviderIds(baseItem);
        List<string>? memberIds = null;

        // A Season resolves from its episodes, so the realtime path must gather them too — without
        // members the aggregate is a no-match, and ClearField would wipe the season's rating.
        if (level == ItemLevel.Season && baseItem is Season seasonItem)
        {
            memberIds = CollectSeasonMembers(seasonItem);
            providerIds = BuildSeasonProviderIds(baseItem, memberIds.Count);

            // The episode map may have been built without this series — a new show, or a season the
            // last full pass never saw — so widen it before the resolver reads it.
            if (seasonItem.Series is { } series
                && ReadProviderIds(series).TryGetValue("Imdb", out var seriesImdb)
                && ImdbRatingsIndex.ParseTconst(seriesImdb) is { } seriesId)
            {
                await _imdbEpisodes
                    .EnsureCoversAsync(new HashSet<long> { seriesId }, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        var workItem = new RatingWorkItem(
            new RatingItemRef(baseItem.Id, baseItem.Name),
            level.Value,
            providerIds,
            source,
            memberIds);

        // The daily budget only governs the metered route. A counter left exhausted by an earlier
        // mdblist run must not stall an item the offline dataset would serve for free.
        if (router.Route(workItem).Resolver is MdblistResolver
            && _requestCounter.IsExhausted(config.DailyRequestLimit))
        {
            _logger.LogDebug("External Ratings realtime enrichment for {ItemId} skipped: daily budget exhausted", itemId);
            return;
        }

        var runner = new SingleItemEnrichmentRunner(pipeline, new Logger<SingleItemEnrichmentRunner>(_loggerFactory));
        await runner.RunAsync(workItem, cancellationToken).ConfigureAwait(false);

        // Persist any resolution the pipeline cached, but throttled: rewriting the whole cache file after
        // every single realtime item would be O(cache size) per event (L). Cache is write-behind, so a
        // coalesced flush is safe; the listener force-flushes the tail on shutdown via FlushPendingAsync.
        await FlushCacheIfDueAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Restores every backed-up item's community rating to its original value and clears the backups
    /// (spec §9.2, §15 step 10). Refused while a full pass or cache clear is in progress. Restore
    /// ignores <c>IsLocked</c> on purpose — it is a deliberate admin reset.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The number of entries restored, or <see langword="null"/> if another operation was in progress.</returns>
    public async Task<int?> RestoreAllAsync(CancellationToken cancellationToken)
    {
        if (!_gate.TryBeginRestore())
        {
            _logger.LogInformation("External Ratings restore skipped: another operation is in progress");
            return null;
        }

        try
        {
            // Load backup.json (and the cache) once, exactly as a run would; without this the backup
            // set is empty on a cold process and restore would silently no-op.
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

            // Write through the self-write-tracking _writer so the restore's own MetadataEdit echo does
            // not re-trigger the realtime listener and immediately re-apply an external rating.
            var runner = new RestoreRunner(_backup, _writer, new Logger<RestoreRunner>(_loggerFactory));
            return await runner.RestoreAllAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.End();
        }
    }

    /// <summary>
    /// Clears the resolved-rating cache (spec §9.3, §15 step 10). The backup store is left intact, so
    /// restore remains possible. Refused while a full pass or restore is in progress.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns><see langword="true"/> if the cache was cleared; <see langword="false"/> if another operation was in progress.</returns>
    public async Task<bool> ClearCacheAsync(CancellationToken cancellationToken)
    {
        if (!_gate.TryBeginClearCache())
        {
            _logger.LogInformation("External Ratings cache clear skipped: another operation is in progress");
            return false;
        }

        try
        {
            // ClearAsync drops the in-memory dictionary and deletes the file unconditionally, so no
            // prior InitializeAsync is needed.
            await _cache.ClearAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.End();
        }
    }

    /// <summary>Gets a value indicating whether the plugin wrote the given item within the self-write window.</summary>
    /// <param name="itemId">The item id.</param>
    /// <returns><see langword="true"/> if the item was written by the plugin recently.</returns>
    public bool WasSelfWrite(Guid itemId) => _selfWriteTracker.IsRecent(itemId);

    /// <summary>
    /// Flushes any pending realtime cache entries to disk regardless of the throttle. Called by the
    /// listener on shutdown so entries resolved since the last throttled flush are still persisted.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes once the cache is flushed.</returns>
    public Task FlushPendingAsync(CancellationToken cancellationToken)
    {
        lock (_flushGate)
        {
            _lastCacheFlushUtc = _clock.UtcNow;
        }

        return _cache.FlushAsync(cancellationToken);
    }

    /// <summary>Gets a snapshot of the last run and the circuit-breaker state for the status endpoint.</summary>
    /// <returns>The snapshot.</returns>
    public RunStatusSnapshot GetStatusSnapshot()
    {
        lock (_statusGate)
        {
            var s = _lastSummary;
            return new RunStatusSnapshot(
                _lastRunUtc,
                s?.ItemsProcessed ?? 0,
                s?.Updated ?? 0,
                s?.NoMatch ?? 0,
                s?.Errors ?? 0,
                s?.DryRunSkipped ?? 0,
                IsCircuitOpen);
        }
    }

    /// <summary>
    /// Gets the selectable rating sources across every resolver, each with the item levels some
    /// resolver can serve it at (for the status endpoint and the config page).
    /// </summary>
    /// <remarks>
    /// The union, deduplicated by source key: <c>imdb</c> is offered by both resolvers and must
    /// appear once, carrying the wider level set. Capability is static, so these instances need no
    /// API key and make no requests — and mdblist is included regardless of whether a key is
    /// configured, so the user can see what a key would unlock.
    /// </remarks>
    /// <returns>The sources in a stable order, most capable first.</returns>
    public IReadOnlyList<SourceCapabilities> GetSupportedSources()
    {
        var resolvers = new IRatingResolver[]
        {
            new ImdbDatasetResolver(_imdbDataset, _imdbEpisodes, new Logger<ImdbDatasetResolver>(_loggerFactory)),
            new MdblistResolver(_httpClient, string.Empty, new Logger<MdblistResolver>(_loggerFactory))
        };

        var router = new RatingRouter(resolvers);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<SourceCapabilities>();

        foreach (var resolver in resolvers)
        {
            foreach (var source in resolver.SupportedRatingSources)
            {
                if (!seen.Add(source.Key))
                {
                    continue;
                }

                result.Add(new SourceCapabilities
                {
                    Key = source.Key,
                    DisplayName = source.DisplayName,
                    Scale = source.Scale,
                    Rescaled = source.Rescaled,
                    Levels = router.ServableLevels(source.Key).Select(level => level.ToString()).ToList(),
                    // The offline dataset serves exactly one source; everything else is mdblist's.
                    RequiresApiKey = !string.Equals(
                        source.Key, ImdbDatasetResolver.ImdbSource, StringComparison.OrdinalIgnoreCase)
                });
            }
        }

        return result;
    }

    /// <summary>Disposes the owned cache, backup stores, and HTTP stack.</summary>
    public void Dispose()
    {
        _cache.Dispose();
        _backup.Dispose();
        _httpClient.Dispose();
        _imdbDataset.Dispose();
        _imdbEpisodes.Dispose();
        _datasetHttpClient.Dispose();
        _initGate.Dispose();
    }

    private async Task FlushCacheIfDueAsync(CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        bool due;
        lock (_flushGate)
        {
            due = now - _lastCacheFlushUtc >= CacheFlushMinInterval;
            if (due)
            {
                _lastCacheFlushUtc = now;
            }
        }

        if (due)
        {
            await _cache.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _initGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            await _cache.InitializeAsync(cancellationToken).ConfigureAwait(false);
            await _backup.InitializeAsync(cancellationToken).ConfigureAwait(false);

            // Cold-start budget read (§7.3): adopt the server's authoritative used-count for the day,
            // once. Thereafter the budget handler reconciles the counter from response headers, so the
            // listener path never needs a per-event /user call. Best-effort: never fault the caller.
            var config = Plugin.Instance?.Configuration;
            var apiKey = config is null ? null : PluginConfigurationMapper.GetResolverSetting(config, ApiKeySettingKey);

            // Only mdblist has a request budget to reconcile, and it is reconcilable whenever a key
            // exists — no provider choice gates it any more.
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                try
                {
                    var mdblist = new MdblistResolver(_httpClient, apiKey, new Logger<MdblistResolver>(_loggerFactory));
                    var used = await mdblist.GetUsedRequestCountAsync(cancellationToken).ConfigureAwait(false);
                    if (used is int usedCount)
                    {
                        _requestCounter.InitializeFromColdStart(usedCount);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "External Ratings cold-start budget read failed; relying on header reconciliation");
                }
            }

            _initialized = true;
        }
        finally
        {
            _initGate.Release();
        }
    }

    /// <summary>
    /// Builds the router for a run: every resolver that is usable right now, most preferred first.
    /// </summary>
    /// <remarks>
    /// The offline IMDb dataset comes first because it costs no key and no quota, reads the same
    /// numbers, and reaches all four levels; mdblist follows and is the only way to the other eight
    /// sources — and, for <c>imdb</c>, the fallback for an item that has no IMDb id but does have a
    /// Tmdb or Tvdb one. Without an API key mdblist is simply left out, which is how "no key" becomes
    /// "those sources are unreachable" instead of a special case inside the router.
    /// </remarks>
    /// <param name="apiKey">The mdblist API key; blank means mdblist is unavailable.</param>
    /// <returns>The router.</returns>
    private RatingRouter BuildRouter(string apiKey)
    {
        var candidates = new List<IRatingResolver>(2)
        {
            new ImdbDatasetResolver(_imdbDataset, _imdbEpisodes, new Logger<ImdbDatasetResolver>(_loggerFactory))
        };

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            candidates.Add(new MdblistResolver(_httpClient, apiKey, new Logger<MdblistResolver>(_loggerFactory)));
        }

        return new RatingRouter(candidates);
    }

    private (RatingRouter Router, RatingPipeline Pipeline) GetOrBuildPipeline(string apiKey)
    {
        lock (_pipelineLock)
        {
            if (_sharedRouter is null
                || _sharedPipeline is null
                || !string.Equals(_pipelineApiKey, apiKey, StringComparison.Ordinal))
            {
                _sharedRouter = BuildRouter(apiKey);
                _pipelineApiKey = apiKey;
                _sharedPipeline = new RatingPipeline(
                    _sharedRouter,
                    _writer,
                    _backup,
                    _cache,
                    _clock,
                    BreakerFor,
                    () => PluginConfigurationMapper.ToPipelineOptions(Plugin.Instance!.Configuration),
                    new Logger<RatingPipeline>(_loggerFactory));
            }

            return (_sharedRouter, _sharedPipeline);
        }
    }

    private bool IsInEnabledLibrary(BaseItem item, Guid[] enabledLibraries)
    {
        foreach (var folder in _libraryManager.GetCollectionFolders(item))
        {
            if (Array.IndexOf(enabledLibraries, folder.Id) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The item levels the resolver supports (its capability feature flag). Deterministic order; the
    /// resolver's <see cref="IRatingResolver.SupportedInputProviders"/> keys are the source of truth.
    /// </summary>
    /// <param name="resolver">The active resolver.</param>
    /// <returns>The supported levels in canonical order.</returns>
    internal static IReadOnlyList<ItemLevel> SupportedLevels(IRatingResolver resolver)
        => new[] { ItemLevel.Movie, ItemLevel.Series, ItemLevel.Season, ItemLevel.Episode }
            .Where(resolver.SupportedInputProviders.ContainsKey)
            .ToArray();

    /// <summary>
    /// The levels actually enumerated: whatever the user opted into via
    /// <see cref="PluginConfiguration.EnabledLevels"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately independent of any resolver. The opt-in is global while rating sources are
    /// per-library, so no single source can decide what gets enumerated. Whether a given item can
    /// actually be served is a routing question answered per item, and an item with no route is
    /// cleared rather than skipped — see <see cref="RatingPipeline"/>'s unsupported branch.
    /// </para>
    /// <para>
    /// Both the full pass and the realtime path must call this and nothing else; lesson 8 in
    /// <c>docs/lessons-learned.md</c> is the regression from those two disagreeing.
    /// </para>
    /// </remarks>
    /// <param name="config">The plugin configuration.</param>
    /// <returns>The processed levels, in canonical order.</returns>
    internal static IReadOnlyList<ItemLevel> ProcessedLevels(PluginConfiguration config)
        => PluginConfigurationMapper.ParseLevels(config.EnabledLevels);

    /// <summary>
    /// Builds the enumeration query for the enabled libraries. Filtering is by <c>AncestorIds</c>, not
    /// <c>TopParentIds</c>: the config stores the <em>CollectionFolder</em> ids returned by
    /// <c>getVirtualFolders().ItemId</c>, and an item's <c>TopParentId</c> is the underlying physical
    /// folder, not that CollectionFolder — so <c>TopParentIds</c> matches nothing. The CollectionFolder
    /// is an <em>ancestor</em> of the item, so <c>AncestorIds</c> is the correct filter (verified against
    /// a live 10.11 library).
    /// </summary>
    /// <param name="levels">The processed item levels.</param>
    /// <param name="enabledLibraries">The enabled library (CollectionFolder) ids.</param>
    /// <returns>The query.</returns>
    internal static InternalItemsQuery BuildLibraryQuery(IReadOnlyList<ItemLevel> levels, Guid[] enabledLibraries)
    {
        var kinds = new List<BaseItemKind>();
        foreach (var level in levels)
        {
            var kind = ToKind(level);
            if (kind.HasValue)
            {
                kinds.Add(kind.Value);
            }
        }

        return new InternalItemsQuery
        {
            IncludeItemTypes = kinds.ToArray(),
            AncestorIds = enabledLibraries,
            Recursive = true,

            // Missing-episode placeholders are virtual items with no file behind them. They only
            // start appearing once Episode is an enumerated level, and resolving them is pointless:
            // under the shipped ClearField default a non-match would clear a field on an item the
            // user does not even have.
            IsVirtualItem = false
        };
    }

    private (IReadOnlyList<RatingWorkItem> Items, IReadOnlySet<Guid> LiveIds, IReadOnlySet<long> SeriesIds) BuildWorkItems(PluginConfiguration config)
    {
        var query = BuildLibraryQuery(ProcessedLevels(config), config.EnabledLibraries);

        // Resolve each item's source per its library. With no overrides configured, every item uses the
        // default, so skip the per-item GetCollectionFolders lookup entirely.
        var hasOverrides = config.LibrarySources.Length > 0;
        var defaultSource = PluginConfigurationMapper.ResolveSource(config, Array.Empty<Guid>());

        var levels = ProcessedLevels(config);
        var seriesIds = new HashSet<long>();
        var seasonMembers = levels.Contains(ItemLevel.Season)
            ? CollectSeasonMembers(config.EnabledLibraries, seriesIds)
            : null;

        var items = new List<RatingWorkItem>();
        var liveIds = new HashSet<Guid>();
        foreach (var baseItem in _libraryManager.GetItemList(query))
        {
            var level = ToLevel(baseItem.GetBaseItemKind());
            if (level is null)
            {
                continue;
            }

            // Record every item as "live" first — even locked ones — so the orphan-prune
            // (which deletes backups not in liveIds) never strands a still-existing item's protected
            // original rating. Only after that do we skip locked items from enrichment (spec §15 step 10):
            // a locked item is user-protected, so the full pass must not overwrite its rating.
            liveIds.Add(baseItem.Id);
            if (baseItem.IsLocked)
            {
                continue;
            }

            var source = hasOverrides
                ? PluginConfigurationMapper.ResolveSource(config, _libraryManager.GetCollectionFolders(baseItem).Select(f => f.Id))
                : defaultSource;

            // "none" means the effective source disables enrichment for this item; still recorded as
            // live above so the orphan-prune keeps any existing backup.
            if (PluginConfigurationMapper.IsNoSource(source))
            {
                continue;
            }

            var providerIds = ExtractProviderIds(baseItem);
            IReadOnlyList<string>? memberIds = null;

            if (level == ItemLevel.Season)
            {
                memberIds = seasonMembers is not null && seasonMembers.TryGetValue(baseItem.Id, out var members)
                    ? members
                    : Array.Empty<string>();
                providerIds = BuildSeasonProviderIds(baseItem, memberIds.Count);
            }

            items.Add(new RatingWorkItem(
                new RatingItemRef(baseItem.Id, baseItem.Name),
                level.Value,
                providerIds,
                source,
                memberIds));
        }

        return (items, liveIds, seriesIds);
    }

    /// <summary>
    /// Reads the provider ids to resolve an item by, dropping any an Episode only carries because
    /// it was inherited from its series (see <see cref="InheritedProviderIdFilter"/>).
    /// </summary>
    /// <param name="item">The library item.</param>
    /// <returns>The usable provider ids.</returns>
    private static Dictionary<string, string> ExtractProviderIds(BaseItem item)
    {
        var ids = ReadProviderIds(item);

        if (item is Episode episode)
        {
            var series = episode.Series;
            if (series is not null)
            {
                ids = InheritedProviderIdFilter.Strip(ids, ReadProviderIds(series));
            }
        }

        return ids;
    }

    /// <summary>
    /// Groups every non-virtual episode in the enabled libraries under its season, as the input ids
    /// a season aggregate is computed from. One query for the whole pass rather than one per season.
    /// </summary>
    /// <param name="enabledLibraries">The enabled CollectionFolder ids.</param>
    /// <param name="seriesIds">
    /// Receives the numeric IMDb id of every series seen, which is what the episode map has to be
    /// filtered to. Collected here because nothing else in the pass enumerates Series items.
    /// </param>
    /// <returns>Season id to its episodes' input ids.</returns>
    private Dictionary<Guid, IReadOnlyList<string>> CollectSeasonMembers(
        Guid[] enabledLibraries,
        HashSet<long> seriesIds)
    {
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Episode },
            AncestorIds = enabledLibraries,
            Recursive = true,
            IsVirtualItem = false
        };

        var members = new Dictionary<Guid, IReadOnlyList<string>>();
        foreach (var baseItem in _libraryManager.GetItemList(query))
        {
            if (baseItem is not Episode episode || episode.SeasonId == Guid.Empty)
            {
                continue;
            }

            // The series id comes for free here and is what the episode map must be filtered to.
            // Nothing else in the pass enumerates Series items, so this is the cheapest source.
            if (episode.Series is { } series
                && ReadProviderIds(series).TryGetValue("Imdb", out var seriesImdb)
                && ImdbRatingsIndex.ParseTconst(seriesImdb) is { } seriesId)
            {
                seriesIds.Add(seriesId);
            }

            // Same ids the episode itself would resolve by, inherited-id filter included: a season
            // must never be identified from ids that are really its series'. These members only
            // *identify* which IMDb season this is — the episodes actually averaged are IMDb's — so
            // an episode without an id is not a useful witness and is simply left out.
            var ids = ExtractProviderIds(baseItem);
            if (!ids.TryGetValue("Imdb", out var imdbId) || string.IsNullOrWhiteSpace(imdbId))
            {
                continue;
            }

            if (!members.TryGetValue(episode.SeasonId, out var list))
            {
                list = new List<string>();
                members[episode.SeasonId] = list;
            }

            ((List<string>)list).Add(imdbId);
        }

        return members;
    }

    /// <summary>Collects one season's episode input ids (the realtime counterpart).</summary>
    /// <param name="season">The season.</param>
    /// <returns>Its episodes' input ids.</returns>
    private List<string> CollectSeasonMembers(Season season)
    {
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Episode },
            ParentId = season.Id,
            Recursive = true,
            IsVirtualItem = false
        };

        // Witnesses for identifying the IMDb season — see the full-pass overload.
        var ids = new List<string>();
        foreach (var baseItem in _libraryManager.GetItemList(query))
        {
            if (ExtractProviderIds(baseItem).TryGetValue("Imdb", out var imdbId)
                && !string.IsNullOrWhiteSpace(imdbId))
            {
                ids.Add(imdbId);
            }
        }

        return ids;
    }

    /// <summary>
    /// Synthesises the input id a season is cached under. IMDb has no season entity, so there is no
    /// real id: the series' tconst plus the season number identifies it, and the episode count makes
    /// the key change when the season gains or loses an episode, which is what invalidates a stale
    /// average before its TTL expires.
    /// </summary>
    /// <param name="item">The season item.</param>
    /// <param name="memberCount">How many episodes contributed.</param>
    /// <returns>The synthetic provider ids for the season.</returns>
    private static Dictionary<string, string> BuildSeasonProviderIds(BaseItem item, int memberCount)
    {
        var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var series = (item as Season)?.Series;
        if (series is not null
            && ReadProviderIds(series).TryGetValue("Imdb", out var seriesId)
            && !string.IsNullOrWhiteSpace(seriesId))
        {
            ids["Imdb"] = string.Create(
                CultureInfo.InvariantCulture,
                $"{seriesId}/S{item.IndexNumber ?? 0}#{memberCount}");
        }

        return ids;
    }

    private static Dictionary<string, string> ReadProviderIds(BaseItem item)
    {
        var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddIfPresent(ids, item, MetadataProvider.Tmdb);
        AddIfPresent(ids, item, MetadataProvider.Imdb);
        AddIfPresent(ids, item, MetadataProvider.Tvdb);
        return ids;
    }

    private static void AddIfPresent(Dictionary<string, string> ids, BaseItem item, MetadataProvider provider)
    {
        var value = item.GetProviderId(provider);
        if (!string.IsNullOrWhiteSpace(value))
        {
            ids[provider.ToString()] = value;
        }
    }

    private static BaseItemKind? ToKind(ItemLevel level) => level switch
    {
        ItemLevel.Movie => BaseItemKind.Movie,
        ItemLevel.Series => BaseItemKind.Series,
        ItemLevel.Season => BaseItemKind.Season,
        ItemLevel.Episode => BaseItemKind.Episode,
        _ => null
    };

    private static ItemLevel? ToLevel(BaseItemKind kind) => kind switch
    {
        BaseItemKind.Movie => ItemLevel.Movie,
        BaseItemKind.Series => ItemLevel.Series,
        BaseItemKind.Season => ItemLevel.Season,
        BaseItemKind.Episode => ItemLevel.Episode,
        _ => null
    };
}
