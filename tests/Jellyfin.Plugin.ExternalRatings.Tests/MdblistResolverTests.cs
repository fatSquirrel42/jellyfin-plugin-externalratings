using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Jellyfin.Plugin.ExternalRatings.Core;
using Jellyfin.Plugin.ExternalRatings.Resolvers;
using Jellyfin.Plugin.ExternalRatings.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ExternalRatings.Tests;

public class MdblistResolverTests
{
    private const string ApiKey = "SECRET";

    private static string Golden(string name)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Golden", name));

    private static MdblistResolver Build(FakeHttpMessageHandler handler)
    {
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.mdblist.com/") };
        return new MdblistResolver(client, ApiKey, NullLogger<MdblistResolver>.Instance);
    }

    private static RatingRequest Request(ItemLevel level, string provider, string id)
        => new(level, provider, id, "myanimelist");

    private static RatingRequest Request(ItemLevel level, string provider, string id, string source)
        => new(level, provider, id, source);

    [Fact]
    public async Task ResolveAsync_MovieFound_ReturnsNormalizedScore()
    {
        var handler = FakeHttpMessageHandler.Json(HttpStatusCode.OK, Golden("movie_found.json"));

        var result = await Build(handler).ResolveAsync(Request(ItemLevel.Movie, "Tmdb", "129"), CancellationToken.None);

        result.Resolution.Should().Be(RatingResolution.Found);
        result.Score.Should().BeApproximately(8.7f, 0.001f); // MAL score 87 normalized to 0–10
    }

    [Fact]
    public async Task ResolveAsync_ZeroToHundredSource_IsNormalizedToTen()
    {
        var handler = FakeHttpMessageHandler.Json(HttpStatusCode.OK, Golden("movie_found.json"));

        var result = await Build(handler).ResolveAsync(
            Request(ItemLevel.Movie, "Tmdb", "129", "metacritic"), CancellationToken.None);

        result.Resolution.Should().Be(RatingResolution.Found);
        result.Score.Should().BeApproximately(9.6f, 0.001f); // metacritic score 96 → 9.6
    }

    [Fact]
    public async Task ResolveAsync_ZeroToFiveSource_IsNormalizedToTen()
    {
        var handler = FakeHttpMessageHandler.Json(HttpStatusCode.OK, Golden("movie_found.json"));

        var result = await Build(handler).ResolveAsync(
            Request(ItemLevel.Movie, "Tmdb", "129", "letterboxd"), CancellationToken.None);

        result.Resolution.Should().Be(RatingResolution.Found);
        result.Score.Should().BeApproximately(8.8f, 0.001f); // letterboxd score 88 → 8.8
    }

    [Fact]
    public async Task ResolveAsync_SourceWithNullScore_IsNoMatch()
    {
        // rogerebert has a native value but no unified score in the fixture.
        var handler = FakeHttpMessageHandler.Json(HttpStatusCode.OK, Golden("movie_found.json"));

        var result = await Build(handler).ResolveAsync(
            Request(ItemLevel.Movie, "Tmdb", "129", "rogerebert"), CancellationToken.None);

        result.Resolution.Should().Be(RatingResolution.NoMatch);
    }

    [Fact]
    public async Task ResolveAsync_UnknownSource_IsNoMatch()
    {
        var handler = FakeHttpMessageHandler.Json(HttpStatusCode.OK, Golden("movie_found.json"));

        var result = await Build(handler).ResolveAsync(
            Request(ItemLevel.Movie, "Tmdb", "129", "nonexistent"), CancellationToken.None);

        result.Resolution.Should().Be(RatingResolution.NoMatch);
    }

    [Fact]
    public async Task ResolveAsync_BuildsProviderTypeIdUrlWithKey()
    {
        var handler = FakeHttpMessageHandler.Json(HttpStatusCode.OK, Golden("movie_found.json"));

        await Build(handler).ResolveAsync(Request(ItemLevel.Movie, "Tmdb", "129"), CancellationToken.None);

        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Method.Should().Be(HttpMethod.Get);
        handler.Requests[0].Uri.PathAndQuery.Should().Be("/tmdb/movie/129?apikey=SECRET");
    }

