using Jellyfin.Plugin.ExternalRatings.Core;
using Jellyfin.Plugin.ExternalRatings.Core.Abstractions;
using Jellyfin.Plugin.ExternalRatings.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.ExternalRatings;

/// <summary>
/// Registers the plugin's services with the host container (spec §5.1). The enrichment service is a
/// singleton so every trigger shares one circuit breaker, cache, and backup store.
/// </summary>
public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<RatingEnrichmentService>();
        serviceCollection.AddSingleton<IScheduledTask, EnrichRatingsScheduledTask>();

        // The realtime listener (§15 step 9) enriches single items on add/update. It reaches the shared
        // enrichment state through the ISingleItemEnricher seam, which resolves to the same singleton
        // service, and needs a clock for its debounce window.
        serviceCollection.AddSingleton<IClock, SystemClock>();
        serviceCollection.AddSingleton<ISingleItemEnricher>(sp => sp.GetRequiredService<RatingEnrichmentService>());
        serviceCollection.AddHostedService<ItemChangedListener>();

        // EnrichRatingsPostScanTask is intentionally NOT registered here: the host discovers
        // ILibraryPostScanTask implementations by assembly scanning and runs them after every scan.
        // Registering it as a service would not make the scan pipeline invoke it (unlike IScheduledTask).
    }
}
