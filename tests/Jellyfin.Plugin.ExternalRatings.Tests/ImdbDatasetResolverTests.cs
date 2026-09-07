using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
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
    private readonly string _dir;
    private readonly ImdbRatingsDataset _dataset;
    private readonly ImdbDatasetResolver _resolver;

    public ImdbDatasetResolverTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "extratings-imdbres-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);

        // Real rows: Breaking Bad the series, and "Ozymandias" the episode.
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Gzip(
                    "tconst\taverageRating\tnumVotes\n" +
                    "tt0903747\t9.5\t2671907\n" +
                    "tt2301451\t9.5\t507558\n" +
                    "tt16364366\t6.8\t3217\n"))
            }));

        _dataset = new ImdbRatingsDataset(
            new HttpClient(handler), _dir, () => TimeSpan.FromHours(24), new FakeClock(), NullLogger.Instance);
        _resolver = new ImdbDatasetResolver(_dataset);
    }

    public void Dispose()
    {
        _dataset.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Ignore a leaked temp dir.
        }
    }

    private static byte[] Gzip(string tsv)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            var bytes = Encoding.UTF8.GetBytes(tsv);
            gzip.Write(bytes, 0, bytes.Length);
        }

        return output.ToArray();
    }

    private static RatingRequest Request(ItemLevel level, string id, string source = "imdb")
        => new(level, "Imdb", id, source);

    [Fact]
    public void DeclaresMovieSeriesAndEpisode_ButNotSeason()
    {
        // Season is an aggregate over episodes, produced outside this per-item seam.
        _resolver.SupportedInputProviders.Keys.Should().BeEquivalentTo(
            new[] { ItemLevel.Movie, ItemLevel.Series, ItemLevel.Episode });
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

    [Fact]
    public async Task SeasonIsNotSupportedForLevel()
    {
        var result = await _resolver.ResolveAsync(Request(ItemLevel.Season, "tt0903747"), CancellationToken.None);

        result.Resolution.Should().Be(RatingResolution.NotSupportedForLevel);
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
    public async Task AnUnavailableDatasetIsAnError_NotANoMatch()
    {
        // Same reasoning as the foreign-source case: an empty index must never clear the library.
        var dir = Path.Combine(Path.GetTempPath(), "extratings-imdbres-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var dataset = new ImdbRatingsDataset(
                new HttpClient(FakeHttpMessageHandler.Throws(new HttpRequestException("offline"))),
                dir,
                () => TimeSpan.FromHours(24),
                new FakeClock(),
                NullLogger.Instance);
            var resolver = new ImdbDatasetResolver(dataset);

            var result = await resolver.ResolveAsync(Request(ItemLevel.Series, "tt0903747"), CancellationToken.None);

            result.Resolution.Should().Be(RatingResolution.Error);
            result.ErrorDetail.Should().Contain("dataset");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
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
