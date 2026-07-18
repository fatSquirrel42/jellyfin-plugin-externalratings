using System;
using FluentAssertions;
using Jellyfin.Plugin.ExternalRatings;
using Jellyfin.Plugin.ExternalRatings.Core;
using Jellyfin.Data.Enums;
using Xunit;

namespace Jellyfin.Plugin.ExternalRatings.Tests;

public class RatingEnrichmentServiceTests
{
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
