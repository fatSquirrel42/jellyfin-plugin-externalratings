using System;
using System.Collections.Generic;
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
    private const string TargetSource = "myanimelist";
    private const string ApiKeySettingKey = "mdblist.apiKey";
    private const int DefaultDailyLimit = 1000;

    // How long a plugin write suppresses the change event it raises (self-write guard, §15 step 9).
    private static readonly TimeSpan SelfWriteWindow = TimeSpan.FromSeconds(30);

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

    // One-time persistence + cold-start init guard (shared by full runs and the listener).
    private readonly SemaphoreSlim _initGate = new(1, 1);

    // Guards building of the shared resolver + per-item pipeline (built once and reused across events,
    // preserving the pipeline's per-id single-flight and error cache; rebuilt only when the key changes).
    private readonly object _pipelineLock = new();

    private bool _initialized;
    private MdblistResolver? _sharedResolver;
    private RatingPipeline? _sharedPipeline;
    private string? _pipelineApiKey;

    // Set while a full pass runs, so the listener skips items the pass will cover anyway (avoids the
    // double-resolve window and the orphan-prune race between the two paths).
    private volatile bool _fullRunInProgress;

    private RunSummary? _lastSummary;
    private DateTimeOffset? _lastRunUtc;

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
        _backup = new BackupStore(new FileCacheFileStore(System.IO.Path.Combine(dataDir, "backup.json")), _clock);
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
        _httpClient = new HttpClient(budgetHandler) { BaseAddress = new Uri("https://api.mdblist.com/") };
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

        _fullRunInProgress = true;
        try
        {
            var (items, liveIds) = BuildWorkItems(config);
            var summary = await runner.RunAsync(items, liveIds, progress, cancellationToken).ConfigureAwait(false);

            lock (_statusGate)
            {
                _lastSummary = summary;
                _lastRunUtc = _clock.UtcNow;
            }
        }
        finally
        {
            _fullRunInProgress = false;
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

        // A full pass covers this item; skip to avoid a double resolve and the orphan-prune race.
        if (_fullRunInProgress)
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

        var workItem = new RatingWorkItem(
            new RatingItemRef(baseItem.Id, baseItem.Name),
            level.Value,
            ExtractProviderIds(baseItem),
            TargetSource);

        var (_, pipeline) = GetOrBuildPipeline(apiKey);
        var runner = new SingleItemEnrichmentRunner(pipeline, _cache, new Logger<SingleItemEnrichmentRunner>(_loggerFactory));
        await runner.RunAsync(workItem, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Gets a value indicating whether the plugin wrote the given item within the self-write window.</summary>
    /// <param name="itemId">The item id.</param>
    /// <returns><see langword="true"/> if the item was written by the plugin recently.</returns>
    public bool WasSelfWrite(Guid itemId) => _selfWriteTracker.IsRecent(itemId);

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

    /// <summary>Disposes the owned cache, backup stores, and HTTP stack.</summary>
    public void Dispose()
    {
        _cache.Dispose();
        _backup.Dispose();
        _httpClient.Dispose();
        _initGate.Dispose();
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

    private (IReadOnlyList<RatingWorkItem> Items, IReadOnlySet<Guid> LiveIds) BuildWorkItems(PluginConfiguration config)
    {
        var query = BuildLibraryQuery(PluginConfigurationMapper.ParseLevels(config), config.EnabledLibraries);

        var items = new List<RatingWorkItem>();
        var liveIds = new HashSet<Guid>();
        foreach (var baseItem in _libraryManager.GetItemList(query))
        {
            var level = ToLevel(baseItem.GetBaseItemKind());
            if (level is null)
            {
                continue;
            }

            // Record every item as "live" first — even locked ones — so the orphan-prune (which deletes
            // backups not in liveIds) never strands a locked item's protected original rating. Only after
            // that do we skip locked items from enrichment (spec §15 step 10): a locked item is
            // user-protected, so the full pass must not overwrite its rating.
            liveIds.Add(baseItem.Id);
            if (baseItem.IsLocked)
            {
                continue;
            }

            items.Add(new RatingWorkItem(
                new RatingItemRef(baseItem.Id, baseItem.Name),
                level.Value,
                ExtractProviderIds(baseItem),
                TargetSource));
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
