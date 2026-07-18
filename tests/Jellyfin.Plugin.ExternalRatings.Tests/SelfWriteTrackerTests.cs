using System;
using FluentAssertions;
using Jellyfin.Plugin.ExternalRatings.Core;
using Jellyfin.Plugin.ExternalRatings.Tests.Fakes;
using Xunit;

namespace Jellyfin.Plugin.ExternalRatings.Tests;

public class SelfWriteTrackerTests
{
    private static readonly Guid Id = Guid.Parse("22222222-0000-0000-0000-000000000001");

    private static readonly TimeSpan Window = TimeSpan.FromSeconds(30);

    [Fact]
    public void IsRecent_False_ForUnknownId()
    {
        var tracker = new SelfWriteTracker(new FakeClock(), Window);

        tracker.IsRecent(Id).Should().BeFalse();
    }

    [Fact]
    public void IsRecent_True_WithinWindow()
    {
        var clock = new FakeClock();
        var tracker = new SelfWriteTracker(clock, Window);

        tracker.MarkWritten(Id);
        clock.Advance(TimeSpan.FromSeconds(29));

        tracker.IsRecent(Id).Should().BeTrue();
    }

    [Fact]
    public void IsRecent_True_AtExactlyWindowEdge()
    {
        var clock = new FakeClock();
        var tracker = new SelfWriteTracker(clock, Window);

        tracker.MarkWritten(Id);
        clock.Advance(Window);

        tracker.IsRecent(Id).Should().BeTrue();
    }

    [Fact]
    public void IsRecent_False_AfterWindow()
    {
        var clock = new FakeClock();
        var tracker = new SelfWriteTracker(clock, Window);

        tracker.MarkWritten(Id);
        clock.Advance(Window + TimeSpan.FromSeconds(1));

        tracker.IsRecent(Id).Should().BeFalse();
    }

    [Fact]
    public void MarkWritten_Again_RefreshesWindow()
    {
        var clock = new FakeClock();
        var tracker = new SelfWriteTracker(clock, Window);

        tracker.MarkWritten(Id);
        clock.Advance(Window + TimeSpan.FromSeconds(1));
        tracker.IsRecent(Id).Should().BeFalse(); // expired (and evicted on read)

        tracker.MarkWritten(Id);
        tracker.IsRecent(Id).Should().BeTrue();
    }
}
