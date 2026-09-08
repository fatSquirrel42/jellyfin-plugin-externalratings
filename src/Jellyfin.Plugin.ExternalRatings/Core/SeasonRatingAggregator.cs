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
/// The average is taken over the episodes the *library* has, not over IMDb's idea of the season.
/// Where the two numberings agree — nearly always — the result is the value IMDb displays. Where
/// they diverge it deliberately differs: asking "which IMDb season is this Jellyfin season?" is the
/// positional guess that was rejected for episodes, and averaging IMDb's members would describe a
/// season the user does not have.
/// </para>
/// <para>
/// It is all-or-nothing. IMDb rates every episode that has aired, so a member without a score is
/// an *unmatched* episode rather than an unrated one, and an average over the remainder would
/// quietly describe a different season than the one being written to. This replaced a configurable
/// coverage threshold that could not do the job: it counted only episodes that already had an IMDb
/// id, so a season with 2 of 12 matched reported 100 % coverage and got a two-episode "average".
/// </para>
/// </remarks>
internal static class SeasonRatingAggregator
{
    /// <summary>Averages the episode scores, but only if every episode of the season resolved.</summary>
    /// <param name="memberRatings">The scores that resolved, on Jellyfin's 0–10 scale.</param>
    /// <param name="memberCount">
    /// How many episodes the season holds — every non-virtual episode, matched or not. Unaired
    /// episodes are virtual items and are never counted, so an airing season is judged complete on
    /// the episodes it actually has.
    /// </param>
    /// <returns>The mean rounded to one decimal, or <see langword="null"/> if it should not be written.</returns>
    public static float? Average(IReadOnlyList<float> memberRatings, int memberCount)
    {
        // Zero of zero is vacuously complete; an empty season must still produce nothing.
        if (memberCount <= 0 || memberRatings.Count != memberCount)
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
