using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.ExternalRatings.Core;

/// <summary>
/// Picks exactly one input id per item, level-restricted, in priority order Tmdb → Imdb → Tvdb (spec §5.2).
/// </summary>
internal static class InputIdSelector
{
    private static readonly string[] MovieProviders = { "Tmdb", "Imdb" };
    private static readonly string[] SeriesProviders = { "Tmdb", "Imdb", "Tvdb" };
    private static readonly string[] NoProviders = Array.Empty<string>();

    /// <summary>Returns the allowed input providers for a level, in priority order.</summary>
    /// <param name="level">The item level.</param>
    /// <returns>The allowed provider keys.</returns>
    public static IReadOnlyList<string> ProviderPriority(ItemLevel level) => level switch
    {
        ItemLevel.Movie => MovieProviders,
        ItemLevel.Series => SeriesProviders,
        _ => NoProviders
    };

    /// <summary>Selects the single input id for an item, or <see langword="null"/> if none is usable.</summary>
    /// <param name="level">The item level.</param>
    /// <param name="providerIds">The available provider ids (keys are case-insensitive).</param>
    /// <returns>The selection, or <see langword="null"/>.</returns>
    public static InputIdSelection? Select(ItemLevel level, IReadOnlyDictionary<string, string> providerIds)
    {
        var allowed = ProviderPriority(level);
        if (allowed.Count == 0 || providerIds.Count == 0)
        {
            return null;
        }

        var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in providerIds)
        {
            lookup[pair.Key] = pair.Value;
        }

        string? chosenProvider = null;
        string? chosenId = null;
        var presentCount = 0;

        foreach (var provider in allowed)
        {
            if (!lookup.TryGetValue(provider, out var id) || string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            presentCount++;
            if (chosenProvider is null)
            {
                chosenProvider = provider;
                chosenId = id;
            }
        }

        if (chosenProvider is null || chosenId is null)
        {
            return null;
        }

        return new InputIdSelection(chosenProvider, chosenId, presentCount > 1);
    }
}
