using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.ExternalRatings.Core;

/// <summary>
/// Derives a season's score from its episodes' scores.
/// </summary>
/// <remarks>
/// <para>
/// IMDb has no season entity, so there is no season rating to read — the figure IMDb's own site
/// shows is computed, and it is the plain unweighted mean of the episode averages. Verified against
/// <c>tt7078180</c> season 1, where the site's "8.6 average from 43K episode ratings" reproduces
/// exactly as the mean of the 13 episode scores (the vote-weighted mean would be 8.856). See
/// <c>docs/level-support-diagnosis.md</c> §3.
/// </para>
/// <para>
/// The scores handed in are IMDb's whole season, not the episodes the library happens to hold —
/// <see cref="Resolvers.ImdbDatasetResolver"/> identifies the season from its episodes and then
/// collects every episode IMDb lists for it. Unrated episodes simply do not appear, which is what
/// IMDb's own figure does too, so there is nothing to gate on here: this is a mean and a rounding
/// rule, and the judgement lives in the resolver.
/// </para>
/// </remarks>
internal static class SeasonRatingAggregator
{
    /// <summary>Averages the episode scores of a season.</summary>
    /// <param name="memberRatings">The season's episode scores, on Jellyfin's 0–10 scale.</param>
    /// <returns>The mean rounded to one decimal, or <see langword="null"/> when there is nothing to average.</returns>
    public static float? Average(IReadOnlyList<float> memberRatings)
    {
        if (memberRatings.Count == 0)
        {
            return null;
        }

        double sum = 0;
        foreach (var rating in memberRatings)
        {
            sum += rating;
        }

        // Away-from-zero, not the .NET default of to-even: IMDb's displayed figure follows the
        // former, and a season score differing from the site by 0.1 reads as a bug.
        return (float)Math.Round(sum / memberRatings.Count, 1, MidpointRounding.AwayFromZero);
    }
}