    [Fact]
    public async Task ResolveAsync_SeriesFound_MapsToShow()
    {
        var handler = FakeHttpMessageHandler.Json(HttpStatusCode.OK, Golden("show_found.json"));

        var result = await Build(handler).ResolveAsync(Request(ItemLevel.Series, "Imdb", "tt0213338"), CancellationToken.None);

        result.Resolution.Should().Be(RatingResolution.Found);
        result.Score.Should().BeApproximately(8.7f, 0.001f);
        handler.Requests[0].Uri.PathAndQuery.Should().Be("/imdb/show/tt0213338?apikey=SECRET");
    }

    [Fact]
    public async Task ResolveAsync_MalEntryWithNullValue_IsNoMatch()
    {
        var handler = FakeHttpMessageHandler.Json(HttpStatusCode.OK, Golden("no_match.json"));

        var result = await Build(handler).ResolveAsync(Request(ItemLevel.Movie, "Imdb", "tt0073195"), CancellationToken.None);

        result.Resolution.Should().Be(RatingResolution.NoMatch);
    }

    [Fact]
    public async Task ResolveAsync_NotFound_IsNoMatch()
    {
        var handler = FakeHttpMessageHandler.Json(HttpStatusCode.NotFound, "{\"error\":\"not found\"}");

        var result = await Build(handler).ResolveAsync(Request(ItemLevel.Movie, "Tmdb", "999999"), CancellationToken.None);

        result.Resolution.Should().Be(RatingResolution.NoMatch);
    }

    [Fact]
    public async Task ResolveAsync_RateLimited_IsError()
    {
        var handler = FakeHttpMessageHandler.Json((HttpStatusCode)429, "{\"error\":\"Daily API limit exceeded!\"}");

        var result = await Build(handler).ResolveAsync(Request(ItemLevel.Movie, "Tmdb", "129"), CancellationToken.None);

        result.Resolution.Should().Be(RatingResolution.Error);
    }

    [Fact]
    public async Task ResolveAsync_ServerError_IsError()
    {
        var handler = FakeHttpMessageHandler.Json(HttpStatusCode.InternalServerError, "boom");

        var result = await Build(handler).ResolveAsync(Request(ItemLevel.Movie, "Tmdb", "129"), CancellationToken.None);

        result.Resolution.Should().Be(RatingResolution.Error);
    }

    [Fact]
    public async Task ResolveAsync_NetworkException_IsError()
    {
        var handler = FakeHttpMessageHandler.Throws(new HttpRequestException("network down"));

        var result = await Build(handler).ResolveAsync(Request(ItemLevel.Movie, "Tmdb", "129"), CancellationToken.None);

        result.Resolution.Should().Be(RatingResolution.Error);
    }

