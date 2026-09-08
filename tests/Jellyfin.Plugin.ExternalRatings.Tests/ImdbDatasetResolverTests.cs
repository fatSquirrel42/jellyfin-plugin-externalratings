using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Jellyfin.Plugin.ExternalRatings.Core;
using Jellyfin.Plugin.ExternalRatings.Resolvers;
using Jellyfin.Plugin.ExternalRatings.Resolvers.Imdb;
using Jellyfin.Plugin.ExternalRatings.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ExternalRatings.Tests;

public sealed class ImdbDatasetResolverTests : IDisposable
{
    /// <summary>Breaking Bad's numeric tconst — the series both fake episodes hang off.</summary>
    private const long BreakingBad = 903747;

    private readonly ImdbTestDatasets _datasets;
    private readonly ImdbDatasetResolver _resolver;

    public ImdbDatasetResolverTests()
    {
        _datasets = ImdbTestDatasets.Create();
        _resolver = new ImdbDatasetResolver(_datasets.Ratings, _datasets.Episodes, NullLogger.Instance);
    }

    public void Dispose() => _datasets.Dispose();

    private static RatingRequest Request(ItemLevel level, string id, string source = "imdb")
        => new(level, "Imdb", id, source);

    [Fact]
    public void DeclaresAllFourLevels()
    {
        _resolver.SupportedInputProviders.Keys.Should().BeEquivalentTo(
            new[] { ItemLevel.Movie, ItemLevel.Series, ItemLevel.Season, ItemLevel.Episode });
    }

    [Fact]
    public void DeclaresImdbAsTheOnlyInputProvider()
    {
        foreach (var providers in _resolver.SupportedInputProviders.Values)
        {
            providers.Should().Equal("Imdb");
        }
    }

    [Fact]
    public void DeclaresImdbAsTheOnlyRatingSource()
    {
        _resolver.SupportedRatingSources.Should().ContainSingle()
            .Which.Key.Should().Be("imdb");
        _resolver.SupportedRatingSources.Should().OnlyContain(s => !s.Rescaled);
    }

    [Fact]
    public async Task ResolvesASeries()
    {
        var result = await _resolver.ResolveAsync(Request(ItemLevel.Series, "tt0903747"), CancellationToken.None);

        result.Resolution.Should().Be(RatingResolution.Found);
        result.Score.Should().Be(9.5f);
    }

    [Fact]
    public async Task ResolvesAnEpisodeByItsOwnId()
    {
        var result = await _resolver.ResolveAsync(Request(ItemLevel.Episode, "tt2301451"), CancellationToken.None);

        result.Resolution.Should().Be(RatingResolution.Found);
        result.Score.Should().Be(9.5f);
    }

    [Fact]
    public async Task ResolvesAMovie()
    {
        var result = await _resolver.ResolveAsync(Request(ItemLevel.Movie, "tt16364366"), CancellationToken.None);

        result.Resolution.Should().Be(RatingResolution.Found);
        result.Score.Should().Be(6.8f);
    }

    [Fact]
    public async Task UnknownIdIsNoMatch()
    {
        var result = await _resolver.ResolveAsync(Request(ItemLevel.Episode, "tt9999999"), CancellationToken.None);

        result.Resolution.Should().Be(RatingResolution.NoMatch);
    }

    // --- season: IMDb's season, identified by the episodes the host supplies ---
    //
    // The members only *identify* which IMDb season this is; the episodes averaged are the ones IMDb
    // lists for it. The fake episode map puts tt2301451 (9.5) and tt16364366 (6.8) in Breaking Bad
    // season 5, so a correctly identified season averages 8.15 -> 8.2 no matter how many of the two
    // the "library" holds.

    private static RatingRequest SeasonRequest(params string[] memberIds)
        => new(ItemLevel.Season, "Imdb", "tt0903747/S5#" + memberIds.Length, "imdb", memberIds);

    /// <summary>Does what the host does before a pass: tells the map which series to cover.</summary>
    private Task CoverBreakingBadAsync()
        => _datasets.Episodes.EnsureCoversAsync(new HashSet<long> { BreakingBad }, CancellationToken.None);

    [Fact]
    public async Task SeasonAveragesEveryEpisodeImdbListsForIt_NotOnlyTheOnesOnDisk()
    {
        // The point of the whole design: one episode on disk, and the score is still the season's.
        await CoverBreakingBadAsync();

        var result = await _resolver.ResolveAsync(SeasonRequest("tt2301451"), CancellationToken.None);

        result.Resolution.Should().Be(RatingResolution.Found);
        result.Score.Should().Be(8.2f);
    }

    [Fact]
    public async Task AMemberTheEpisodeMapDoesNotKnow_DoesNotBlockTheSeason()
    {
        // It used to: back when the season was the mean of our own hits, an unmatched episode meant
        // no score at all. Now an unresolvable member is simply not a witness.
        await CoverBreakingBadAsync();

        var result = await _resolver.ResolveAsync(
            SeasonRequest("tt2301451", "tt9999999"), CancellationToken.None);

        result.Resolution.Should().Be(RatingResolution.Found);
        result.Score.Should().Be(8.2f);
    }

