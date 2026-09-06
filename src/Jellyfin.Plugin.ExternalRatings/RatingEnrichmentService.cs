using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ExternalRatings.Configuration;
using Jellyfin.Plugin.ExternalRatings.Core;
using Jellyfin.Plugin.ExternalRatings.Core.Abstractions;
using Jellyfin.Plugin.ExternalRatings.Infrastructure;
using Jellyfin.Plugin.ExternalRatings.Persistence;
using Jellyfin.Plugin.ExternalRatings.Resolvers;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
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
    private readonly CircuitBreaker _breaker;
    private readonly SelfWriteTracker _selfWriteTracker;
    private readonly IItemWriter _writer;
    private readonly DailyRequestCounter _requestCounter;
    private readonly HttpClient _httpClient;
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
    private MdblistResolver? _sharedResolver;
    private RatingPipeline? _sharedPipeline;
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
        _breaker = new CircuitBreaker(_clock);
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
            _breaker,
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
    }

    /// <summary>Gets a value indicating whether the circuit breaker is currently open.</summary>
    public bool IsCircuitOpen => _breaker.IsOpen;

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
            _logger.LogWarning("External Ratings run skipped: no mdblist API key configured");
            return;
        }

        if (config.EnabledLibraries.Length == 0)
        {
            _logger.LogInformation("External Ratings run skipped: no libraries enabled");
            return;
        }

        // Initialize the stores + cold-start budget once (shared with the listener).
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        var (resolver, pipeline) = GetOrBuildPipeline(apiKey);

        Func<PipelineOptions> optionsAccessor = () => PluginConfigurationMapper.ToPipelineOptions(Plugin.Instance!.Configuration);
        Func<int> dailyLimitAccessor = () => Plugin.Instance?.Configuration.DailyRequestLimit ?? DefaultDailyLimit;

        var prefetcher = new RatingPrefetcher(
            resolver,
            _cache,
            _clock,
            _breaker,
            _requestCounter,
            optionsAccessor,
            dailyLimitAccessor,
            new Logger<RatingPrefetcher>(_loggerFactory));

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
            var (items, liveIds) = BuildWorkItems(config, resolver);
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
        if (string.IsNullOrWhiteSpace(apiKey) || config.EnabledLibraries.Length == 0)
        {
            return;
        }

        // A full pass or restore covers/overwrites this item; skip to avoid a double resolve, the
        // orphan-prune race, and re-applying an external rating over a value being restored.
        if (_gate.IsFullRunInProgress || _gate.IsRestoreInProgress)
        {
            return;
        }

        if (_requestCounter.IsExhausted(config.DailyRequestLimit))
        {
            _logger.LogDebug("External Ratings realtime enrichment for {ItemId} skipped: daily budget exhausted", itemId);
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

        var (resolver, pipeline) = GetOrBuildPipeline(apiKey);

        // Mirror the full pass: BuildWorkItems enumerates only the levels the resolver supports, so the
        // realtime path must apply the same filter. Without it an Episode or Season reaches the pipeline
        // as an unsupported level, and UnsupportedLevelBehavior=ClearField then wipes a rating the
        // scheduled task would never have touched -- and every such write re-triggers other plugins
        // listening on ItemUpdated (media-segment analysis, for example).
        if (!resolver.SupportedInputProviders.ContainsKey(level.Value))
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

        var workItem = new RatingWorkItem(
            new RatingItemRef(baseItem.Id, baseItem.Name),
            level.Value,
            ExtractProviderIds(baseItem),
            source);

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
                _breaker.IsOpen);
        }
    }

    /// <summary>Gets the item levels the active resolver supports, as strings (for the status endpoint).</summary>
    /// <returns>The supported levels (for example <c>Movie</c>, <c>Series</c>).</returns>
    public IReadOnlyList<string> GetSupportedLevels()
    {
        // SupportedInputProviders needs neither the API key nor HTTP, so a capability-only instance is fine.
        var resolver = new MdblistResolver(_httpClient, string.Empty, new Logger<MdblistResolver>(_loggerFactory));
        return SupportedLevels(resolver).Select(level => level.ToString()).ToList();
    }

    /// <summary>Gets the external rating sources the active resolver can return (for the status endpoint).</summary>
    /// <returns>The selectable rating sources with their display names and native scales.</returns>
    public IReadOnlyList<RatingSourceInfo> GetSupportedSources()
    {
        // SupportedRatingSources is a static capability: no API key or HTTP needed.
        var resolver = new MdblistResolver(_httpClient, string.Empty, new Logger<MdblistResolver>(_loggerFactory));
        return resolver.SupportedRatingSources.ToList();
    }

    /// <summary>Disposes the owned cache, backup stores, and HTTP stack.</summary>
    public void Dispose()
    {
        _cache.Dispose();
        _backup.Dispose();
        _httpClient.Dispose();
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
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                try
                {
                    var (resolver, _) = GetOrBuildPipeline(apiKey);
                    var used = await resolver.GetUsedRequestCountAsync(cancellationToken).ConfigureAwait(false);
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

    private (MdblistResolver Resolver, RatingPipeline Pipeline) GetOrBuildPipeline(string apiKey)
    {
        lock (_pipelineLock)
        {
            if (_sharedResolver is null || _sharedPipeline is null || !string.Equals(_pipelineApiKey, apiKey, StringComparison.Ordinal))
            {
                _sharedResolver = new MdblistResolver(_httpClient, apiKey, new Logger<MdblistResolver>(_loggerFactory));
                _pipelineApiKey = apiKey;
                _sharedPipeline = new RatingPipeline(
                    _sharedResolver,
                    _writer,
                    _backup,
                    _cache,
                    _clock,
                    _breaker,
                    () => PluginConfigurationMapper.ToPipelineOptions(Plugin.Instance!.Configuration),
                    new Logger<RatingPipeline>(_loggerFactory));
            }

            return (_sharedResolver, _sharedPipeline);
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
            Recursive = true
        };
    }

    private (IReadOnlyList<RatingWorkItem> Items, IReadOnlySet<Guid> LiveIds) BuildWorkItems(PluginConfiguration config, IRatingResolver resolver)
    {
        var query = BuildLibraryQuery(SupportedLevels(resolver), config.EnabledLibraries);

        // Resolve each item's source per its library. With no overrides configured, every item uses the
        // default, so skip the per-item GetCollectionFolders lookup entirely.
        var hasOverrides = config.LibrarySources.Length > 0;
        var defaultSource = PluginConfigurationMapper.ResolveSource(config, Array.Empty<Guid>());

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

            items.Add(new RatingWorkItem(
                new RatingItemRef(baseItem.Id, baseItem.Name),
                level.Value,
                ExtractProviderIds(baseItem),
                source));
        }

        return (items, liveIds);
    }

    private static Dictionary<string, string> ExtractProviderIds(BaseItem item)
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
