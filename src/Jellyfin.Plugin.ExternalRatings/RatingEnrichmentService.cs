using System;
using System.Collections.Generic;
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
using MediaBrowser.Common.Net;
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

    private readonly ILibraryManager _libraryManager;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<RatingEnrichmentService> _logger;

    // Shared, run-spanning state.
    private readonly SystemClock _clock;
    private readonly FileRatingCache _cache;
    private readonly BackupStore _backup;
    private readonly CircuitBreaker _breaker;
    private readonly JellyfinItemWriter _writer;
    private readonly object _statusGate = new();

    private RunSummary? _lastSummary;
    private DateTimeOffset? _lastRunUtc;

    /// <summary>Initializes a new instance of the <see cref="RatingEnrichmentService"/> class.</summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="applicationPaths">The application paths (for the plugin data directory).</param>
    /// <param name="loggerFactory">The logger factory.</param>
    public RatingEnrichmentService(
        ILibraryManager libraryManager,
        IHttpClientFactory httpClientFactory,
        IApplicationPaths applicationPaths,
        ILoggerFactory loggerFactory)
    {
        _libraryManager = libraryManager;
        _httpClientFactory = httpClientFactory;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<RatingEnrichmentService>();

        var dataDir = System.IO.Path.Combine(applicationPaths.DataPath, "ExternalRatings");
        _clock = new SystemClock();
        _cache = new FileRatingCache(new FileCacheFileStore(System.IO.Path.Combine(dataDir, "cache.json")), _clock);
        _backup = new BackupStore(new FileCacheFileStore(System.IO.Path.Combine(dataDir, "backup.json")), _clock);
        _breaker = new CircuitBreaker(_clock);
        _writer = new JellyfinItemWriter(libraryManager);
    }

    /// <summary>Runs a full enrichment pass over the enabled libraries (scheduled-task entry point).</summary>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the run finishes.</returns>
    public async Task RunScheduledAsync(IProgress<double>? progress, CancellationToken cancellationToken)
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

        var httpClient = _httpClientFactory.CreateClient(NamedClient.Default);
        httpClient.BaseAddress = new Uri("https://api.mdblist.com/");
        var resolver = new MdblistResolver(httpClient, apiKey, new Logger<MdblistResolver>(_loggerFactory));

        var pipeline = new RatingPipeline(
            resolver,
            _writer,
            _backup,
            _cache,
            _clock,
            _breaker,
            () => PluginConfigurationMapper.ToPipelineOptions(Plugin.Instance!.Configuration),
            new Logger<RatingPipeline>(_loggerFactory));

        var runner = new EnrichmentRunner(pipeline, _cache, _backup, new Logger<EnrichmentRunner>(_loggerFactory));

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

    /// <summary>Disposes the owned cache and backup stores.</summary>
    public void Dispose()
    {
        _cache.Dispose();
        _backup.Dispose();
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
