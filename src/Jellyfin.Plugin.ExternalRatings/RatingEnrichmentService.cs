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
public sealed class RatingEnrichmentService : IDisposable
{
    private const string TargetSource = "myanimelist";
    private const string ApiKeySettingKey = "mdblist.apiKey";
    private const int DefaultDailyLimit = 1000;

    private readonly ILibraryManager _libraryManager;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<RatingEnrichmentService> _logger;

    // Shared, run-spanning state.
    private readonly SystemClock _clock;
    private readonly FileRatingCache _cache;
    private readonly BackupStore _backup;
    private readonly CircuitBreaker _breaker;
    private readonly JellyfinItemWriter _writer;
    private readonly DailyRequestCounter _requestCounter;
    private readonly HttpClient _httpClient;
    private readonly object _statusGate = new();

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
        _writer = new JellyfinItemWriter(libraryManager);
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

        var resolver = new MdblistResolver(_httpClient, apiKey, new Logger<MdblistResolver>(_loggerFactory));

        // Cold-start budget read (§7.3): adopt the server's authoritative used-count for the day.
        var used = await resolver.GetUsedRequestCountAsync(cancellationToken).ConfigureAwait(false);
        if (used is int usedCount)
        {
            _requestCounter.InitializeFromColdStart(usedCount);
        }

        Func<PipelineOptions> optionsAccessor = () => PluginConfigurationMapper.ToPipelineOptions(Plugin.Instance!.Configuration);
        Func<int> dailyLimitAccessor = () => Plugin.Instance?.Configuration.DailyRequestLimit ?? DefaultDailyLimit;

        var pipeline = new RatingPipeline(
            resolver,
            _writer,
            _backup,
            _cache,
            _clock,
            _breaker,
            optionsAccessor,
            new Logger<RatingPipeline>(_loggerFactory));

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

        var (items, liveIds) = BuildWorkItems(config);
        var summary = await runner.RunAsync(items, liveIds, progress, cancellationToken).ConfigureAwait(false);

        lock (_statusGate)
        {
            _lastSummary = summary;
            _lastRunUtc = _clock.UtcNow;
        }
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

    /// <summary>Disposes the owned cache, backup stores, and HTTP stack.</summary>
    public void Dispose()
    {
        _cache.Dispose();
        _backup.Dispose();
        _httpClient.Dispose();
    }

    private (IReadOnlyList<RatingWorkItem> Items, IReadOnlySet<Guid> LiveIds) BuildWorkItems(PluginConfiguration config)
    {
        var levels = PluginConfigurationMapper.ParseLevels(config);
        var kinds = new List<BaseItemKind>();
        foreach (var level in levels)
        {
            var kind = ToKind(level);
            if (kind.HasValue)
            {
                kinds.Add(kind.Value);
            }
        }

        var query = new InternalItemsQuery
        {
            IncludeItemTypes = kinds.ToArray(),
            TopParentIds = config.EnabledLibraries,
            Recursive = true
        };

        var items = new List<RatingWorkItem>();
        var liveIds = new HashSet<Guid>();
        foreach (var baseItem in _libraryManager.GetItemList(query))
        {
            var level = ToLevel(baseItem.GetBaseItemKind());
            if (level is null)
            {
                continue;
            }

            items.Add(new RatingWorkItem(
                new RatingItemRef(baseItem.Id, baseItem.Name),
                level.Value,
                ExtractProviderIds(baseItem),
                TargetSource));
            liveIds.Add(baseItem.Id);
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
