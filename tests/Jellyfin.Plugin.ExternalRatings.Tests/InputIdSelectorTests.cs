using System.Collections.Generic;
using FluentAssertions;
using Jellyfin.Plugin.ExternalRatings.Core;
using Xunit;

namespace Jellyfin.Plugin.ExternalRatings.Tests;

public class InputIdSelectorTests
{
    private static Dictionary<string, string> Ids(params (string Provider, string Id)[] pairs)
    {
        var dict = new Dictionary<string, string>();
        foreach (var (provider, id) in pairs)
        {
            dict[provider] = id;
        }

        return dict;
    }

    [Fact]
    public void Movie_PrefersTmdb_WhenAllPresent()
    {
        var result = InputIdSelector.Select(ItemLevel.Movie, Ids(("Tmdb", "111"), ("Imdb", "tt222")));

        result.Should().NotBeNull();
        result!.Value.Provider.Should().Be("Tmdb");
        result.Value.Id.Should().Be("111");
    }

    [Fact]
    public void Movie_FallsBackToImdb_WhenNoTmdb()
    {
        var result = InputIdSelector.Select(ItemLevel.Movie, Ids(("Imdb", "tt222")));

        result.Should().NotBeNull();
        result!.Value.Provider.Should().Be("Imdb");
        result.Value.Id.Should().Be("tt222");
    }

    [Fact]
    public void Movie_ReturnsNull_WhenOnlyTvdbPresent()
    {
        // Tvdb is not an allowed input provider for movies.
        var result = InputIdSelector.Select(ItemLevel.Movie, Ids(("Tvdb", "333")));

        result.Should().BeNull();
    }

    [Fact]
    public void Series_PrefersTmdb_WhenAllPresent()
    {
        var result = InputIdSelector.Select(ItemLevel.Series, Ids(("Tvdb", "333"), ("Imdb", "tt222"), ("Tmdb", "111")));

        result.Should().NotBeNull();
        result!.Value.Provider.Should().Be("Tmdb");
    }

    [Fact]
    public void Series_FallsBackToImdb_WhenNoTmdb()
    {
        var result = InputIdSelector.Select(ItemLevel.Series, Ids(("Tvdb", "333"), ("Imdb", "tt222")));

        result.Should().NotBeNull();
        result!.Value.Provider.Should().Be("Imdb");
    }

    [Fact]
    public void Series_UsesTvdb_WhenOnlyTvdbPresent()
    {
        var result = InputIdSelector.Select(ItemLevel.Series, Ids(("Tvdb", "333")));

        result.Should().NotBeNull();
        result!.Value.Provider.Should().Be("Tvdb");
        result.Value.Id.Should().Be("333");
    }

    [Fact]
    public void ReturnsNull_WhenNoIds()
    {
        var result = InputIdSelector.Select(ItemLevel.Series, Ids());

        result.Should().BeNull();
    }

    [Fact]
    public void ReturnsNull_WhenAllIdsBlank()
    {
        var result = InputIdSelector.Select(ItemLevel.Series, Ids(("Tmdb", ""), ("Imdb", "   ")));

        result.Should().BeNull();
    }

    [Fact]
    public void HasUnusedAlternatives_True_WhenMoreThanOneAllowedIdPresent()
    {
        var result = InputIdSelector.Select(ItemLevel.Series, Ids(("Tmdb", "111"), ("Tvdb", "333")));

        result.Should().NotBeNull();
        result!.Value.HasUnusedAlternatives.Should().BeTrue();
    }

    [Fact]
    public void HasUnusedAlternatives_False_WhenOnlyChosenPresent()
    {
        var result = InputIdSelector.Select(ItemLevel.Series, Ids(("Tmdb", "111")));

        result.Should().NotBeNull();
        result!.Value.HasUnusedAlternatives.Should().BeFalse();
    }

    [Fact]
    public void HasUnusedAlternatives_IgnoresProvidersNotAllowedForLevel()
    {
        // Only Imdb present for a movie; Tvdb is not counted (not allowed for movies).
        var result = InputIdSelector.Select(ItemLevel.Movie, Ids(("Imdb", "tt222"), ("Tvdb", "333")));

        result.Should().NotBeNull();
        result!.Value.Provider.Should().Be("Imdb");
        result.Value.HasUnusedAlternatives.Should().BeFalse();
    }

    [Fact]
    public void ProviderKeys_AreCaseInsensitive()
    {
        var result = InputIdSelector.Select(ItemLevel.Movie, Ids(("tmdb", "111")));

        result.Should().NotBeNull();
        result!.Value.Provider.Should().Be("Tmdb");
        result.Value.Id.Should().Be("111");
    }

    [Fact]
    public void BlankChosenIsSkipped_FallsThroughToNextProvider()
    {
        var result = InputIdSelector.Select(ItemLevel.Series, Ids(("Tmdb", "  "), ("Imdb", "tt222")));

        result.Should().NotBeNull();
        result!.Value.Provider.Should().Be("Imdb");
    }

    [Fact]
    public void Season_IsNotSupported_ReturnsNull()
    {
        var result = InputIdSelector.Select(ItemLevel.Season, Ids(("Tmdb", "111"), ("Tvdb", "333")));

        result.Should().BeNull();
    }

    [Fact]
    public void Episode_IsNotSupported_ReturnsNull()
    {
        var result = InputIdSelector.Select(ItemLevel.Episode, Ids(("Tmdb", "111"), ("Tvdb", "333")));

        result.Should().BeNull();
    }
}
