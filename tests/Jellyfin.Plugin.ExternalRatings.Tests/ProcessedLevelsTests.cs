using System;
using FluentAssertions;
using Jellyfin.Plugin.ExternalRatings;
using Jellyfin.Plugin.ExternalRatings.Configuration;
using Jellyfin.Plugin.ExternalRatings.Core;
using Xunit;

namespace Jellyfin.Plugin.ExternalRatings.Tests;

/// <summary>
/// The processed levels are the user's global opt-in, nothing more. Resolver capability used to be
/// intersected in, which made sense while one chosen provider served the whole library; it stopped
/// making sense once sources are per-library and the backend is chosen per item.
/// </summary>
public class ProcessedLevelsTests
{
    [Fact]
    public void DefaultConfiguration_ProcessesMovieAndSeriesOnly()
    {
        // The upgrade contract: an existing install must not silently start writing episodes.
        var config = new PluginConfiguration();

        PluginConfigurationMapper.ParseLevels(config.EnabledLevels)
            .Should().Equal(ItemLevel.Movie, ItemLevel.Series);
    }

    [Fact]
    public void OptingIntoEpisode_AddsIt()
    {
        var config = new PluginConfiguration { EnabledLevels = new[] { "Movie", "Series", "Episode" } };

        PluginConfigurationMapper.ParseLevels(config.EnabledLevels)
            .Should().Equal(ItemLevel.Movie, ItemLevel.Series, ItemLevel.Episode);
    }

    [Fact]
    public void LevelNamesAreCaseInsensitiveAndTrimmed()
    {
        PluginConfigurationMapper.ParseLevels(new[] { " episode ", "SEASON" })
            .Should().Equal(ItemLevel.Season, ItemLevel.Episode);
    }

    [Fact]
    public void ResultIsInCanonicalOrder_RegardlessOfConfigOrder()
    {
        PluginConfigurationMapper.ParseLevels(new[] { "Episode", "Movie", "Season", "Series" })
            .Should().Equal(ItemLevel.Movie, ItemLevel.Series, ItemLevel.Season, ItemLevel.Episode);
    }

    [Fact]
    public void UnknownAndBlankNamesAreIgnored()
    {
        PluginConfigurationMapper.ParseLevels(new[] { "Movie", "Sasquatch", "", "   " })
            .Should().Equal(ItemLevel.Movie);
    }

    [Fact]
    public void DuplicatesCollapse()
    {
        // XmlSerializer round-trips have bitten this config before; a duplicated entry must not
        // enumerate the level twice.
        PluginConfigurationMapper.ParseLevels(new[] { "Movie", "Movie", "Series" })
            .Should().Equal(ItemLevel.Movie, ItemLevel.Series);
    }

    [Fact]
    public void AnEmptyListMeansNothingIsProcessed()
    {
        // Distinct from "unset": unchecking every level in the UI must disable the plugin's writes,
        // not silently restore the defaults.
        PluginConfigurationMapper.ParseLevels(Array.Empty<string>()).Should().BeEmpty();
    }

    [Fact]
    public void ProcessedLevels_IsTheOptInAlone_NotIntersectedWithAnyResolver()
    {
        // Deliberately independent of capability. The opt-in is global while sources are
        // per-library, so no single resolver may decide what gets enumerated; whether an item can
        // be served is a routing question answered per item.
        var config = new PluginConfiguration { EnabledLevels = new[] { "Movie", "Series", "Season", "Episode" } };

        RatingEnrichmentService.ProcessedLevels(config)
            .Should().Equal(ItemLevel.Movie, ItemLevel.Series, ItemLevel.Season, ItemLevel.Episode);
    }

    [Fact]
    public void ProcessedLevels_EnumeratesAnEpisode_EvenWhereNoSourceCouldServeIt()
    {
        // The consequence of the above, and the point of the design: an episode in a library whose
        // source has no episode scores is still enumerated, routes nowhere, and is then cleared
        // under the shipped ClearField default rather than quietly skipped.
        var config = new PluginConfiguration { EnabledLevels = new[] { "Episode" } };

        RatingEnrichmentService.ProcessedLevels(config).Should().Equal(ItemLevel.Episode);
    }

    [Fact]
    public void ProcessedLevels_OmitsLevelsTheUserDidNotEnable()
    {
        var config = new PluginConfiguration { EnabledLevels = new[] { "Movie", "Series" } };

        RatingEnrichmentService.ProcessedLevels(config).Should().Equal(ItemLevel.Movie, ItemLevel.Series);
    }

    [Fact]
    public void ProcessedLevels_CanBeEmpty()
    {
        var config = new PluginConfiguration { EnabledLevels = Array.Empty<string>() };

        RatingEnrichmentService.ProcessedLevels(config).Should().BeEmpty();
    }
}
