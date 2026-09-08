using Jellyfin.Plugin.ExternalRatings.Core;
using Jellyfin.Plugin.ExternalRatings.Resolvers;

namespace Jellyfin.Plugin.ExternalRatings.Tests.Fakes;

/// <summary>
/// An <see cref="IRatingRouter"/> that always routes to one resolver.
/// </summary>
/// <remarks>
/// Deliberately ignores the item's target source: the pipeline, runner and prefetcher tests are
/// about those components, not about routing. Routing itself is covered by
/// <c>RatingRouterTests</c> against the real <see cref="RatingRouter"/>, so a stub resolver here
/// does not need a matching source catalogue.
/// </remarks>
internal sealed class FixedRouter : IRatingRouter
{
    private readonly IRatingResolver _resolver;

    public FixedRouter(IRatingResolver resolver) => _resolver = resolver;

    public RouteDecision Route(RatingWorkItem item)
    {
        var levelServable = _resolver.SupportedInputProviders.ContainsKey(item.Level);
        var selection = InputIdSelector.Select(_resolver, item.Level, item.ProviderIds);

        return selection is null
            ? new RouteDecision(null, null, levelServable)
            : new RouteDecision(_resolver, selection, true);
    }

    public IBatchRatingResolver? TryGetBatchResolver() => _resolver as IBatchRatingResolver;

    public IReadOnlyList<ItemLevel> ServableLevels(string targetSource)
    {
        var levels = new List<ItemLevel>();
        foreach (var level in new[] { ItemLevel.Movie, ItemLevel.Series, ItemLevel.Season, ItemLevel.Episode })
        {
            if (_resolver.SupportedInputProviders.ContainsKey(level))
            {
                levels.Add(level);
            }
        }

        return levels;
    }
}
