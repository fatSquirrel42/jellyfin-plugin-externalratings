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
    public void ToPipelineOptions_UnknownUnsupportedBehavior_FallsBackToLeaveExisting()
    {
        var config = new PluginConfiguration { UnsupportedLevelBehavior = "garbage" };

        PluginConfigurationMapper.ToPipelineOptions(config)
            .UnsupportedLevelBehavior.Should().Be(UnsupportedLevelBehavior.LeaveExisting);
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
    public void ResolveSource_NoOverrides_ReturnsDefault()
    {
        var config = new PluginConfiguration { RatingSource = "imdb" };

        PluginConfigurationMapper.ResolveSource(config, new[] { Guid.NewGuid() }).Should().Be("imdb");
    }

    [Fact]
    public void ResolveSource_OverrideMatches_ReturnsOverride()
    {
        var libraryId = Guid.NewGuid();
        var config = new PluginConfiguration
        {
            RatingSource = "imdb",
            LibrarySources = new[] { new LibrarySourceSetting { LibraryId = libraryId, Source = "myanimelist" } }
        };

        PluginConfigurationMapper.ResolveSource(config, new[] { libraryId }).Should().Be("myanimelist");
    }

    [Fact]
    public void ResolveSource_NoMatchingLibrary_ReturnsDefault()
    {
        var config = new PluginConfiguration
        {
            RatingSource = "imdb",
            LibrarySources = new[] { new LibrarySourceSetting { LibraryId = Guid.NewGuid(), Source = "myanimelist" } }
        };

        PluginConfigurationMapper.ResolveSource(config, new[] { Guid.NewGuid() }).Should().Be("imdb");
    }

    [Fact]
    public void ResolveSource_ItemInMultipleLibraries_FirstMatchingOverrideWins()
    {
        var animeLib = Guid.NewGuid();
        var config = new PluginConfiguration
        {
            RatingSource = "imdb",
            LibrarySources = new[] { new LibrarySourceSetting { LibraryId = animeLib, Source = "letterboxd" } }
        };

        // The item belongs to two libraries; the one with an override determines the source.
        PluginConfigurationMapper.ResolveSource(config, new[] { Guid.NewGuid(), animeLib }).Should().Be("letterboxd");
    }

    [Fact]
    public void ResolveSource_BlankDefault_FallsBackToNone()
    {
        var config = new PluginConfiguration { RatingSource = "  " };

        PluginConfigurationMapper.ResolveSource(config, Array.Empty<Guid>()).Should().Be("none");
    }

    [Fact]
    public void ResolveSource_NoneDefault_ReturnsNone()
    {
        var config = new PluginConfiguration { RatingSource = "none" };

        PluginConfigurationMapper.ResolveSource(config, new[] { Guid.NewGuid() }).Should().Be("none");
    }

    [Fact]
    public void ResolveSource_NoneDefault_LibraryOverrideStillApplies()
    {
        var libraryId = Guid.NewGuid();
        var config = new PluginConfiguration
        {
            RatingSource = "none",
            LibrarySources = new[] { new LibrarySourceSetting { LibraryId = libraryId, Source = "imdb" } }
        };

        // Overridden library enriches; everything else stays "none" (untouched).
        PluginConfigurationMapper.ResolveSource(config, new[] { libraryId }).Should().Be("imdb");
        PluginConfigurationMapper.ResolveSource(config, new[] { Guid.NewGuid() }).Should().Be("none");
    }

    [Theory]
    [InlineData("none", true)]
    [InlineData("None", true)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("imdb", false)]
    [InlineData("myanimelist", false)]
    public void IsNoSource_DetectsDisabledSource(string source, bool expected)
    {
        PluginConfigurationMapper.IsNoSource(source).Should().Be(expected);
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
