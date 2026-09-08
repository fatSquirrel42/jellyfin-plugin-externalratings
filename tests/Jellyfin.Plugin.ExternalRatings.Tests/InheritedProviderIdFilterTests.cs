using System.Collections.Generic;
using FluentAssertions;
using Jellyfin.Plugin.ExternalRatings.Core;
using Xunit;

namespace Jellyfin.Plugin.ExternalRatings.Tests;

public class InheritedProviderIdFilterTests
{
    private static Dictionary<string, string> Ids(params (string Provider, string Id)[] pairs)
    {
        var dict = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var (provider, id) in pairs)
        {
            dict[provider] = id;
        }

        return dict;
    }

    [Fact]
    public void DropsAnIdThatIsIdenticalToTheParentsOwn()
    {
        // Seen in the wild: an Episode carrying the *series'* IMDb id. Left alone, every episode of
        // the series would resolve to the series score and be written as if it were its own.
        var episode = Ids(("Imdb", "tt0903747"), ("Tvdb", "8951947"));
        var series = Ids(("Imdb", "tt0903747"), ("Tvdb", "81189"));

        var result = InheritedProviderIdFilter.Strip(episode, series);

        result.Should().NotContainKey("Imdb");
        result.Should().ContainKey("Tvdb").WhoseValue.Should().Be("8951947");
    }

    [Fact]
    public void KeepsAGenuineOwnId()
    {
        var episode = Ids(("Imdb", "tt2301451"));
        var series = Ids(("Imdb", "tt0903747"));

        var result = InheritedProviderIdFilter.Strip(episode, series);

        result.Should().ContainKey("Imdb").WhoseValue.Should().Be("tt2301451");
    }

    [Fact]
    public void ComparisonIgnoresCaseAndSurroundingSpace()
    {
        var episode = Ids(("Imdb", " TT0903747 "));
        var series = Ids(("Imdb", "tt0903747"));

        InheritedProviderIdFilter.Strip(episode, series).Should().NotContainKey("Imdb");
    }

    [Fact]
    public void WithNoParentIdsNothingIsDropped()
    {
        var episode = Ids(("Imdb", "tt2301451"), ("Tvdb", "8951947"));

        var result = InheritedProviderIdFilter.Strip(episode, Ids());

        result.Should().HaveCount(2);
    }

    [Fact]
    public void OnlyTheMatchingProviderIsCompared()
    {
        // Identical values under *different* providers are a coincidence, not inheritance.
        var episode = Ids(("Tvdb", "12345"));
        var series = Ids(("Tmdb", "12345"));

        InheritedProviderIdFilter.Strip(episode, series).Should().ContainKey("Tvdb");
    }

    [Fact]
    public void DroppingEveryIdYieldsAnEmptySet()
    {
        // The item then has no usable input id, which is the honest outcome: better no rating than
        // the parent's rating.
        var episode = Ids(("Imdb", "tt0903747"));
        var series = Ids(("Imdb", "tt0903747"));

        InheritedProviderIdFilter.Strip(episode, series).Should().BeEmpty();
    }

    [Fact]
    public void ResultKeysStayCaseInsensitive()
    {
        var result = InheritedProviderIdFilter.Strip(Ids(("Imdb", "tt1")), Ids());

        result.ContainsKey("imdb").Should().BeTrue();
    }

    [Fact]
    public void BlankValuesAreNotTreatedAsMatches()
    {
        var episode = Ids(("Imdb", "  "));
        var series = Ids(("Imdb", "  "));

        // A blank is not an id at all; InputIdSelector ignores it either way, so leave it be.
        InheritedProviderIdFilter.Strip(episode, series).Should().ContainKey("Imdb");
    }
}
