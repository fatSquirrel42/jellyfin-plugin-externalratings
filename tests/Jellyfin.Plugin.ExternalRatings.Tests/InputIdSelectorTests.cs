using System;
using System.Collections.Generic;
using FluentAssertions;
using Jellyfin.Plugin.ExternalRatings.Core;
using Jellyfin.Plugin.ExternalRatings.Tests.Fakes;
using Xunit;

namespace Jellyfin.Plugin.ExternalRatings.Tests;

public class InputIdSelectorTests
{
    // The mdblist priorities, restated here so these tests describe a selector contract rather
    // than whatever the shipped resolver happens to declare.
    private static readonly string[] MovieProviders = { "Tmdb", "Imdb" };
    private static readonly string[] SeriesProviders = { "Tmdb", "Imdb", "Tvdb" };

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
        var result = InputIdSelector.Select(MovieProviders, Ids(("Tmdb", "111"), ("Imdb", "tt222")));

        result.Should().NotBeNull();
        result!.Value.Provider.Should().Be("Tmdb");
        result.Value.Id.Should().Be("111");
    }

    [Fact]
    public void Movie_FallsBackToImdb_WhenNoTmdb()
    {
        var result = InputIdSelector.Select(MovieProviders, Ids(("Imdb", "tt222")));

        result.Should().NotBeNull();
        result!.Value.Provider.Should().Be("Imdb");
        result.Value.Id.Should().Be("tt222");
    }

    [Fact]
    public void Movie_ReturnsNull_WhenOnlyTvdbPresent()
    {
        // Tvdb is not an allowed input provider for movies.
        var result = InputIdSelector.Select(MovieProviders, Ids(("Tvdb", "333")));

        result.Should().BeNull();
    }

    [Fact]
    public void Series_PrefersTmdb_WhenAllPresent()
    {
        var result = InputIdSelector.Select(SeriesProviders, Ids(("Tvdb", "333"), ("Imdb", "tt222"), ("Tmdb", "111")));

        result.Should().NotBeNull();
        result!.Value.Provider.Should().Be("Tmdb");
    }

    [Fact]
    public void Series_FallsBackToImdb_WhenNoTmdb()
    {
        var result = InputIdSelector.Select(SeriesProviders, Ids(("Tvdb", "333"), ("Imdb", "tt222")));

        result.Should().NotBeNull();
        result!.Value.Provider.Should().Be("Imdb");
    }

    [Fact]
    public void Series_FallsBackToTvdb_WhenOnlyTvdb()
    {
        var result = InputIdSelector.Select(SeriesProviders, Ids(("Tvdb", "333")));

        result.Should().NotBeNull();
        result!.Value.Provider.Should().Be("Tvdb");
        result.Value.Id.Should().Be("333");
    }

    [Fact]
    public void ReturnsNull_WhenNoIdsAtAll()
    {
        InputIdSelector.Select(MovieProviders, Ids()).Should().BeNull();
    }

    [Fact]
    public void ReturnsNull_WhenNoProvidersAllowed()
    {
        InputIdSelector.Select(Array.Empty<string>(), Ids(("Tmdb", "111"))).Should().BeNull();
    }

    [Fact]
    public void ProviderKeysAreCaseInsensitive()
    {
        var result = InputIdSelector.Select(MovieProviders, Ids(("tmdb", "111")));

        result.Should().NotBeNull();
        result!.Value.Provider.Should().Be("Tmdb");
        result.Value.Id.Should().Be("111");
    }

    [Fact]
    public void HasUnusedAlternatives_IsTrue_WhenMoreThanOneAllowedIdPresent()
    {
        var result = InputIdSelector.Select(MovieProviders, Ids(("Tmdb", "111"), ("Imdb", "tt222")));

        result!.Value.HasUnusedAlternatives.Should().BeTrue();
    }

    [Fact]
    public void HasUnusedAlternatives_IsFalse_WhenOnlyOneAllowedIdPresent()
    {
        // Tvdb is present but not allowed for movies, so it is not an "alternative".
        var result = InputIdSelector.Select(MovieProviders, Ids(("Tmdb", "111"), ("Tvdb", "333")));

        result!.Value.HasUnusedAlternatives.Should().BeFalse();
    }

    [Fact]
    public void BlankIdsAreIgnored()
    {
        var result = InputIdSelector.Select(SeriesProviders, Ids(("Tmdb", "  "), ("Imdb", "tt222")));

        result.Should().NotBeNull();
        result!.Value.Provider.Should().Be("Imdb");
    }

    // --- resolver-driven priority (the capability is the resolver's, not the selector's) ---

    [Fact]
    public void PriorityComesFromTheResolver_NotAFixedTable()
    {
        // A resolver that only speaks Imdb must pick Imdb even though Tmdb is present and would
        // win under the mdblist ordering.
        var resolver = new StubRatingResolver
        {
            SupportedInputProviders = new Dictionary<ItemLevel, IReadOnlyList<string>>
            {
                [ItemLevel.Movie] = new[] { "Imdb" }
            }
        };

        var result = InputIdSelector.Select(resolver, ItemLevel.Movie, Ids(("Tmdb", "111"), ("Imdb", "tt222")));

        result.Should().NotBeNull();
        result!.Value.Provider.Should().Be("Imdb");
        result.Value.Id.Should().Be("tt222");
    }

    [Fact]
    public void LevelTheResolverDoesNotDeclare_ReturnsNull()
    {
        var resolver = new StubRatingResolver
        {
            SupportedInputProviders = new Dictionary<ItemLevel, IReadOnlyList<string>>
            {
                [ItemLevel.Movie] = new[] { "Imdb" }
            }
        };

        InputIdSelector.Select(resolver, ItemLevel.Episode, Ids(("Imdb", "tt222"))).Should().BeNull();
        InputIdSelector.ProviderPriority(resolver, ItemLevel.Episode).Should().BeEmpty();
    }

    [Fact]
    public void EpisodeIsSupported_WhenTheResolverDeclaresIt()
    {
        // The dataset resolver declares Episode; the selector must not veto it.
        var resolver = new StubRatingResolver
        {
            SupportedInputProviders = new Dictionary<ItemLevel, IReadOnlyList<string>>
            {
                [ItemLevel.Episode] = new[] { "Imdb" }
            }
        };

        var result = InputIdSelector.Select(resolver, ItemLevel.Episode, Ids(("Imdb", "tt16364366"), ("Tvdb", "8951947")));

        result.Should().NotBeNull();
        result!.Value.Provider.Should().Be("Imdb");
        result.Value.Id.Should().Be("tt16364366");
    }
}
