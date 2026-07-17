using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Jellyfin.Plugin.ExternalRatings.Core;
using Jellyfin.Plugin.ExternalRatings.Core.Abstractions;
using Jellyfin.Plugin.ExternalRatings.Persistence;
using Jellyfin.Plugin.ExternalRatings.Resolvers;
using Jellyfin.Plugin.ExternalRatings.Tests.Fakes;
using Xunit;

namespace Jellyfin.Plugin.ExternalRatings.Tests;

public class FileRatingCacheTests
{
    private static RatingCacheKey Key(string id = "111", ItemLevel level = ItemLevel.Movie)
        => new("mdblist", "myanimelist", "Tmdb", id, level);

    private RatingCacheEntry Found(FakeClock clock, float score, TimeSpan ttl)
        => new(RatingResolution.Found, score, clock.UtcNow + ttl);

    [Fact]
    public async Task SetThenTryGet_ReturnsEntry()
    {
        var clock = new FakeClock();
        var cache = new FileRatingCache(new InMemoryCacheFileStore(), clock);
        await cache.InitializeAsync(CancellationToken.None);

        cache.Set(Key(), Found(clock, 8.5f, TimeSpan.FromHours(1)));

        cache.TryGet(Key(), out var entry).Should().BeTrue();
        entry.Kind.Should().Be(RatingResolution.Found);
        entry.Score.Should().Be(8.5f);
    }

    [Fact]
    public void TryGet_Miss_ReturnsFalse()
    {
        var cache = new FileRatingCache(new InMemoryCacheFileStore(), new FakeClock());

        cache.TryGet(Key(), out _).Should().BeFalse();
    }

    [Fact]
    public void TryGet_Expired_ReturnsFalse()
    {
        var clock = new FakeClock();
        var cache = new FileRatingCache(new InMemoryCacheFileStore(), clock);
        cache.Set(Key(), Found(clock, 8.5f, TimeSpan.FromHours(1)));

        clock.Advance(TimeSpan.FromHours(2));

        cache.TryGet(Key(), out _).Should().BeFalse();
    }

    [Fact]
    public async Task NegativeEntry_RoundTripsThroughFlushAndReload()
    {
        var clock = new FakeClock();
        var store = new InMemoryCacheFileStore();
        var cache = new FileRatingCache(store, clock);
        cache.Set(Key(), new RatingCacheEntry(RatingResolution.NoMatch, null, clock.UtcNow + TimeSpan.FromHours(1)));
        await cache.FlushAsync(CancellationToken.None);

        var reloaded = new FileRatingCache(store, clock);
        await reloaded.InitializeAsync(CancellationToken.None);

        reloaded.TryGet(Key(), out var entry).Should().BeTrue();
        entry.Kind.Should().Be(RatingResolution.NoMatch);
        entry.Score.Should().BeNull();
    }

    [Fact]
    public async Task FoundEntry_ScoreRoundTrips()
    {
        var clock = new FakeClock();
        var store = new InMemoryCacheFileStore();
        var cache = new FileRatingCache(store, clock);
        cache.Set(Key(), Found(clock, 7.3f, TimeSpan.FromHours(5)));
        await cache.FlushAsync(CancellationToken.None);

        var reloaded = new FileRatingCache(store, clock);
        await reloaded.InitializeAsync(CancellationToken.None);

        reloaded.TryGet(Key(), out var entry).Should().BeTrue();
        entry.Score.Should().Be(7.3f);
    }

    [Fact]
    public async Task Clear_RemovesInMemoryImmediately()
    {
        var clock = new FakeClock();
        var cache = new FileRatingCache(new InMemoryCacheFileStore(), clock);
        cache.Set(Key(), Found(clock, 8.5f, TimeSpan.FromHours(1)));

        await cache.ClearAsync(CancellationToken.None);

        cache.TryGet(Key(), out _).Should().BeFalse();
    }

    [Fact]
    public async Task Clear_DeletesPersistedFile()
    {
        var clock = new FakeClock();
        var store = new InMemoryCacheFileStore();
        var cache = new FileRatingCache(store, clock);
        cache.Set(Key(), Found(clock, 8.5f, TimeSpan.FromHours(1)));
        await cache.FlushAsync(CancellationToken.None);
        store.Exists.Should().BeTrue();

        await cache.ClearAsync(CancellationToken.None);

        store.Exists.Should().BeFalse();
    }

    [Fact]
    public async Task Clear_ThenFlush_DoesNotReanimate()
    {
        var clock = new FakeClock();
        var store = new InMemoryCacheFileStore();
        var cache = new FileRatingCache(store, clock);
        cache.Set(Key(), Found(clock, 8.5f, TimeSpan.FromHours(1)));

        await cache.ClearAsync(CancellationToken.None);
        await cache.FlushAsync(CancellationToken.None);

        var reloaded = new FileRatingCache(store, clock);
        await reloaded.InitializeAsync(CancellationToken.None);
        reloaded.TryGet(Key(), out _).Should().BeFalse();
    }

    [Fact]
    public async Task CorruptFile_StartsEmpty()
    {
        var store = new InMemoryCacheFileStore();
        store.SetRawContent(Encoding.UTF8.GetBytes("{ this is not valid json"));
        var cache = new FileRatingCache(store, new FakeClock());

        var act = async () => await cache.InitializeAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
        cache.TryGet(Key(), out _).Should().BeFalse();
    }

    [Fact]
    public async Task SchemaVersionMismatch_StartsEmpty()
    {
        var store = new InMemoryCacheFileStore();
        store.SetRawContent(Encoding.UTF8.GetBytes("{\"SchemaVersion\":999,\"Entries\":[]}"));
        var cache = new FileRatingCache(store, new FakeClock());

        await cache.InitializeAsync(CancellationToken.None);

        cache.TryGet(Key(), out _).Should().BeFalse();
    }

    [Fact]
    public void PruneExpired_DropsStaleEntries()
    {
        var clock = new FakeClock();
        var cache = new FileRatingCache(new InMemoryCacheFileStore(), clock);
        cache.Set(Key("short"), Found(clock, 8.5f, TimeSpan.FromMinutes(30)));
        cache.Set(Key("long"), Found(clock, 8.5f, TimeSpan.FromHours(10)));

        clock.Advance(TimeSpan.FromHours(1));
        cache.PruneExpired();
        clock.UtcNow = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        // "short" was pruned while expired; even after resetting the clock it stays gone.
        cache.TryGet(Key("short"), out _).Should().BeFalse();
        cache.TryGet(Key("long"), out _).Should().BeTrue();
    }

    [Fact]
    public void Remove_DropsSingleEntry()
    {
        var clock = new FakeClock();
        var cache = new FileRatingCache(new InMemoryCacheFileStore(), clock);
        cache.Set(Key(), Found(clock, 8.5f, TimeSpan.FromHours(1)));

        cache.Remove(Key());

        cache.TryGet(Key(), out _).Should().BeFalse();
    }

    [Fact]
    public void KeyComposition_LevelDistinguishesEntries()
    {
        var clock = new FakeClock();
        var cache = new FileRatingCache(new InMemoryCacheFileStore(), clock);
        cache.Set(Key("111", ItemLevel.Movie), Found(clock, 8.5f, TimeSpan.FromHours(1)));

        // Same everything but a different level must not collide.
        cache.TryGet(Key("111", ItemLevel.Series), out _).Should().BeFalse();
        cache.TryGet(Key("111", ItemLevel.Movie), out _).Should().BeTrue();
    }
}
