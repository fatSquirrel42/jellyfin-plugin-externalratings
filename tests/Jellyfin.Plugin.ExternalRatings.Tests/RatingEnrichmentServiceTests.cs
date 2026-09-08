using System;
using System.Net.Http;
using FluentAssertions;
using Jellyfin.Plugin.ExternalRatings;
using Jellyfin.Plugin.ExternalRatings.Core;
using Jellyfin.Plugin.ExternalRatings.Resolvers;
using Jellyfin.Plugin.ExternalRatings.Tests.Fakes;
using Jellyfin.Data.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ExternalRatings.Tests;

public class RatingEnrichmentServiceTests
{
    [Fact]
    public void SupportedLevels_MdblistResolver_IsMovieAndSeriesOnly()
    {
        // The processed levels are driven by the resolver's capability flag (SupportedInputProviders),
        // not by config. mdblist supports only Movie and Series (no season/episode scores).
        var resolver = new MdblistResolver(new HttpClient(), string.Empty, NullLogger<MdblistResolver>.Instance);

        RatingEnrichmentService.SupportedLevels(resolver).Should().Equal(ItemLevel.Movie, ItemLevel.Series);
    }

    [Fact]
    public void SupportedLevels_ImdbDatasetResolver_CoversAllFour()
    {
        // The dataset has a rating row per episode, so Episode is a direct lookup; Season has no
        // row of its own and is aggregated from those episodes.
        using var datasets = ImdbTestDatasets.Create();
        var resolver = new ImdbDatasetResolver(
            datasets.Ratings, datasets.Episodes, NullLogger.Instance);

        RatingEnrichmentService.SupportedLevels(resolver)
            .Should().Equal(ItemLevel.Movie, ItemLevel.Series, ItemLevel.Season, ItemLevel.Episode);
    }

    [Fact]
    public void BuildLibraryQuery_IncludesEpisodeKind_WhenTheResolverSupportsIt()
    {
        var query = RatingEnrichmentService.BuildLibraryQuery(
            new[] { ItemLevel.Movie, ItemLevel.Series, ItemLevel.Episode }, Array.Empty<Guid>());

        query.IncludeItemTypes.Should().Contain(BaseItemKind.Episode);
    }

    [Fact]
    public void BuildLibraryQuery_ExcludesVirtualItems()
    {
        // Missing-episode placeholders are virtual items. Enumerating them would resolve nothing and,
        // under the shipped ClearField default, clear a field on an item that has no file at all.
        var query = RatingEnrichmentService.BuildLibraryQuery(new[] { ItemLevel.Episode }, Array.Empty<Guid>());

        query.IsVirtualItem.Should().BeFalse();
    }

    [Fact]
    public void BuildLibraryQuery_FiltersByAncestorIds_NotTopParentIds()
    {
        // Regression: the config stores CollectionFolder ids (getVirtualFolders().ItemId). An item's
        // TopParentId is the underlying physical folder, so filtering by TopParentIds matches nothing;
        // the CollectionFolder is an ancestor, so AncestorIds is the correct filter.
        var libraries = new[] { Guid.NewGuid(), Guid.NewGuid() };

        var query = RatingEnrichmentService.BuildLibraryQuery(new[] { ItemLevel.Movie, ItemLevel.Series }, libraries);

        query.AncestorIds.Should().Equal(libraries);
        query.TopParentIds.Should().BeEmpty();
        query.Recursive.Should().BeTrue();
        query.IncludeItemTypes.Should().Contain(BaseItemKind.Movie).And.Contain(BaseItemKind.Series);
    }

    [Fact]
    public void BuildLibraryQuery_MapsOnlySupportedLevelsToKinds()
    {
        var query = RatingEnrichmentService.BuildLibraryQuery(new[] { ItemLevel.Movie }, Array.Empty<Guid>());

        query.IncludeItemTypes.Should().ContainSingle().Which.Should().Be(BaseItemKind.Movie);
    }
}
