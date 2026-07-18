using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Jellyfin.Plugin.ExternalRatings.Core;
using Jellyfin.Plugin.ExternalRatings.Core.Abstractions;
using Jellyfin.Plugin.ExternalRatings.Tests.Fakes;
using Xunit;

namespace Jellyfin.Plugin.ExternalRatings.Tests;

public class SelfWriteTrackingItemWriterTests
{
    private static readonly Guid Id = Guid.Parse("55555555-0000-0000-0000-000000000001");

    private static readonly RatingItemRef Ref = new(Id, "A Movie");

    /// <summary>An inner writer that captures the tracker's view of the id at the moment WriteAsync runs.</summary>
    private sealed class ProbeWriter : IItemWriter
    {
        private readonly Func<Guid, bool> _probe;

        public ProbeWriter(Func<Guid, bool> probe) => _probe = probe;

        public bool? RecentAtWriteTime { get; private set; }

        public int WriteCount { get; private set; }

        public float? GetCommunityRating(RatingItemRef item) => null;

        public Task WriteAsync(RatingItemRef item, float? value, ItemWriteReason reason, CancellationToken cancellationToken)
        {
            RecentAtWriteTime = _probe(item.ItemId);
            WriteCount++;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task WriteAsync_MarksSelfWrite_BeforeDelegating()
    {
        var clock = new FakeClock();
        var tracker = new SelfWriteTracker(clock, TimeSpan.FromSeconds(30));
        var inner = new ProbeWriter(tracker.IsRecent);
        var writer = new SelfWriteTrackingItemWriter(inner, tracker);

        await writer.WriteAsync(Ref, 8.0f, ItemWriteReason.RatingUpdated, CancellationToken.None);

        inner.RecentAtWriteTime.Should().BeTrue("the id must be marked before the inner write runs");
        inner.WriteCount.Should().Be(1);
        tracker.IsRecent(Id).Should().BeTrue();
    }

    [Fact]
    public void GetCommunityRating_Delegates()
    {
        var clock = new FakeClock();
        var tracker = new SelfWriteTracker(clock, TimeSpan.FromSeconds(30));
        var inner = new RecordingItemWriter();
        inner.SeedRating(Id, 6.5f);
        var writer = new SelfWriteTrackingItemWriter(inner, tracker);

        writer.GetCommunityRating(Ref).Should().Be(6.5f);
    }
}
