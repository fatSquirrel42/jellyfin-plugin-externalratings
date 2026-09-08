using System;
using FluentAssertions;
using Jellyfin.Plugin.ExternalRatings.Core;
using Xunit;

namespace Jellyfin.Plugin.ExternalRatings.Tests;

/// <summary>
/// The aggregator is now only a mean and a rounding rule. It used to carry a completeness gate,
/// which made sense while the denominator was "the episodes on disk" — but the season is IMDb's
/// season, so the scores handed in are already the right set and every judgement about which
/// episodes belong to it lives in <c>ImdbDatasetResolver</c>.
/// </summary>
public class SeasonRatingAggregatorTests
{
    [Fact]
    public void AveragesTheEpisodeScores_Unweighted()
    {
        // IMDb's own season figure is the plain mean of the episode averages, verified against
        // tt7078180 season 1 (see docs/level-support-diagnosis.md §3).
        var ratings = new[] { 9.1f, 8.7f, 8.6f, 8.5f, 8.4f, 8.6f, 8.5f, 8.6f, 8.4f, 8.5f, 8.5f, 8.4f, 8.4f };

        SeasonRatingAggregator.Average(ratings).Should().Be(8.6f);
    }

    [Fact]
    public void RoundsToOneDecimal()
    {
        SeasonRatingAggregator.Average(new[] { 8.0f, 8.1f, 8.2f }).Should().Be(8.1f);
        SeasonRatingAggregator.Average(new[] { 7.0f, 8.0f }).Should().Be(7.5f);
    }

    [Fact]
    public void RoundsHalvesAwayFromZero()
    {
        // Banker's rounding would give 8.2 here, which disagrees with what IMDb displays.
        SeasonRatingAggregator.Average(new[] { 8.2f, 8.3f }).Should().Be(8.3f);
    }

    [Fact]
    public void NoRatingsMeansNoAverage()
    {
        // The resolver's "IMDb lists this season but has rated none of it" case. Fail closed rather
        // than divide by zero.
        SeasonRatingAggregator.Average(Array.Empty<float>()).Should().BeNull();
    }

    [Fact]
    public void ASingleEpisodeSeasonWorks()
    {
        SeasonRatingAggregator.Average(new[] { 9.0f }).Should().Be(9.0f);
    }

    [Fact]
    public void TheResultStaysInsideJellyfinsScale()
    {
        // H14: the pipeline rejects anything outside 0..10 as an error, so the aggregate must not
        // be able to leave the range on its own.
        SeasonRatingAggregator.Average(new[] { 10.0f, 10.0f }).Should().Be(10.0f);
        SeasonRatingAggregator.Average(new[] { 0.1f, 0.1f }).Should().Be(0.1f);
    }
}
