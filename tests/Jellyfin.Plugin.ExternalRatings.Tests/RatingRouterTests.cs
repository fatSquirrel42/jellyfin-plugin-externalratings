using System;
using System.Collections.Generic;
using FluentAssertions;
using Jellyfin.Plugin.ExternalRatings.Core;
using Jellyfin.Plugin.ExternalRatings.Core.Abstractions;
using Jellyfin.Plugin.ExternalRatings.Resolvers;
using Jellyfin.Plugin.ExternalRatings.Tests.Fakes;
using Xunit;

namespace Jellyfin.Plugin.ExternalRatings.Tests;

/// <summary>
/// The router is what replaced the user-facing provider choice: the caller picks a rating source,
/// and the route follows from (source × level × available ids).
/// </summary>
public class RatingRouterTests
{
    private static StubRatingResolver Resolver(
        string key,
        IReadOnlyList<string> sources,
        params (ItemLevel Level, string[] Providers)[] levels)
    {
        var providers = new Dictionary<ItemLevel, IReadOnlyList<string>>();
        foreach (var (level, keys) in levels)
        {
            providers[level] = keys;
        }

        var sourceInfos = new List<RatingSourceInfo>();
        foreach (var source in sources)
        {
            sourceInfos.Add(new RatingSourceInfo(source, source, "0–10", false));
        }

        return new StubRatingResolver
        {
            Key = key,
            SupportedInputProviders = providers,
            SupportedRatingSources = sourceInfos
        };
    }

    /// <summary>The offline IMDb dataset: imdb only, every level, Imdb ids only.</summary>
    private static StubRatingResolver Dataset() => Resolver(
        "imdb-dataset",
        new[] { "imdb" },
        (ItemLevel.Movie, new[] { "Imdb" }),
        (ItemLevel.Series, new[] { "Imdb" }),
        (ItemLevel.Season, new[] { "Imdb" }),
        (ItemLevel.Episode, new[] { "Imdb" }));

    /// <summary>mdblist: nine sources, Movie and Series only, Tmdb→Imdb→Tvdb.</summary>
    private static StubRatingResolver Mdblist() => Resolver(
        "mdblist",
        new[] { "imdb", "myanimelist", "trakt" },
        (ItemLevel.Movie, new[] { "Tmdb", "Imdb" }),
        (ItemLevel.Series, new[] { "Tmdb", "Imdb", "Tvdb" }));

