using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.ExternalRatings.Core;

/// <summary>
/// Removes provider ids an item only appears to own because they were inherited from its parent.
/// </summary>
/// <remarks>
/// Some libraries store a *series'* external id on its Episode items. Nothing downstream can tell
/// that apart from a genuine per-episode id, so the episode would resolve to the series' score and
/// have it written as its own — silently, on every episode of that series. Dropping the id instead
/// leaves the episode with no usable input, which is the honest outcome.
/// <para>
/// The same hazard is documented in TheTVDB plugin's own episode provider, which prefers series ids
/// over item ids for the mirror-image reason (see <c>docs/level-support-diagnosis.md</c> §5).
/// </para>
/// </remarks>
internal static class InheritedProviderIdFilter
{
    /// <summary>Returns <paramref name="itemIds"/> without ids identical to the parent's own.</summary>
    /// <param name="itemIds">The item's provider ids.</param>
    /// <param name="parentIds">The parent's provider ids (empty when there is no parent).</param>
    /// <returns>A new dictionary with case-insensitive keys.</returns>
    public static Dictionary<string, string> Strip(
        IReadOnlyDictionary<string, string> itemIds,
        IReadOnlyDictionary<string, string> parentIds)
    {
        var result = new Dictionary<string, string>(itemIds.Count, StringComparer.OrdinalIgnoreCase);

        foreach (var pair in itemIds)
        {
            // A blank is not an id; the selector skips it anyway, so do not reason about it here.
            if (!string.IsNullOrWhiteSpace(pair.Value)
                && parentIds.TryGetValue(pair.Key, out var parentValue)
                && !string.IsNullOrWhiteSpace(parentValue)
                && string.Equals(pair.Value.Trim(), parentValue.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            result[pair.Key] = pair.Value;
        }

        return result;
    }
}
