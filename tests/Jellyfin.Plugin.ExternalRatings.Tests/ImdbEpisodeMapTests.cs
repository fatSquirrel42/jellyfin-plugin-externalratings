using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using FluentAssertions;
using Jellyfin.Plugin.ExternalRatings.Resolvers.Imdb;
using Xunit;

namespace Jellyfin.Plugin.ExternalRatings.Tests;

public class ImdbEpisodeMapTests
{
    private const string Header = "tconst\tparentTconst\tseasonNumber\tepisodeNumber\n";

    private static ImdbEpisodeMap Load(string body, params string[] parents)
    {
        var filter = new HashSet<long>();
        foreach (var parent in parents)
        {
            var id = ImdbRatingsIndex.ParseTconst(parent);
            if (id.HasValue)
            {
                filter.Add(id.Value);
            }
        }

        return ImdbEpisodeMap.Load(new MemoryStream(Encoding.UTF8.GetBytes(Header + body)), filter);
    }

    [Fact]
    public void MapsAnEpisodeToItsSeason()
    {
        // Real rows: Breaking Bad's "Ozymandias" is season 5, episode 14.
        var map = Load("tt2301451\ttt0903747\t5\t14\n", "tt0903747");

        map.TryGetSeasonOf("tt2301451", out var season).Should().BeTrue();
        season.Parent.Should().Be(903747);
        season.Season.Should().Be(5);
    }

    [Fact]
    public void MapsASeasonToItsEpisodes()
    {
        var map = Load(
            "tt0000101\ttt0000001\t1\t1\n" +
            "tt0000102\ttt0000001\t1\t2\n" +
            "tt0000103\ttt0000001\t1\t3\n" +
            "tt0000201\ttt0000001\t2\t1\n",
            "tt0000001");

        map.GetEpisodesOf(1, 1).Should().BeEquivalentTo(new[] { 101L, 102L, 103L });
        map.GetEpisodesOf(1, 2).Should().BeEquivalentTo(new[] { 201L });
    }

    [Fact]
    public void EpisodesAreOrderedByEpisodeNumber()
    {
        // The order is not load-bearing for an unweighted mean, but a stable one keeps logs and
        // any future per-episode reporting readable.
        var map = Load(
            "tt0000103\ttt0000001\t1\t3\n" +
            "tt0000101\ttt0000001\t1\t1\n" +
            "tt0000102\ttt0000001\t1\t2\n",
            "tt0000001");

        map.GetEpisodesOf(1, 1).Should().Equal(101L, 102L, 103L);
    }

    [Fact]
    public void OnlyTheRequestedParentsAreKept()
    {
        // The whole file is 9.87 M rows; unfiltered it would cost ~178 MB. Filtering to the
        // library's series brings it down to kilobytes.
        var map = Load(
            "tt0000101\ttt0000001\t1\t1\n" +
            "tt0000901\ttt0000009\t1\t1\n",
            "tt0000001");

        map.EpisodeCount.Should().Be(1);
        map.TryGetSeasonOf("tt0000101", out _).Should().BeTrue();
        map.TryGetSeasonOf("tt0000901", out _).Should().BeFalse();
        map.Covers(1).Should().BeTrue();
        map.Covers(9).Should().BeFalse();
    }

    [Fact]
    public void RowsWithoutSeasonOrEpisodeNumbersAreDropped()
    {
        // 20.95 % of the real file carries \N for both: specials and unnumbered entries. Keeping
        // them would invent a phantom season.
        var map = Load(
            "tt0000101\ttt0000001\t1\t1\n" +
            "tt0000102\ttt0000001\t\\N\t\\N\n" +
            "tt0000103\ttt0000001\t1\t\\N\n" +
            "tt0000104\ttt0000001\t\\N\t2\n",
            "tt0000001");

        map.EpisodeCount.Should().Be(1);
        map.TryGetSeasonOf("tt0000102", out _).Should().BeFalse();
        map.TryGetSeasonOf("tt0000103", out _).Should().BeFalse();
        map.TryGetSeasonOf("tt0000104", out _).Should().BeFalse();
    }

