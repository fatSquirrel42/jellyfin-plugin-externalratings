using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.ExternalRatings.Tasks;

/// <summary>
/// The dashboard-triggerable "run now" task (spec §5.3.3). A thin Humble Object: it delegates to the
/// shared <see cref="RatingEnrichmentService"/>. No default triggers — it runs on demand from the
/// Scheduled Tasks page. Automatic post-scan running is handled separately by
/// <see cref="EnrichRatingsPostScanTask"/>.
/// </summary>
public sealed class EnrichRatingsScheduledTask : IScheduledTask
{
    private readonly RatingEnrichmentService _service;

    /// <summary>Initializes a new instance of the <see cref="EnrichRatingsScheduledTask"/> class.</summary>
    /// <param name="service">The shared enrichment service.</param>
    public EnrichRatingsScheduledTask(RatingEnrichmentService service)
    {
        _service = service;
    }

    /// <inheritdoc />
    public string Name => "Enrich External Ratings";

    /// <inheritdoc />
    public string Key => "ExternalRatingsEnrich";

    /// <inheritdoc />
    public string Description => "Resolves external community ratings (MyAnimeList via mdblist) into CommunityRating.";

    /// <inheritdoc />
    public string Category => "External Ratings";

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        => _service.RunAsync(progress, cancellationToken);

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();
}
