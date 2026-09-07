using System;
using FluentAssertions;
using Jellyfin.Plugin.ExternalRatings.Core;
using Xunit;

namespace Jellyfin.Plugin.ExternalRatings.Tests;

public class SeasonRatingAggregatorTests
{
    [Fact]
    public void AveragesTheEpisodeScores_Unweighted()
    {
        // IMDb's own season figure is the plain mean of the episode averages, verified against
        // tt7078180 season 1 (see docs/level-support-diagnosis.md §3).
        var ratings = new[] { 9.1f, 8.7f, 8.6f, 8.5f, 8.4f, 8.6f, 8.5f, 8.6f, 8.4f, 8.5f, 8.5f, 8.4f, 8.4f };

        SeasonRatingAggregator.Average(ratings, ratings.Length, 50).Should().Be(8.6f);
    }

    [Fact]
    public void RoundsToOneDecimal()
    {
        SeasonRatingAggregator.Average(new[] { 8.0f, 8.1f, 8.2f }, 3, 0).Should().Be(8.1f);
        SeasonRatingAggregator.Average(new[] { 7.0f, 8.0f }, 2, 0).Should().Be(7.5f);
    }

    [Fact]
    public void RoundsHalvesAwayFromZero()
    {
        // Banker's rounding would give 8.2 here, which disagrees with what IMDb displays.
        SeasonRatingAggregator.Average(new[] { 8.2f, 8.3f }, 2, 0).Should().Be(8.3f);
    }

    [Fact]
    public void NoRatingsMeansNoAverage()
    {
        SeasonRatingAggregator.Average(Array.Empty<float>(), 10, 0).Should().BeNull();
        SeasonRatingAggregator.Average(Array.Empty<float>(), 0, 0).Should().BeNull();
    }

    [Fact]
    public void BelowTheCoverageThreshold_ThereIsNoAverage()
    {
        // Two resolved episodes out of twenty describe the two, not the season.
        SeasonRatingAggregator.Average(new[] { 9.0f, 9.0f }, 20, 50).Should().BeNull();
    }

    [Fact]
    public void AtTheCoverageThreshold_TheAverageStands()
    {
        SeasonRatingAggregator.Average(new[] { 9.0f, 8.0f }, 4, 50).Should().Be(8.5f);
    }

    [Fact]
    public void AThresholdOfZeroAcceptsASingleEpisode()
    {
        SeasonRatingAggregator.Average(new[] { 9.0f }, 20, 0).Should().Be(9.0f);
    }

    [Fact]
    public void FullCoverageAlwaysPasses()
    {
        SeasonRatingAggregator.Average(new[] { 6.0f, 7.0f }, 2, 100).Should().Be(6.5f);
    }

    [Fact]
    public void CoverageUsesTheEpisodeCount_NotTheRatingCount()
    {
        // memberCount is how many episodes the season has; the ratings are the ones that resolved.
        SeasonRatingAggregator.Average(new[] { 8.0f, 8.0f, 8.0f }, 4, 75).Should().Be(8.0f);
        SeasonRatingAggregator.Average(new[] { 8.0f, 8.0f, 8.0f }, 5, 75).Should().BeNull();
    }

    [Fact]
    public void AMemberCountBelowTheRatingCountIsTolerated()
    {
        // Defensive: a caller miscounting must not produce a >100% coverage rejection.
        SeasonRatingAggregator.Average(new[] { 8.0f, 6.0f }, 1, 100).Should().Be(7.0f);
    }

    [Fact]
    public void TheResultStaysInsideJellyfinsScale()
    {
        // H14: the pipeline rejects anything outside 0..10 as an error, so the aggregate must not
        // be able to leave the range on its own.
        SeasonRatingAggregator.Average(new[] { 10.0f, 10.0f }, 2, 0).Should().Be(10.0f);
        SeasonRatingAggregator.Average(new[] { 0.1f, 0.1f }, 2, 0).Should().Be(0.1f);
    }
}
