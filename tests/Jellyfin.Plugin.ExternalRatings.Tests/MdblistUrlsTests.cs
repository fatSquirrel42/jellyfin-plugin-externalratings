using FluentAssertions;
using Jellyfin.Plugin.ExternalRatings.Core;
using Jellyfin.Plugin.ExternalRatings.Resolvers.Mdblist;
using Xunit;

namespace Jellyfin.Plugin.ExternalRatings.Tests;

public class MdblistUrlsTests
{
    [Fact]
    public void MapProvider_LowercasesJellyfinName()
    {
        MdblistUrls.MapProvider("Tmdb").Should().Be("tmdb");
        MdblistUrls.MapProvider("Imdb").Should().Be("imdb");
        MdblistUrls.MapProvider("Tvdb").Should().Be("tvdb");
    }

    [Fact]
    public void MapType_Movie_IsMovie()
    {
        MdblistUrls.MapType(ItemLevel.Movie).Should().Be("movie");
    }

    [Fact]
    public void MapType_Series_IsShow()
    {
        MdblistUrls.MapType(ItemLevel.Series).Should().Be("show");
    }

    [Fact]
    public void MapType_UnsupportedLevel_IsNull()
    {
        MdblistUrls.MapType(ItemLevel.Season).Should().BeNull();
        MdblistUrls.MapType(ItemLevel.Episode).Should().BeNull();
    }

    [Fact]
    public void BuildSingle_ProducesProviderTypeIdWithKey()
    {
        MdblistUrls.BuildSingle("imdb", "movie", "tt0245429", "SECRET")
            .Should().Be("imdb/movie/tt0245429?apikey=SECRET");
    }

    [Fact]
    public void BuildBatch_ProducesProviderTypeWithKey()
    {
        MdblistUrls.BuildBatch("tmdb", "show", "SECRET")
            .Should().Be("tmdb/show?apikey=SECRET");
    }

    [Fact]
    public void MaskApiKey_ReplacesKeyValue()
    {
        MdblistUrls.MaskApiKey("imdb/movie/tt0245429?apikey=SECRET")
            .Should().Be("imdb/movie/tt0245429?apikey=***");
    }

    [Fact]
    public void MaskApiKey_KeepsOtherQueryParameters()
    {
        MdblistUrls.MaskApiKey("tmdb/movie/129?apikey=SECRET&append_to_response=review")
            .Should().Be("tmdb/movie/129?apikey=***&append_to_response=review");
    }

    [Fact]
    public void MaskApiKey_IsCaseInsensitiveOnParamName()
    {
        MdblistUrls.MaskApiKey("x?ApiKey=SECRET").Should().Be("x?ApiKey=***");
    }

    [Fact]
    public void MaskApiKey_WithoutKey_ReturnsUnchanged()
    {
        MdblistUrls.MaskApiKey("user").Should().Be("user");
    }
}
