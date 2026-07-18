using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Jellyfin.Plugin.ExternalRatings.Core;
using Jellyfin.Plugin.ExternalRatings.Core.Abstractions;
using Jellyfin.Plugin.ExternalRatings.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ExternalRatings.Tests;

public class RestoreRunnerTests
{
    private static readonly Guid ItemA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ItemB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static RestoreRunner Build(RecordingBackupStore backup, RecordingItemWriter writer)
        => new(backup, writer, NullLogger<RestoreRunner>.Instance);

    [Fact]
    public async Task RestoreAll_WritesOriginal_RemovesBackup_WithRestoredReason()
    {
        var backup = new RecordingBackupStore();
        var writer = new RecordingItemWriter();
        await backup.EnsureBackedUpAsync(ItemA, 6.4f, CancellationToken.None);
        // Pretend the plugin had overwritten the rating to a MAL score.
        writer.SeedRating(ItemA, 8.8f);

        var count = await Build(backup, writer).RestoreAllAsync(CancellationToken.None);

        count.Should().Be(1);
        writer.Writes.Should().ContainSingle();
        writer.Writes[0].Should().BeEquivalentTo(new { ItemId = ItemA, Value = (float?)6.4f, Reason = ItemWriteReason.RatingRestored });
        writer.GetCommunityRating(new RatingItemRef(ItemA, null)).Should().Be(6.4f);
        backup.TryGet(ItemA, out _).Should().BeFalse();
    }

    [Fact]
    public async Task RestoreAll_NullOriginal_ClearsRating()
    {
        var backup = new RecordingBackupStore();
        var writer = new RecordingItemWriter();
        // The item originally had no rating; restore must clear the plugin's written value.
        await backup.EnsureBackedUpAsync(ItemA, null, CancellationToken.None);
        writer.SeedRating(ItemA, 8.8f);

        var count = await Build(backup, writer).RestoreAllAsync(CancellationToken.None);

        count.Should().Be(1);
        writer.Writes[0].Value.Should().BeNull();
        writer.GetCommunityRating(new RatingItemRef(ItemA, null)).Should().BeNull();
    }

    [Fact]
    public async Task RestoreAll_Empty_ReturnsZero_NoWrites()
    {
        var backup = new RecordingBackupStore();
        var writer = new RecordingItemWriter();

        var count = await Build(backup, writer).RestoreAllAsync(CancellationToken.None);

        count.Should().Be(0);
        writer.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task RestoreAll_MultipleEntries_RestoresEach_ReturnsCount()
    {
        var backup = new RecordingBackupStore();
        var writer = new RecordingItemWriter();
        await backup.EnsureBackedUpAsync(ItemA, 6.4f, CancellationToken.None);
        await backup.EnsureBackedUpAsync(ItemB, 7.1f, CancellationToken.None);

        var count = await Build(backup, writer).RestoreAllAsync(CancellationToken.None);

        count.Should().Be(2);
        writer.Writes.Should().HaveCount(2);
        backup.GetItemIds().Should().BeEmpty();
    }

    [Fact]
    public async Task RestoreAll_HonorsCancellation()
    {
        var backup = new RecordingBackupStore();
        var writer = new RecordingItemWriter();
        await backup.EnsureBackedUpAsync(ItemA, 6.4f, CancellationToken.None);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await Build(backup, writer).RestoreAllAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