    [Fact]
    public void MalformedRowsAreSkipped()
    {
        var map = Load(
            "tt0000101\ttt0000001\t1\t1\n" +
            "garbage\n" +
            "tt0000102\ttt0000001\t1\n" +          // too few columns
            "nm0000103\ttt0000001\t1\t3\n" +       // a name, not a title
            "tt0000104\tnm0000001\t1\t4\n" +       // parent is not a title
            "tt0000105\ttt0000001\tone\t5\n" +     // unparseable season
            "\n" +
            "tt0000106\ttt0000001\t1\t6\n",
            "tt0000001", "nm0000001");

        map.EpisodeCount.Should().Be(2);
        map.GetEpisodesOf(1, 1).Should().Equal(101L, 106L);
    }

    [Fact]
    public void HandlesCarriageReturns()
    {
        var map = ImdbEpisodeMap.Load(
            new MemoryStream(Encoding.UTF8.GetBytes(Header.Replace("\n", "\r\n") + "tt0000101\ttt0000001\t1\t1\r\n")),
            new HashSet<long> { 1 });

        map.EpisodeCount.Should().Be(1);
        map.GetEpisodesOf(1, 1).Should().Equal(101L);
    }

    [Fact]
    public void ADuplicateRowIsKeptOnce()
    {
        var map = Load(
            "tt0000101\ttt0000001\t1\t1\n" +
            "tt0000101\ttt0000001\t1\t1\n",
            "tt0000001");

        map.EpisodeCount.Should().Be(1);
        map.GetEpisodesOf(1, 1).Should().Equal(101L);
    }

    [Fact]
    public void AnUnknownSeasonIsEmpty()
    {
        var map = Load("tt0000101\ttt0000001\t1\t1\n", "tt0000001");

        map.GetEpisodesOf(1, 7).Should().BeEmpty();
        map.GetEpisodesOf(9, 1).Should().BeEmpty();
    }

    [Fact]
    public void SeasonZeroIsRepresentableEvenThoughIMDbNeverUsesIt()
    {
        // Measured: zero rows in all 9.87 M carry seasonNumber 0, so a Jellyfin specials season can
        // never identify itself. The map itself must not be what forbids it, though — that would
        // hide a data change behind a parser rule.
        var map = Load("tt0000101\ttt0000001\t0\t1\n", "tt0000001");

        map.GetEpisodesOf(1, 0).Should().Equal(101L);
    }

    [Fact]
    public void AnEmptyFilterKeepsNothing()
    {
        var map = ImdbEpisodeMap.Load(
            new MemoryStream(Encoding.UTF8.GetBytes(Header + "tt0000101\ttt0000001\t1\t1\n")),
            new HashSet<long>());

        map.EpisodeCount.Should().Be(0);
    }

    [Fact]
    public void NonTconstLookupsAreNotFound()
    {
        var map = Load("tt0000101\ttt0000001\t1\t1\n", "tt0000001");

        map.TryGetSeasonOf(null, out _).Should().BeFalse();
        map.TryGetSeasonOf(string.Empty, out _).Should().BeFalse();
        map.TryGetSeasonOf("   ", out _).Should().BeFalse();
        map.TryGetSeasonOf("nm0000101", out _).Should().BeFalse();
        map.TryGetSeasonOf("101", out _).Should().BeFalse();
    }

    [Fact]
    public void ScalesToManyRows()
    {
        var sb = new StringBuilder();
        for (var i = 1; i <= 20_000; i++)
        {
            sb.Append("tt").Append(i.ToString("D7", System.Globalization.CultureInfo.InvariantCulture))
              .Append("\ttt0000001\t").Append((i % 20) + 1).Append('\t').Append(i).Append('\n');
        }

        var map = Load(sb.ToString(), "tt0000001");

        map.EpisodeCount.Should().Be(20_000);
        map.GetEpisodesOf(1, 1).Should().HaveCount(1000);
    }
}
