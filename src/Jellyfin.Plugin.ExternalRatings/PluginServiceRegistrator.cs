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
    }
}
