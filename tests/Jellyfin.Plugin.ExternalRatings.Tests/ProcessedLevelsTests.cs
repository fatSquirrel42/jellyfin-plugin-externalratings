using System;
using System.Collections.Generic;
using System.Net.Http;
using FluentAssertions;
using Jellyfin.Plugin.ExternalRatings;
using Jellyfin.Plugin.ExternalRatings.Configuration;
using Jellyfin.Plugin.ExternalRatings.Core;
using Jellyfin.Plugin.ExternalRatings.Resolvers;
using Jellyfin.Plugin.ExternalRatings.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ExternalRatings.Tests;

/// <summary>
/// The processed levels are the intersection of what the resolver can do and what the user opted
/// into. Capability alone used to decide it, which is fine at two levels but not once Episode
/// multiplies the item count by one to two orders of magnitude.
/// </summary>
public class ProcessedLevelsTests
{
    private static StubRatingResolver AllFourLevels() => new()
    {
        SupportedInputProviders = new Dictionary<ItemLevel, IReadOnlyList<string>>
        {
            [ItemLevel.Movie] = new[] { "Imdb" },
            [ItemLevel.Series] = new[] { "Imdb" },
            [ItemLevel.Season] = new[] { "Imdb" },
            [ItemLevel.Episode] = new[] { "Imdb" }
        }
    };

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
    public void ProcessedLevels_IsTheIntersectionOfCapabilityAndConfig()
    {
        var config = new PluginConfiguration { EnabledLevels = new[] { "Movie", "Series", "Season", "Episode" } };
        var resolver = new MdblistResolver(new HttpClient(), string.Empty, NullLogger<MdblistResolver>.Instance);

        // mdblist cannot do Season/Episode, so opting in must not conjure them.
        RatingEnrichmentService.ProcessedLevels(resolver, config)
            .Should().Equal(ItemLevel.Movie, ItemLevel.Series);
    }

    [Fact]
    public void ProcessedLevels_OmitsCapableLevelsTheUserDidNotEnable()
    {
        var config = new PluginConfiguration { EnabledLevels = new[] { "Movie", "Series" } };

        RatingEnrichmentService.ProcessedLevels(AllFourLevels(), config)
            .Should().Equal(ItemLevel.Movie, ItemLevel.Series);
    }

    [Fact]
    public void ProcessedLevels_IncludesEpisode_WhenBothAgree()
    {
        var config = new PluginConfiguration { EnabledLevels = new[] { "Movie", "Series", "Episode" } };

        RatingEnrichmentService.ProcessedLevels(AllFourLevels(), config)
            .Should().Equal(ItemLevel.Movie, ItemLevel.Series, ItemLevel.Episode);
    }

    [Fact]
    public void ProcessedLevels_CanBeEmpty()
    {
        var config = new PluginConfiguration { EnabledLevels = Array.Empty<string>() };

        RatingEnrichmentService.ProcessedLevels(AllFourLevels(), config).Should().BeEmpty();
    }
}
