using System;
using System.IO;
using System.Text;
using FluentAssertions;
using Jellyfin.Plugin.ExternalRatings.Resolvers.Imdb;
using Xunit;

namespace Jellyfin.Plugin.ExternalRatings.Tests;

public class ImdbRatingsIndexTests
{
    private const string Header = "tconst\taverageRating\tnumVotes\n";

    private static ImdbRatingsIndex Load(string body)
        => ImdbRatingsIndex.Load(new MemoryStream(Encoding.UTF8.GetBytes(Header + body)));

    [Fact]
    public void ParsesRowsAndLooksThemUp()
    {
        var index = Load("tt0903747\t9.5\t2671907\ntt2301451\t9.5\t507558\n");

        index.Count.Should().Be(2);
        index.TryGetRating("tt0903747", out var series).Should().BeTrue();
        series.Should().Be(9.5f);
        index.TryGetRating("tt2301451", out var episode).Should().BeTrue();
        episode.Should().Be(9.5f);
    }

    [Fact]
    public void UnknownIdIsNotFound()
    {
        var index = Load("tt0903747\t9.5\t2671907\n");

        index.TryGetRating("tt9999999", out var rating).Should().BeFalse();
        rating.Should().Be(0f);
    }

    [Fact]
    public void LookupWorksRegardlessOfInputOrder()
    {
        // The index sorts for binary search; rows arrive in no particular order.
        var index = Load("tt0000500\t7.0\t10\ntt0000100\t6.0\t10\ntt0000900\t8.0\t10\ntt0000300\t5.0\t10\n");

        index.Count.Should().Be(4);
        index.TryGetRating("tt0000100", out var a).Should().BeTrue();
        a.Should().Be(6.0f);
        index.TryGetRating("tt0000900", out var b).Should().BeTrue();
        b.Should().Be(8.0f);
        index.TryGetRating("tt0000300", out var c).Should().BeTrue();
        c.Should().Be(5.0f);
        index.TryGetRating("tt0000700", out _).Should().BeFalse();
    }

    [Fact]
    public void LeadingZerosAndCasingDoNotMatter()
    {
        // tconsts are zero-padded to a varying width and the id is stored numerically, so
        // "tt0000100" and "tt100" denote the same title.
        var index = Load("tt0000100\t6.0\t10\n");

        index.TryGetRating("tt100", out var a).Should().BeTrue();
        a.Should().Be(6.0f);
        index.TryGetRating("TT0000100", out var b).Should().BeTrue();
        b.Should().Be(6.0f);
        index.TryGetRating("  tt0000100  ", out var c).Should().BeTrue();
        c.Should().Be(6.0f);
    }

    [Fact]
    public void HeaderIsSkipped()
    {
        var index = Load(string.Empty);

        index.Count.Should().Be(0);
    }

    [Fact]
    public void MalformedRowsAreSkipped()
    {
        var index = Load(
            "tt0000100\t6.0\t10\n" +
            "garbage\n" +                    // no tabs
            "nm0000101\t7.0\t10\n" +         // a name, not a title
            "tt0000102\t\\N\t10\n" +         // null rating
            "tt0000103\tnope\t10\n" +        // unparseable rating
            "\n" +                           // blank
            "tt0000104\t7.5\t10\n");

        index.Count.Should().Be(2);
        index.TryGetRating("tt0000100", out _).Should().BeTrue();
        index.TryGetRating("tt0000104", out _).Should().BeTrue();
        index.TryGetRating("tt0000102", out _).Should().BeFalse();
        index.TryGetRating("tt0000103", out _).Should().BeFalse();
    }

    [Fact]
    public void RatingsAreParsedInvariantly()
    {
        // The file always uses a dot; a comma-decimal culture must not change that.
        var index = Load("tt0000100\t8.9\t10\n");

        index.TryGetRating("tt0000100", out var rating).Should().BeTrue();
        rating.Should().BeApproximately(8.9f, 0.0001f);
    }

    [Fact]
    public void NonPositiveRatingsAreDropped()
    {
        // IMDb never publishes 0; a 0 row would mean "no rating", not "rated zero".
        var index = Load("tt0000100\t0.0\t10\ntt0000101\t-1\t10\ntt0000102\t1.0\t10\n");

        index.Count.Should().Be(1);
        index.TryGetRating("tt0000102", out _).Should().BeTrue();
    }

    [Fact]
    public void DuplicateIdKeepsOneEntry()
    {
        var index = Load("tt0000100\t6.0\t10\ntt0000100\t7.0\t10\n");

        index.Count.Should().Be(1);
        index.TryGetRating("tt0000100", out _).Should().BeTrue();
    }

    [Fact]
    public void NullOrNonTconstInputIsNotFound()
    {
        var index = Load("tt0000100\t6.0\t10\n");

        index.TryGetRating(null, out _).Should().BeFalse();
        index.TryGetRating(string.Empty, out _).Should().BeFalse();
        index.TryGetRating("   ", out _).Should().BeFalse();
        index.TryGetRating("nm0000100", out _).Should().BeFalse();
        index.TryGetRating("100", out _).Should().BeFalse();
        index.TryGetRating("ttabc", out _).Should().BeFalse();
    }

    [Fact]
    public void EmptyIndexFindsNothing()
    {
        ImdbRatingsIndex.Empty.Count.Should().Be(0);
        ImdbRatingsIndex.Empty.TryGetRating("tt0000100", out _).Should().BeFalse();
    }

    [Fact]
    public void HandlesCarriageReturns()
    {
        var index = ImdbRatingsIndex.Load(new MemoryStream(Encoding.UTF8.GetBytes(
            "tconst\taverageRating\tnumVotes\r\ntt0000100\t6.0\t10\r\n")));

        index.Count.Should().Be(1);
        index.TryGetRating("tt0000100", out var rating).Should().BeTrue();
        rating.Should().Be(6.0f);
    }

    [Fact]
    public void ScalesToManyRows()
    {
        // Guards the grow/sort path, which only kicks in past the initial capacity.
        var sb = new StringBuilder();
        for (var i = 1; i <= 50_000; i++)
        {
            sb.Append("tt").Append(i.ToString("D7", System.Globalization.CultureInfo.InvariantCulture))
              .Append('\t').Append((i % 100 / 10.0 + 0.1).ToString("F1", System.Globalization.CultureInfo.InvariantCulture))
              .Append("\t10\n");
        }

        var index = Load(sb.ToString());

        index.Count.Should().Be(50_000);
        index.TryGetRating("tt0000001", out _).Should().BeTrue();
        index.TryGetRating("tt0025000", out _).Should().BeTrue();
        index.TryGetRating("tt0050000", out _).Should().BeTrue();
        index.TryGetRating("tt0050001", out _).Should().BeFalse();
    }
}
