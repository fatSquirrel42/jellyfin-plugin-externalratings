using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Jellyfin.Plugin.ExternalRatings.Persistence;
using Jellyfin.Plugin.ExternalRatings.Tests.Fakes;
using Xunit;

namespace Jellyfin.Plugin.ExternalRatings.Tests;

public class BackupStoreTests
{
    private static readonly Guid ItemA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ItemB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task EnsureBackedUp_StoresOriginalRating()
    {
        var store = new BackupStore(new InMemoryCacheFileStore(), new FakeClock());

        await store.EnsureBackedUpAsync(ItemA, 6.4f, CancellationToken.None);

        store.TryGet(ItemA, out var entry).Should().BeTrue();
        entry.OriginalRating.Should().Be(6.4f);
    }

    [Fact]
    public async Task EnsureBackedUp_IsDurableBeforeReturn_ReloadSeesEntry()
    {
        var fileStore = new InMemoryCacheFileStore();
        var clock = new FakeClock();
        var store = new BackupStore(fileStore, clock);
        await store.EnsureBackedUpAsync(ItemA, 6.4f, CancellationToken.None);

        // Simulate a crash after the backup but before any item write: a fresh store over the same
        // file must still recover the backup (spec §9.1 write-ahead, crash test).
        var reloaded = new BackupStore(fileStore, clock);
        await reloaded.InitializeAsync(CancellationToken.None);

        reloaded.TryGet(ItemA, out var entry).Should().BeTrue();
        entry.OriginalRating.Should().Be(6.4f);
    }

    [Fact]
    public async Task EnsureBackedUp_NeverOverwritesExistingEntry()
    {
        var store = new BackupStore(new InMemoryCacheFileStore(), new FakeClock());
        await store.EnsureBackedUpAsync(ItemA, 6.4f, CancellationToken.None);

        await store.EnsureBackedUpAsync(ItemA, 9.9f, CancellationToken.None);

        store.TryGet(ItemA, out var entry).Should().BeTrue();
        entry.OriginalRating.Should().Be(6.4f);
    }

    [Fact]
    public async Task EnsureBackedUp_NullOriginalRating_RoundTrips()
    {
        var fileStore = new InMemoryCacheFileStore();
        var store = new BackupStore(fileStore, new FakeClock());
        await store.EnsureBackedUpAsync(ItemA, null, CancellationToken.None);

        var reloaded = new BackupStore(fileStore, new FakeClock());
        await reloaded.InitializeAsync(CancellationToken.None);

        reloaded.TryGet(ItemA, out var entry).Should().BeTrue();
        entry.OriginalRating.Should().BeNull();
    }

    [Fact]
    public async Task BackedUpAt_UsesClock()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero));
        var store = new BackupStore(new InMemoryCacheFileStore(), clock);

        await store.EnsureBackedUpAsync(ItemA, 6.4f, CancellationToken.None);

        store.TryGet(ItemA, out var entry).Should().BeTrue();
        entry.BackedUpAt.Should().Be(clock.UtcNow);
    }

    [Fact]
    public void TryGet_Miss_ReturnsFalse()
    {
        var store = new BackupStore(new InMemoryCacheFileStore(), new FakeClock());

        store.TryGet(ItemA, out _).Should().BeFalse();
    }

    [Fact]
    public async Task Remove_DeletesEntry_AndPersists()
    {
        var fileStore = new InMemoryCacheFileStore();
        var store = new BackupStore(fileStore, new FakeClock());
        await store.EnsureBackedUpAsync(ItemA, 6.4f, CancellationToken.None);

        await store.RemoveAsync(ItemA, CancellationToken.None);

        store.TryGet(ItemA, out _).Should().BeFalse();

        var reloaded = new BackupStore(fileStore, new FakeClock());
        await reloaded.InitializeAsync(CancellationToken.None);
        reloaded.TryGet(ItemA, out _).Should().BeFalse();
    }

    [Fact]
    public async Task PruneOrphans_RemovesMissing_KeepsLive()
    {
        var store = new BackupStore(new InMemoryCacheFileStore(), new FakeClock());
        await store.EnsureBackedUpAsync(ItemA, 6.4f, CancellationToken.None);
        await store.EnsureBackedUpAsync(ItemB, 7.1f, CancellationToken.None);

        store.PruneOrphans(new HashSet<Guid> { ItemA });

        store.TryGet(ItemA, out _).Should().BeTrue();
        store.TryGet(ItemB, out _).Should().BeFalse();
    }

    [Fact]
    public async Task Clear_RemovesAll_AndDeletesFile()
    {
        var fileStore = new InMemoryCacheFileStore();
        var store = new BackupStore(fileStore, new FakeClock());
        await store.EnsureBackedUpAsync(ItemA, 6.4f, CancellationToken.None);

        await store.ClearAsync(CancellationToken.None);

        store.TryGet(ItemA, out _).Should().BeFalse();
        fileStore.Exists.Should().BeFalse();
    }

    [Fact]
    public async Task CorruptFile_StartsEmpty()
    {
        var fileStore = new InMemoryCacheFileStore();
        fileStore.SetRawContent(System.Text.Encoding.UTF8.GetBytes("not json"));
        var store = new BackupStore(fileStore, new FakeClock());

        var act = async () => await store.InitializeAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
        store.TryGet(ItemA, out _).Should().BeFalse();
    }
}
