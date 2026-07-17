using System;
using System.Collections.Generic;
using FluentAssertions;
using Jellyfin.Plugin.ExternalRatings.Core;
using Xunit;

namespace Jellyfin.Plugin.ExternalRatings.Tests;

public class NfoSaverDetectorTests
{
    private static readonly Guid LibA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid LibB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private static LibraryNfoSnapshot Snapshot(Guid id, string name, bool saveLocal, params string[] savers)
        => new(id, name, saveLocal, savers);

    [Fact]
    public void Detect_NfoSaverActiveAndEnabled_IsReported()
    {
        var libraries = new[] { Snapshot(LibA, "Anime", saveLocal: true, "Nfo") };

        NfoSaverDetector.Detect(libraries, new[] { LibA }).Should().Equal("Anime");
    }

    [Fact]
    public void Detect_SaveLocalMetadataOff_IsNotReported()
    {
        var libraries = new[] { Snapshot(LibA, "Anime", saveLocal: false, "Nfo") };

        NfoSaverDetector.Detect(libraries, new[] { LibA }).Should().BeEmpty();
    }

    [Fact]
    public void Detect_LibraryNotEnabled_IsIgnored()
    {
        var libraries = new[] { Snapshot(LibA, "Anime", saveLocal: true, "Nfo") };

        NfoSaverDetector.Detect(libraries, new[] { LibB }).Should().BeEmpty();
    }

    [Fact]
    public void Detect_SaverNameMatchIsCaseInsensitive()
    {
        var libraries = new[] { Snapshot(LibA, "Anime", saveLocal: true, "nfo") };

        NfoSaverDetector.Detect(libraries, new[] { LibA }).Should().Equal("Anime");
    }

    [Fact]
    public void Detect_NoNfoSaver_IsNotReported()
    {
        var libraries = new[] { Snapshot(LibA, "Anime", saveLocal: true, "Kodi", "Something") };

        NfoSaverDetector.Detect(libraries, new[] { LibA }).Should().BeEmpty();
    }

    [Fact]
    public void Detect_EmptySavers_IsNotReported()
    {
        var libraries = new[] { Snapshot(LibA, "Anime", saveLocal: true) };

        NfoSaverDetector.Detect(libraries, new[] { LibA }).Should().BeEmpty();
    }

    [Fact]
    public void Detect_NullSavers_IsNotReported()
    {
        var libraries = new[] { new LibraryNfoSnapshot(LibA, "Anime", true, null) };

        NfoSaverDetector.Detect(libraries, new[] { LibA }).Should().BeEmpty();
    }

    [Fact]
    public void Detect_MixedLibraries_ReturnsOnlyActiveEnabledOnes()
    {
        var libraries = new[]
        {
            Snapshot(LibA, "Anime", saveLocal: true, "Nfo"),
            Snapshot(LibB, "Movies", saveLocal: true, "Kodi")
        };

        NfoSaverDetector.Detect(libraries, new[] { LibA, LibB }).Should().Equal("Anime");
    }

    [Fact]
    public void Detect_NoEnabledLibraries_ReturnsEmpty()
    {
        var libraries = new[] { Snapshot(LibA, "Anime", saveLocal: true, "Nfo") };

        NfoSaverDetector.Detect(libraries, Array.Empty<Guid>()).Should().BeEmpty();
    }
}