    [Fact]
    public async Task ASeasonNoMemberCanIdentifyIsNoMatch()
    {
        // No witness resolves, so there is nothing to anchor on. A specials season lands here by
        // construction: IMDb has no season 0 rows at all.
        await CoverBreakingBadAsync();

        var result = await _resolver.ResolveAsync(SeasonRequest("tt9999999"), CancellationToken.None);

        result.Resolution.Should().Be(RatingResolution.NoMatch);
    }

    [Fact]
    public async Task ASeasonSpanningTwoImdbSeasonsIsRefused()
    {
        // The Futurama case: TheTVDB's season and IMDb's disagree, so the members land in two IMDb
        // seasons. Nothing is written rather than one of the two being guessed at.
        using var diverging = ImdbTestDatasets.Create(episodeRows:
            "tt2301451\ttt0903747\t5\t14\n" +
            "tt16364366\ttt0903747\t6\t1\n");
        await diverging.Episodes.EnsureCoversAsync(
            new HashSet<long> { BreakingBad }, CancellationToken.None);
        var resolver = new ImdbDatasetResolver(
            diverging.Ratings, diverging.Episodes, NullLogger.Instance);

        var result = await resolver.ResolveAsync(
            SeasonRequest("tt2301451", "tt16364366"), CancellationToken.None);

        result.Resolution.Should().Be(RatingResolution.NoMatch);
    }

    [Fact]
    public async Task SeasonWithNoMembersIsNoMatch()
    {
        // An empty season resolves to nothing rather than erroring: it is a legitimate library
        // state, not a fault.
        await CoverBreakingBadAsync();

        var result = await _resolver.ResolveAsync(SeasonRequest(), CancellationToken.None);

        result.Resolution.Should().Be(RatingResolution.NoMatch);
    }

    [Fact]
    public async Task SeasonIgnoresItsOwnInputId()
    {
        // The synthetic "tt0903747/S5#1" is a cache key, not something to look up — and the season
        // number in it is never translated to IMDb's. One member identifies season 5, and the score
        // is that season's mean (8.2), not the member's own 6.8.
        await CoverBreakingBadAsync();

        var result = await _resolver.ResolveAsync(SeasonRequest("tt16364366"), CancellationToken.None);

        result.Resolution.Should().Be(RatingResolution.Found);
        result.Score.Should().Be(8.2f);
    }

    [Fact]
    public async Task AnUnavailableEpisodeMapIsAnError_NotANoMatch()
    {
        // A failed 54-MB download must not clear every season rating in the library, which is what a
        // no-match would do under the ClearField default.
        using var offline = ImdbTestDatasets.Create(episodesOffline: true);
        var resolver = new ImdbDatasetResolver(offline.Ratings, offline.Episodes, NullLogger.Instance);

        var result = await resolver.ResolveAsync(SeasonRequest("tt2301451"), CancellationToken.None);

        result.Resolution.Should().Be(RatingResolution.Error);
        result.ErrorDetail.Should().Contain("episode");
    }

    [Fact]
    public async Task AForeignTargetSourceIsAnError_NotANoMatch()
    {
        // A no-match is cached and, under ClearField, would wipe the field across the library on a
        // config left over from mdblist. An error writes nothing and is not cached persistently.
        var result = await _resolver.ResolveAsync(
            Request(ItemLevel.Series, "tt0903747", "myanimelist"), CancellationToken.None);

        result.Resolution.Should().Be(RatingResolution.Error);
        result.ErrorDetail.Should().Contain("myanimelist");
    }

    [Fact]
    public async Task AForeignInputProviderIsAnError()
    {
        var result = await _resolver.ResolveAsync(
            new RatingRequest(ItemLevel.Series, "Tvdb", "413188", "imdb"), CancellationToken.None);

        result.Resolution.Should().Be(RatingResolution.Error);
        result.ErrorDetail.Should().Contain("Tvdb");
    }

    [Fact]
    public async Task TargetSourceMatchIsCaseInsensitive()
    {
        var result = await _resolver.ResolveAsync(
            Request(ItemLevel.Series, "tt0903747", "IMDb"), CancellationToken.None);

        result.Resolution.Should().Be(RatingResolution.Found);
    }

    [Fact]
    public async Task AnUnavailableRatingsDatasetIsAnError_NotANoMatch()
    {
        // Same reasoning as the foreign-source case: an empty index must never clear the library.
        var dir = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "extratings-imdbres-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            using var ratings = new ImdbRatingsDataset(
                new System.Net.Http.HttpClient(
                    FakeHttpMessageHandler.Throws(new System.Net.Http.HttpRequestException("offline"))),
                dir,
                () => TimeSpan.FromHours(24),
                new FakeClock(),
                NullLogger.Instance);
            var resolver = new ImdbDatasetResolver(ratings, _datasets.Episodes, NullLogger.Instance);

            var result = await resolver.ResolveAsync(Request(ItemLevel.Series, "tt0903747"), CancellationToken.None);

            result.Resolution.Should().Be(RatingResolution.Error);
            result.ErrorDetail.Should().Contain("dataset");
        }
        finally
        {
            System.IO.Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void IsNotABatchResolver()
    {
        // Batching amortises HTTP calls; a local binary search has nothing to amortise, and the
        // prefetch phase must therefore be skipped rather than run pointlessly.
        _resolver.Should().NotBeAssignableTo<IBatchRatingResolver>();
    }
}
