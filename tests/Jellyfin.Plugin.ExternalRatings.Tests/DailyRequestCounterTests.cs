using System;
using FluentAssertions;
using Jellyfin.Plugin.ExternalRatings.Persistence;
using Jellyfin.Plugin.ExternalRatings.Tests.Fakes;
using Xunit;

namespace Jellyfin.Plugin.ExternalRatings.Tests;

public class DailyRequestCounterTests
{
    private static FakeClock ClockAt(int year, int month, int day, int hour)
        => new(new DateTimeOffset(year, month, day, hour, 0, 0, TimeSpan.Zero));

    [Fact]
    public void RecordRequest_Increments()
    {
        var counter = new DailyRequestCounter(new FakeClock());

        counter.RecordRequest();
        counter.RecordRequest();
        counter.RecordRequest();

        counter.Count.Should().Be(3);
    }

    [Fact]
    public void ColdStart_AdoptsServerValue()
    {
        var counter = new DailyRequestCounter(new FakeClock());

        counter.InitializeFromColdStart(100);

        counter.Count.Should().Be(100);
    }

    [Fact]
    public void ColdStart_ThenRecord_Accumulates()
    {
        var counter = new DailyRequestCounter(new FakeClock());

        counter.InitializeFromColdStart(100);
        counter.RecordRequest();
        counter.RecordRequest();

        counter.Count.Should().Be(102);
    }

    [Fact]
    public void HeaderSync_ServerHigher_Adopts()
    {
        var counter = new DailyRequestCounter(new FakeClock());
        counter.InitializeFromColdStart(100);

        counter.SyncFromHeader(150);

        counter.Count.Should().Be(150);
    }

    [Fact]
    public void HeaderSync_ServerLower_DetectsResetAndAdopts()
    {
        var counter = new DailyRequestCounter(new FakeClock());
        counter.InitializeFromColdStart(500);

        // Server count dropped below the local value ⇒ the server rolled over to a new day.
        counter.SyncFromHeader(10);

        counter.Count.Should().Be(10);
    }

    [Fact]
    public void ClockRollover_ResetsAtUtcMidnight()
    {
        var clock = ClockAt(2026, 1, 1, 10);
        var counter = new DailyRequestCounter(clock);
        counter.RecordRequest();
        counter.RecordRequest();

        clock.Advance(TimeSpan.FromDays(1));
        counter.RecordRequest();

        counter.Count.Should().Be(1);
    }

    [Fact]
    public void NeverStuck_ResetsAfterDayChange_WithoutServerSignal()
    {
        var clock = ClockAt(2026, 1, 1, 10);
        var counter = new DailyRequestCounter(clock);
        counter.InitializeFromColdStart(1000);

        clock.Advance(TimeSpan.FromDays(1));

        counter.Count.Should().Be(0);
        counter.IsExhausted(1000).Should().BeFalse();
    }

    [Fact]
    public void IsExhausted_TrueAtLimit()
    {
        var counter = new DailyRequestCounter(new FakeClock());
        counter.InitializeFromColdStart(1000);

        counter.IsExhausted(1000).Should().BeTrue();
    }

    [Fact]
    public void IsExhausted_FalseBelowLimit()
    {
        var counter = new DailyRequestCounter(new FakeClock());
        counter.InitializeFromColdStart(999);

        counter.IsExhausted(1000).Should().BeFalse();
    }

    [Fact]
    public void UserResetFallback_ColdStartAdoptsLowerServerValue()
    {
        // Simulates a persisted counter from yesterday; the /user cold-start read shows a reset.
        var counter = new DailyRequestCounter(new FakeClock());
        counter.InitializeFromColdStart(1000);

        counter.InitializeFromColdStart(3);

        counter.Count.Should().Be(3);
    }
}
