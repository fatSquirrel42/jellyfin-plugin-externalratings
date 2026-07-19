using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;
using FluentAssertions;
using Jellyfin.Plugin.ExternalRatings.Configuration;
using Xunit;

namespace Jellyfin.Plugin.ExternalRatings.Tests;

public class ConfigRoundtripTests
{
    private static PluginConfiguration Roundtrip(PluginConfiguration input)
    {
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        using var buffer = new MemoryStream();
        serializer.Serialize(buffer, input);
        buffer.Position = 0;
        return (PluginConfiguration)serializer.Deserialize(buffer)!;
    }

    [Fact]
    public void XmlSerializer_CanBeConstructed_ForPluginConfiguration()
    {
        // The M6 guard: XmlSerializer's ctor throws for unsupported member types (e.g. Dictionary,
        // TimeSpan). If this does not throw, the whole config graph is Xml-serializable.
        var act = () => new XmlSerializer(typeof(PluginConfiguration));

        act.Should().NotThrow();
    }

    [Fact]
    public void Defaults_MatchSpec()
    {
        var config = new PluginConfiguration();

        config.ActiveResolverKey.Should().Be("mdblist");
        config.RatingSource.Should().Be("none");
        config.CacheTtlDays.Should().Be(7);
        config.NegativeCacheTtlDays.Should().Be(1);
        config.NoMatchBehavior.Should().Be("ClearField");
        config.UnsupportedLevelBehavior.Should().Be("ClearField");
        config.DailyRequestLimit.Should().Be(1000);
        config.RunAfterLibraryScan.Should().BeTrue();
        config.EnableRealtimeListener.Should().BeTrue();
        config.DryRun.Should().BeTrue();
        config.EnabledLibraries.Should().BeEmpty();
        config.LibrarySources.Should().BeEmpty();
        config.ResolverSettings.Should().BeEmpty();
    }

    [Fact]
    public void Roundtrip_DefaultConfig_PreservesAllFields()
    {
        var original = new PluginConfiguration();

        var restored = Roundtrip(original);

        restored.Should().BeEquivalentTo(original);
    }

    [Fact]
    public void Roundtrip_PopulatedConfig_PreservesAllFields()
    {
        var original = new PluginConfiguration
        {
            EnabledLibraries = new[]
            {
                Guid.Parse("11111111-1111-1111-1111-111111111111"),
                Guid.Parse("22222222-2222-2222-2222-222222222222")
            },
            ActiveResolverKey = "mdblist",
            RatingSource = "imdb",
            LibrarySources = new[]
            {
                new LibrarySourceSetting
                {
                    LibraryId = Guid.Parse("33333333-3333-3333-3333-333333333333"),
                    Source = "letterboxd"
                }
            },
            ResolverSettings = new[]
            {
                new ResolverSetting { Key = "mdblist.apiKey", Value = "secret-key-value" }
            },
            CacheTtlDays = 14,
            NegativeCacheTtlDays = 3,
            NoMatchBehavior = "ClearField",
            UnsupportedLevelBehavior = "ClearField",
            DailyRequestLimit = 500,
            RunAfterLibraryScan = false,
            EnableRealtimeListener = false,
            DryRun = false
        };

        var restored = Roundtrip(original);

        restored.Should().BeEquivalentTo(original);
    }

    [Fact]
    public void Roundtrip_ResolverSettings_PreservesKeyAndValue()
    {
        var original = new PluginConfiguration
        {
            ResolverSettings = new[]
            {
                new ResolverSetting { Key = "mdblist.apiKey", Value = "abc123" }
            }
        };

        var restored = Roundtrip(original);

        restored.ResolverSettings.Should().ContainSingle();
        restored.ResolverSettings[0].Key.Should().Be("mdblist.apiKey");
        restored.ResolverSettings[0].Value.Should().Be("abc123");
    }
}
