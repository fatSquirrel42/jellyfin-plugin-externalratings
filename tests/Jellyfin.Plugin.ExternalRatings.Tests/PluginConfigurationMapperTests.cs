using System;
using FluentAssertions;
using Jellyfin.Plugin.ExternalRatings.Configuration;
using Jellyfin.Plugin.ExternalRatings.Core;
using Xunit;

namespace Jellyfin.Plugin.ExternalRatings.Tests;

public class PluginConfigurationMapperTests
{
    [Fact]
    public void ToPipelineOptions_MapsScalarsAndDurations()
    {
        var config = new PluginConfiguration
        {
            DryRun = false,
            NoMatchBehavior = "ClearField",
            UnsupportedLevelBehavior = "ClearField",
            CacheTtlDays = 14,
            NegativeCacheTtlDays = 3
        };

        var options = PluginConfigurationMapper.ToPipelineOptions(config);

        options.DryRun.Should().BeFalse();
        options.NoMatchBehavior.Should().Be(NoMatchBehavior.ClearField);
        options.UnsupportedLevelBehavior.Should().Be(UnsupportedLevelBehavior.ClearField);
        options.CacheTtl.Should().Be(TimeSpan.FromDays(14));
        options.NegativeCacheTtl.Should().Be(TimeSpan.FromDays(3));
    }

    [Fact]
    public void ToPipelineOptions_KeepsDefaultsNotInConfig()
    {
        var options = PluginConfigurationMapper.ToPipelineOptions(new PluginConfiguration());

        // Epsilon and the error-cache TTL are not user-configurable (§10.1) → PipelineOptions defaults.
        options.Epsilon.Should().Be(new PipelineOptions().Epsilon);
        options.ErrorCacheTtl.Should().Be(new PipelineOptions().ErrorCacheTtl);
    }

    [Fact]
    public void ToPipelineOptions_UnknownNoMatchBehavior_FallsBackToLeaveExisting()
    {
        var config = new PluginConfiguration { NoMatchBehavior = "garbage" };

        PluginConfigurationMapper.ToPipelineOptions(config)
            .NoMatchBehavior.Should().Be(NoMatchBehavior.LeaveExisting);
    }

    [Fact]
    public void ToPipelineOptions_UnknownUnsupportedBehavior_FallsBackToSkip()
    {
        var config = new PluginConfiguration { UnsupportedLevelBehavior = "garbage" };

        PluginConfigurationMapper.ToPipelineOptions(config)
            .UnsupportedLevelBehavior.Should().Be(UnsupportedLevelBehavior.Skip);
    }

    [Fact]
    public void ToPipelineOptions_BehaviorParsing_IsCaseInsensitive()
    {
        var config = new PluginConfiguration
        {
            NoMatchBehavior = "clearfield",
            UnsupportedLevelBehavior = "CLEARFIELD"
        };

        var options = PluginConfigurationMapper.ToPipelineOptions(config);

        options.NoMatchBehavior.Should().Be(NoMatchBehavior.ClearField);
        options.UnsupportedLevelBehavior.Should().Be(UnsupportedLevelBehavior.ClearField);
    }

    [Fact]
    public void ParseLevels_ValidStrings_MapToEnum()
    {
        var config = new PluginConfiguration { ProcessedLevels = new[] { "Movie", "Series", "Season", "Episode" } };

        PluginConfigurationMapper.ParseLevels(config)
            .Should().Equal(ItemLevel.Movie, ItemLevel.Series, ItemLevel.Season, ItemLevel.Episode);
    }

    [Fact]
    public void ParseLevels_IsCaseInsensitive()
    {
        var config = new PluginConfiguration { ProcessedLevels = new[] { "movie", "SERIES" } };

        PluginConfigurationMapper.ParseLevels(config).Should().Equal(ItemLevel.Movie, ItemLevel.Series);
    }

    [Fact]
    public void ParseLevels_DropsInvalidEntries()
    {
        var config = new PluginConfiguration { ProcessedLevels = new[] { "Movie", "Nonsense", "Series" } };

        PluginConfigurationMapper.ParseLevels(config).Should().Equal(ItemLevel.Movie, ItemLevel.Series);
    }

    [Fact]
    public void ParseLevels_Deduplicates()
    {
        var config = new PluginConfiguration { ProcessedLevels = new[] { "Movie", "Movie", "Series" } };

        PluginConfigurationMapper.ParseLevels(config).Should().Equal(ItemLevel.Movie, ItemLevel.Series);
    }

    [Fact]
    public void ParseLevels_Empty_FallsBackToMovieAndSeries()
    {
        var config = new PluginConfiguration { ProcessedLevels = Array.Empty<string>() };

        PluginConfigurationMapper.ParseLevels(config).Should().Equal(ItemLevel.Movie, ItemLevel.Series);
    }

    [Fact]
    public void ParseLevels_AllInvalid_FallsBackToMovieAndSeries()
    {
        var config = new PluginConfiguration { ProcessedLevels = new[] { "foo", "bar" } };

        PluginConfigurationMapper.ParseLevels(config).Should().Equal(ItemLevel.Movie, ItemLevel.Series);
    }

    [Fact]
    public void GetResolverSetting_ReturnsValue_WhenKeyPresent()
    {
        var config = new PluginConfiguration
        {
            ResolverSettings = new[] { new ResolverSetting { Key = "mdblist.apiKey", Value = "abc123" } }
        };

        PluginConfigurationMapper.GetResolverSetting(config, "mdblist.apiKey").Should().Be("abc123");
    }

    [Fact]
    public void GetResolverSetting_ReturnsNull_WhenKeyMissing()
    {
        var config = new PluginConfiguration();

        PluginConfigurationMapper.GetResolverSetting(config, "mdblist.apiKey").Should().BeNull();
    }
}