    [Fact]
    public async Task ResolveAsync_CallerCancellation_Propagates_NotError()
    {
        // Caller cancellation must propagate (so the pipeline can rethrow and release the breaker's
        // half-open probe), not be swallowed into an Error result the way a transport fault/timeout is.
        var handler = FakeHttpMessageHandler.Json(HttpStatusCode.OK, Golden("movie_found.json"));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = async () => await Build(handler).ResolveAsync(Request(ItemLevel.Movie, "Tmdb", "129"), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ResolveAsync_MalformedJson_IsError()
    {
        var handler = FakeHttpMessageHandler.Json(HttpStatusCode.OK, "{ \"ratings\": [ ");

        var result = await Build(handler).ResolveAsync(Request(ItemLevel.Movie, "Tmdb", "129"), CancellationToken.None);

        result.Resolution.Should().Be(RatingResolution.Error);
    }

    [Fact]
    public async Task ResolveAsync_OutOfRangeScore_ViolatesH14_IsError()
    {
        var body = "{\"type\":\"movie\",\"ids\":{\"tmdb\":129},\"ratings\":[{\"source\":\"myanimelist\",\"value\":42.0,\"score\":150.0}]}";
        var handler = FakeHttpMessageHandler.Json(HttpStatusCode.OK, body);

        var result = await Build(handler).ResolveAsync(Request(ItemLevel.Movie, "Tmdb", "129"), CancellationToken.None);

        result.Resolution.Should().Be(RatingResolution.Error);
    }

    [Fact]
    public async Task ResolveAsync_UnsupportedLevel_IsNotSupported_AndMakesNoRequest()
    {
        var handler = FakeHttpMessageHandler.Json(HttpStatusCode.OK, "{}");

        var result = await Build(handler).ResolveAsync(Request(ItemLevel.Season, "Tmdb", "1"), CancellationToken.None);

        result.Resolution.Should().Be(RatingResolution.NotSupportedForLevel);
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveBatchAsync_MapsFoundAndNoMatchById()
    {
        var handler = FakeHttpMessageHandler.Json(HttpStatusCode.OK, Golden("batch.json"));

        var results = await Build(handler).ResolveBatchAsync(
            ItemLevel.Movie, "Tmdb", new[] { "129", "578" }, "myanimelist", CancellationToken.None);

        results["129"].Resolution.Should().Be(RatingResolution.Found);
        results["129"].Score.Should().BeApproximately(8.7f, 0.001f);
        results["578"].Resolution.Should().Be(RatingResolution.NoMatch);

        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Method.Should().Be(HttpMethod.Post);
        handler.Requests[0].Uri.PathAndQuery.Should().Be("/tmdb/movie?apikey=SECRET");
        handler.Requests[0].Body.Should().Contain("\"ids\"").And.Contain("129").And.Contain("578");
    }

    [Fact]
    public async Task ResolveBatchAsync_ChunkError_MarksWholeChunkError()
    {
        var handler = FakeHttpMessageHandler.Json(HttpStatusCode.InternalServerError, "boom");

        var results = await Build(handler).ResolveBatchAsync(
            ItemLevel.Movie, "Tmdb", new[] { "129", "578" }, "myanimelist", CancellationToken.None);

        results["129"].Resolution.Should().Be(RatingResolution.Error);
        results["578"].Resolution.Should().Be(RatingResolution.Error);
    }

    [Fact]
    public async Task ResolveBatchAsync_MoreThanMaxBatchSize_SplitsIntoChunks()
    {
        var handler = FakeHttpMessageHandler.Json(HttpStatusCode.OK, "[]");
        var ids = new string[201];
        for (var i = 0; i < ids.Length; i++)
        {
            ids[i] = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        var results = await Build(handler).ResolveBatchAsync(
            ItemLevel.Movie, "Tmdb", ids, "myanimelist", CancellationToken.None);

        handler.Requests.Should().HaveCount(2); // 200 + 1
        results.Should().HaveCount(201);
        results.Values.Should().OnlyContain(r => r.Resolution == RatingResolution.NoMatch);
    }

    [Fact]
    public async Task ResolveBatchAsync_UnsupportedLevel_AllNotSupported_NoRequest()
    {
        var handler = FakeHttpMessageHandler.Json(HttpStatusCode.OK, "[]");

        var results = await Build(handler).ResolveBatchAsync(
            ItemLevel.Episode, "Tmdb", new[] { "1", "2" }, "myanimelist", CancellationToken.None);

        results.Values.Should().OnlyContain(r => r.Resolution == RatingResolution.NotSupportedForLevel);
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task GetUsedRequestCountAsync_ParsesApiRequestsCountFromBody()
    {
        var handler = FakeHttpMessageHandler.Json(HttpStatusCode.OK, Golden("user.json"));

        var used = await Build(handler).GetUsedRequestCountAsync(CancellationToken.None);

        used.Should().Be(42);
        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Method.Should().Be(HttpMethod.Get);
        handler.Requests[0].Uri.PathAndQuery.Should().Be("/user?apikey=SECRET");
    }

    [Fact]
    public async Task GetUsedRequestCountAsync_NonSuccess_ReturnsNull()
    {
        var handler = FakeHttpMessageHandler.Json(HttpStatusCode.Unauthorized, "{\"error\":\"unauthorized\"}");

        var used = await Build(handler).GetUsedRequestCountAsync(CancellationToken.None);

        used.Should().BeNull();
    }

    [Fact]
    public async Task GetUsedRequestCountAsync_TransportError_ReturnsNull()
    {
        var handler = FakeHttpMessageHandler.Throws(new HttpRequestException("boom"));

        var used = await Build(handler).GetUsedRequestCountAsync(CancellationToken.None);

        used.Should().BeNull();
    }
}
