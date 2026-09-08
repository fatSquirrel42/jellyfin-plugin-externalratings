using System;
using FluentAssertions;
using Jellyfin.Plugin.ExternalRatings.Core;
using Xunit;

namespace Jellyfin.Plugin.ExternalRatings.Tests;

/// <summary>
/// A season score is all-or-nothing: every episode the library holds must have resolved, or there
/// is no season score. There used to be a configurable coverage threshold, but it counted only the
/// episodes that already had an IMDb id — so a season with 2 of 12 matched showed 100 % coverage
/// and got a two-episode "average". IMDb rates every aired episode, so a gap means an unmatched
/// episode, and an average over the rest would mis-describe the season.
/// </summary>
public class SeasonRatingAggregatorTests
{
    [Fact]
    public void AveragesTheEpisodeScores_Unweighted()
    {
        // IMDb's own season figure is the plain mean of the episode averages, verified against
        // tt7078180 season 1 (see docs/level-support-diagnosis.md §3).
        var ratings = new[] { 9.1f, 8.7f, 8.6f, 8.5f, 8.4f, 8.6f, 8.5f, 8.6f, 8.4f, 8.5f, 8.5f, 8.4f, 8.4f };

        SeasonRatingAggregator.Average(ratings, ratings.Length).Should().Be(8.6f);
    }

    [Fact]
    public void RoundsToOneDecimal()
    {
        SeasonRatingAggregator.Average(new[] { 8.0f, 8.1f, 8.2f }, 3).Should().Be(8.1f);
        SeasonRatingAggregator.Average(new[] { 7.0f, 8.0f }, 2).Should().Be(7.5f);
    }

    [Fact]
    public void RoundsHalvesAwayFromZero()
    {
        // Banker's rounding would give 8.2 here, which disagrees with what IMDb displays.
        SeasonRatingAggregator.Average(new[] { 8.2f, 8.3f }, 2).Should().Be(8.3f);
    }

    [Fact]
    public void ASingleMissingScoreMeansNoSeasonScore()
    {
        // The rule. Eleven of twelve is not "close enough": the twelfth episode is unmatched, and a
        // season score is a promise that all of its episodes were scored.
        var eleven = new[] { 8f, 8f, 8f, 8f, 8f, 8f, 8f, 8f, 8f, 8f, 8f };

        SeasonRatingAggregator.Average(eleven, 12).Should().BeNull();
        SeasonRatingAggregator.Average(eleven, 11).Should().Be(8.0f);
    }

    [Fact]
    public void AFewStrayEpisodesDoNotStandInForASeason()
    {
        // The case the old threshold was documented for but never actually saw.
        SeasonRatingAggregator.Average(new[] { 9.0f, 9.0f }, 20).Should().BeNull();
    }

    [Fact]
    public void NoRatingsMeansNoAverage()
    {
        SeasonRatingAggregator.Average(Array.Empty<float>(), 10).Should().BeNull();
    }

    [Fact]
    public void AnEmptySeasonMeansNoAverage()
    {
        // Zero of zero is vacuously "complete"; it must still not produce a score.
        SeasonRatingAggregator.Average(Array.Empty<float>(), 0).Should().BeNull();
    }

    [Fact]
    public void AnAiringSeasonIsAveragedOverWhatItHolds()
    {
        // Five episodes downloaded, all five scored: that is a complete set as far as the library
        // is concerned. Unaired episodes are virtual items and never counted.
        SeasonRatingAggregator.Average(new[] { 8.0f, 7.0f, 9.0f, 8.0f, 8.0f }, 5).Should().Be(8.0f);
    }

    [Fact]
    public void ASingleEpisodeSeasonWorks()
    {
        SeasonRatingAggregator.Average(new[] { 9.0f }, 1).Should().Be(9.0f);
    }

    [Fact]
    public void AMemberCountBelowTheRatingCountYieldsNoAverage()
    {
        // Cannot happen — each member yields at most one rating — but a miscount must fail closed
        // rather than write a number nobody can explain.
        SeasonRatingAggregator.Average(new[] { 8.0f, 6.0f }, 1).Should().BeNull();
    }

    [Fact]
    public void TheResultStaysInsideJellyfinsScale()
    {
        // H14: the pipeline rejects anything outside 0..10 as an error, so the aggregate must not
        // be able to leave the range on its own.
        SeasonRatingAggregator.Average(new[] { 10.0f, 10.0f }, 2).Should().Be(10.0f);
        SeasonRatingAggregator.Average(new[] { 0.1f, 0.1f }, 2).Should().Be(0.1f);
    }
}