    private static RatingWorkItem Item(
        ItemLevel level,
        string source,
        params (string Provider, string Id)[] ids)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (provider, id) in ids)
        {
            dict[provider] = id;
        }

        return new RatingWorkItem(new RatingItemRef(Guid.NewGuid(), "item"), level, dict, source);
    }

    [Fact]
    public void RoutesToTheOnlyCandidateThatServesTheSource()
    {
        var router = new RatingRouter(new IRatingResolver[] { Dataset(), Mdblist() });

        var route = router.Route(Item(ItemLevel.Series, "myanimelist", ("Tvdb", "413188")));

        route.Resolver!.Key.Should().Be("mdblist");
        route.Selection!.Value.Provider.Should().Be("Tvdb");
    }

    [Fact]
    public void PrefersTheFirstCandidateWhenBothCouldServe()
    {
        // imdb is served by both; the dataset comes first because it costs no key and no quota.
        var router = new RatingRouter(new IRatingResolver[] { Dataset(), Mdblist() });

        var route = router.Route(Item(ItemLevel.Movie, "imdb", ("Tmdb", "129"), ("Imdb", "tt0245429")));

        route.Resolver!.Key.Should().Be("imdb-dataset");
        route.Selection!.Value.Provider.Should().Be("Imdb");
    }

    [Fact]
    public void CandidateOrderDecides_NotTheRichestIdSet()
    {
        // Reversing the list must reverse the outcome, so the preference really is the order.
        var router = new RatingRouter(new IRatingResolver[] { Mdblist(), Dataset() });

        var route = router.Route(Item(ItemLevel.Movie, "imdb", ("Tmdb", "129"), ("Imdb", "tt0245429")));

        route.Resolver!.Key.Should().Be("mdblist");
        route.Selection!.Value.Provider.Should().Be("Tmdb");
    }

    [Fact]
    public void FallsThroughWhenThePreferredCandidateHasNoUsableId()
    {
        // The decisive case for the imdb fallback: no IMDb id, but mdblist can reach the IMDb score
        // through the Tmdb id.
        var router = new RatingRouter(new IRatingResolver[] { Dataset(), Mdblist() });

        var route = router.Route(Item(ItemLevel.Movie, "imdb", ("Tmdb", "129")));

        route.Resolver!.Key.Should().Be("mdblist");
        route.Selection!.Value.Provider.Should().Be("Tmdb");
        route.LevelServableBySource.Should().BeTrue();
    }

    [Fact]
    public void NoCandidateServesTheSource_LevelIsNotServable()
    {
        var router = new RatingRouter(new IRatingResolver[] { Dataset(), Mdblist() });

        var route = router.Route(Item(ItemLevel.Movie, "letterboxd", ("Imdb", "tt0245429")));

        route.Resolver.Should().BeNull();
        route.Selection.Should().BeNull();
        route.LevelServableBySource.Should().BeFalse();
    }

    [Fact]
    public void SourceServedButNotAtThisLevel_LevelIsNotServable()
    {
        // The case that drives the ClearField decision: an episode in a library set to MyAnimeList.
        var router = new RatingRouter(new IRatingResolver[] { Dataset(), Mdblist() });

        var route = router.Route(Item(ItemLevel.Episode, "myanimelist", ("Imdb", "tt2301451")));

        route.Resolver.Should().BeNull();
        route.LevelServableBySource.Should().BeFalse();
    }

    [Fact]
    public void SourceAndLevelServedButNoId_LevelStaysServable()
    {
        // Distinguishes "no usable id" (SkippedNoId) from "the source cannot do this level"
        // (NotSupported). Both end up at the same ClearField decision but count differently.
        var router = new RatingRouter(new IRatingResolver[] { Dataset(), Mdblist() });

        var route = router.Route(Item(ItemLevel.Episode, "imdb", ("Tvdb", "8951947")));

        route.Resolver.Should().BeNull();
        route.Selection.Should().BeNull();
        route.LevelServableBySource.Should().BeTrue();
    }

    [Fact]
    public void SeasonOnlyRoutesToTheDataset()
    {
        var router = new RatingRouter(new IRatingResolver[] { Dataset(), Mdblist() });

        router.Route(Item(ItemLevel.Season, "imdb", ("Imdb", "tt0903747/S1#8")))
            .Resolver!.Key.Should().Be("imdb-dataset");

        router.Route(Item(ItemLevel.Season, "trakt", ("Imdb", "tt0903747/S1#8")))
            .Resolver.Should().BeNull();
    }

    [Fact]
    public void WithoutMdblistAmongTheCandidates_ItsSourcesAreUnreachable()
    {
        // This is how a missing API key manifests: the facade simply leaves mdblist out.
        var router = new RatingRouter(new IRatingResolver[] { Dataset() });

        router.Route(Item(ItemLevel.Series, "myanimelist", ("Tvdb", "413188")))
            .Resolver.Should().BeNull();
        router.Route(Item(ItemLevel.Series, "imdb", ("Imdb", "tt0903747")))
            .Resolver!.Key.Should().Be("imdb-dataset");
    }

    [Fact]
    public void WithoutMdblist_AnImdbItemLackingAnImdbIdHasNoRoute()
    {
        var router = new RatingRouter(new IRatingResolver[] { Dataset() });

        var route = router.Route(Item(ItemLevel.Movie, "imdb", ("Tmdb", "129")));

        route.Resolver.Should().BeNull();
        route.LevelServableBySource.Should().BeTrue("the dataset serves imdb at Movie level, the id is what is missing");
    }

    [Fact]
    public void NoCandidatesAtAll_RoutesNothing()
    {
        var router = new RatingRouter(Array.Empty<IRatingResolver>());

        var route = router.Route(Item(ItemLevel.Movie, "imdb", ("Imdb", "tt0245429")));

        route.Resolver.Should().BeNull();
        route.LevelServableBySource.Should().BeFalse();
    }

    [Fact]
    public void SourceMatchingIsCaseInsensitive()
    {
        var router = new RatingRouter(new IRatingResolver[] { Dataset() });

        router.Route(Item(ItemLevel.Movie, "IMDb", ("Imdb", "tt0245429")))
            .Resolver!.Key.Should().Be("imdb-dataset");
    }

    [Fact]
    public void ItemWithNoIdsAtAll_HasNoRoute()
    {
        var router = new RatingRouter(new IRatingResolver[] { Dataset(), Mdblist() });

        var route = router.Route(Item(ItemLevel.Movie, "imdb"));

        route.Resolver.Should().BeNull();
        route.LevelServableBySource.Should().BeTrue();
    }

    [Fact]
    public void ServableLevelsForASource_AreTheUnionAcrossCandidates()
    {
        // Feeds the config page's per-source hint.
        var router = new RatingRouter(new IRatingResolver[] { Dataset(), Mdblist() });

        router.ServableLevels("imdb").Should().Equal(
            ItemLevel.Movie, ItemLevel.Series, ItemLevel.Season, ItemLevel.Episode);
        router.ServableLevels("myanimelist").Should().Equal(ItemLevel.Movie, ItemLevel.Series);
        router.ServableLevels("letterboxd").Should().BeEmpty();
    }
}
