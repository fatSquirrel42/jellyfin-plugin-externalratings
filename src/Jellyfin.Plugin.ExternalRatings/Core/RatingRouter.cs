using System;
using System.Collections.Generic;
using Jellyfin.Plugin.ExternalRatings.Resolvers;

namespace Jellyfin.Plugin.ExternalRatings.Core;

/// <summary>
/// Picks the resolver for an item from (rating source × item level × available provider ids).
/// </summary>
/// <remarks>
/// <para>
/// This exists so the user never has to choose a backend. They pick a rating source; whether that
/// is reachable through the local IMDb dataset or through the mdblist API — and with which of the
/// item's ids — is a routing question, not a configuration question.
/// </para>
/// <para>
/// Candidate order is the preference order and the caller owns it: the facade lists the offline
/// dataset first (no key, no quota, all four levels) and mdblist second, and leaves mdblist out
/// entirely when no API key is configured. That is how "no key" becomes "those sources are
/// unreachable" rather than a special case in here.
/// </para>
/// </remarks>
internal sealed class RatingRouter : IRatingRouter
{
    private static readonly ItemLevel[] CanonicalLevels =
    {
        ItemLevel.Movie, ItemLevel.Series, ItemLevel.Season, ItemLevel.Episode
    };

    private readonly IReadOnlyList<IRatingResolver> _candidates;

    /// <summary>Initializes a new instance of the <see cref="RatingRouter"/> class.</summary>
    /// <param name="candidates">The usable resolvers, most preferred first.</param>
    public RatingRouter(IReadOnlyList<IRatingResolver> candidates)
    {
        _candidates = candidates;
    }

    /// <inheritdoc />
    public RouteDecision Route(RatingWorkItem item)
    {
        var levelServable = false;

        foreach (var resolver in _candidates)
        {
            if (!ServesSource(resolver, item.TargetSource))
            {
                continue;
            }

            if (!resolver.SupportedInputProviders.ContainsKey(item.Level))
            {
                continue;
            }

            // Reached only when this resolver offers the source *and* the level, so the pair is
            // servable in principle even if this particular item has no id for it.
            levelServable = true;

            var selection = InputIdSelector.Select(resolver, item.Level, item.ProviderIds);
            if (selection is null)
            {
                continue;
            }

            return new RouteDecision(resolver, selection, true);
        }

        return new RouteDecision(null, null, levelServable);
    }

    /// <inheritdoc />
    public IBatchRatingResolver? TryGetBatchResolver()
    {
        foreach (var resolver in _candidates)
        {
            if (resolver is IBatchRatingResolver batch)
            {
                return batch;
            }
        }

        return null;
    }

    /// <inheritdoc />
    public IReadOnlyList<ItemLevel> ServableLevels(string targetSource)
    {
        var levels = new List<ItemLevel>(CanonicalLevels.Length);

        foreach (var level in CanonicalLevels)
        {
            foreach (var resolver in _candidates)
            {
                if (ServesSource(resolver, targetSource) && resolver.SupportedInputProviders.ContainsKey(level))
                {
                    levels.Add(level);
                    break;
                }
            }
        }

        return levels;
    }

    private static bool ServesSource(IRatingResolver resolver, string targetSource)
    {
        foreach (var source in resolver.SupportedRatingSources)
        {
            if (string.Equals(source.Key, targetSource, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
