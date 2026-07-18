using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.ExternalRatings.Tasks;

/// <summary>
/// Runs enrichment automatically after every library scan (spec §15 step 8). A thin Humble Object
/// that delegates to the shared <see cref="RatingEnrichmentService"/>, gated by the
/// <c>RunAfterLibraryScan</c> config toggle so admins can opt out.
/// </summary>
/// <remarks>
/// The host discovers <see cref="ILibraryPostScanTask"/> implementations by assembly scanning and
/// runs them all at the end of a scan (there is no trigger/schedule of its own), so this type is
/// intentionally NOT registered in <see cref="PluginServiceRegistrator"/>. Its constructor
/// dependencies are still resolved from the host DI container, which is where the shared singleton
/// service lives.
/// </remarks>
public sealed class EnrichRatingsPostScanTask : ILibraryPostScanTask
{
    private readonly RatingEnrichmentService _service;

    /// <summary>Initializes a new instance of the <see cref="EnrichRatingsPostScanTask"/> class.</summary>
    /// <param name="service">The shared enrichment service.</param>
    public EnrichRatingsPostScanTask(RatingEnrichmentService service)
    {
        _service = service;
    }

    /// <inheritdoc />
    public Task Run(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (Plugin.Instance?.Configuration.RunAfterLibraryScan != true)
        {
            return Task.CompletedTask;
        }

        return _service.RunAsync(progress, cancellationToken);
    }
}
