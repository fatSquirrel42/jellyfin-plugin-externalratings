using System;
using System.Collections.Generic;
using Jellyfin.Plugin.ExternalRatings.Resolvers;

namespace Jellyfin.Plugin.ExternalRatings.Core;

/// <summary>
/// Picks exactly one input id per item from the providers the active resolver accepts for that
/// level (spec §5.2).
/// </summary>
/// <remarks>
/// The allowed providers and their order come from
/// <see cref="IRatingResolver.SupportedInputProviders"/>, never from a table here: mdblist ranks
/// Tmdb → Imdb → Tvdb, while a resolver reading the IMDb datasets speaks only Imdb. Presence of a
/// level key in that dictionary is also what marks the level as supported at all, so a single
/// declaration drives both the capability and the priority.
/// </remarks>
internal static class InputIdSelector
{
    private static readonly string[] NoProviders = Array.Empty<string>();

    /// <summary>Returns the resolver's allowed input providers for a level, in priority order.</summary>
    /// <param name="resolver">The active resolver.</param>
    /// <param name="level">The item level.</param>
    /// <returns>The allowed provider keys, empty when the resolver does not support the level.</returns>
    public static IReadOnlyList<string> ProviderPriority(IRatingResolver resolver, ItemLevel level)
        => resolver.SupportedInputProviders.TryGetValue(level, out var providers) ? providers : NoProviders;

    /// <summary>Selects the single input id for an item, or <see langword="null"/> if none is usable.</summary>
    /// <param name="resolver">The active resolver, whose capability defines the allowed providers.</param>
    /// <param name="level">The item level.</param>
    /// <param name="providerIds">The available provider ids (keys are case-insensitive).</param>
    /// <returns>The selection, or <see langword="null"/>.</returns>
    public static InputIdSelection? Select(IRatingResolver resolver, ItemLevel level, IReadOnlyDictionary<string, string> providerIds)
        => Select(ProviderPriority(resolver, level), providerIds);

    /// <summary>Selects the single input id for an item, or <see langword="null"/> if none is usable.</summary>
    /// <param name="allowed">The allowed input providers, in priority order.</param>
    /// <param name="providerIds">The available provider ids (keys are case-insensitive).</param>
    /// <returns>The selection, or <see langword="null"/>.</returns>
    public static InputIdSelection? Select(IReadOnlyList<string> allowed, IReadOnlyDictionary<string, string> providerIds)
    {
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
