using System;
using FluentAssertions;
using Jellyfin.Plugin.ExternalRatings.Core;
using Jellyfin.Plugin.ExternalRatings.Tests.Fakes;
using Xunit;

namespace Jellyfin.Plugin.ExternalRatings.Tests;

public class CircuitBreakerTests
{
    private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(30);

    private static CircuitBreaker Create(FakeClock clock)
        => new(clock, failureThreshold: 5, cooldown: Cooldown);

    [Fact]
    public void StartsClosed_AndAllowsRequests()
    {
        var cb = Create(new FakeClock());

        cb.State.Should().Be(CircuitState.Closed);
        cb.IsOpen.Should().BeFalse();
        cb.AllowRequest().Should().BeTrue();
    }

    [Fact]
    public void FiveConsecutiveErrors_Opens()
    {
        var cb = Create(new FakeClock());

        for (var i = 0; i < 4; i++)
        {
            cb.RecordError();
            cb.State.Should().Be(CircuitState.Closed);
        }

        cb.RecordError();

        cb.State.Should().Be(CircuitState.Open);
        cb.IsOpen.Should().BeTrue();
        cb.AllowRequest().Should().BeFalse();
    }

    [Fact]
    public void SuccessResetsConsecutiveErrorCount()
    {
        var cb = Create(new FakeClock());

        cb.RecordError();
        cb.RecordError();
        cb.RecordError();
        cb.RecordError();
        cb.RecordSuccess();

        // Four more errors should not be enough to open (counter was reset).
        cb.RecordError();
        cb.RecordError();
        cb.RecordError();
        cb.RecordError();

        cb.State.Should().Be(CircuitState.Closed);
    }

    [Fact]
    public void SingleRateLimit_OpensImmediately()
    {
        var cb = Create(new FakeClock());

        cb.RecordRateLimited();

        cb.State.Should().Be(CircuitState.Open);
        cb.IsOpen.Should().BeTrue();
        cb.AllowRequest().Should().BeFalse();
    }

    [Fact]
    public void Open_BlocksWithinCooldown()
    {
        var clock = new FakeClock();
        var cb = Create(clock);
        cb.RecordRateLimited();

        clock.Advance(Cooldown - TimeSpan.FromSeconds(1));

        cb.State.Should().Be(CircuitState.Open);
        cb.IsOpen.Should().BeTrue();
        cb.AllowRequest().Should().BeFalse();
    }

    [Fact]
    public void Open_BecomesHalfOpen_AtCooldownBoundary()
    {
        var clock = new FakeClock();
        var cb = Create(clock);
        cb.RecordRateLimited();

        clock.Advance(Cooldown);

        cb.State.Should().Be(CircuitState.HalfOpen);
        cb.IsOpen.Should().BeFalse();
    }

    [Fact]
    public void HalfOpen_AllowsExactlyOneProbe()
    {
        var clock = new FakeClock();
        var cb = Create(clock);
        cb.RecordRateLimited();
        clock.Advance(Cooldown);

        cb.AllowRequest().Should().BeTrue();
        cb.AllowRequest().Should().BeFalse();
    }

    [Fact]
    public void HalfOpen_ProbeSuccess_Closes()
    {
        var clock = new FakeClock();
        var cb = Create(clock);
        cb.RecordRateLimited();
        clock.Advance(Cooldown);
        cb.AllowRequest();

        cb.RecordSuccess();

        cb.State.Should().Be(CircuitState.Closed);
        cb.AllowRequest().Should().BeTrue();
    }

    [Fact]
    public void HalfOpen_AbandonProbe_AllowsAnotherProbe()
    {
        // Regression: a cancelled probe (no RecordSuccess/RecordError) used to leave the breaker stuck
        // HalfOpen, refusing every future request until process restart. AbandonProbe releases the probe.
        var clock = new FakeClock();
        var cb = Create(clock);
        cb.RecordRateLimited();
        clock.Advance(Cooldown);

        cb.AllowRequest().Should().BeTrue();   // probe granted
        cb.AllowRequest().Should().BeFalse();  // second refused while the probe is outstanding

        cb.AbandonProbe();

        cb.State.Should().Be(CircuitState.HalfOpen);
        cb.AllowRequest().Should().BeTrue();   // a fresh probe is handed out; the breaker is not wedged
    }

    [Fact]
    public void AbandonProbe_WhenClosed_IsNoOp()
    {
        var cb = Create(new FakeClock());

        cb.AbandonProbe();

        cb.State.Should().Be(CircuitState.Closed);
        cb.AllowRequest().Should().BeTrue();
    }

    [Fact]
    public void HalfOpen_ProbeError_Reopens_AndRestartsCooldown()
    {
        var clock = new FakeClock();
        var cb = Create(clock);
        cb.RecordRateLimited();
        clock.Advance(Cooldown);
        cb.AllowRequest();

        cb.RecordError();

        cb.State.Should().Be(CircuitState.Open);
        cb.IsOpen.Should().BeTrue();

        // Cooldown restarted: still open just before the new cooldown elapses.
        clock.Advance(Cooldown - TimeSpan.FromSeconds(1));
        cb.State.Should().Be(CircuitState.Open);

        clock.Advance(TimeSpan.FromSeconds(1));
        cb.State.Should().Be(CircuitState.HalfOpen);
    }

    [Fact]
    public void HalfOpen_ProbeRateLimited_Reopens()
    {
        var clock = new FakeClock();
        var cb = Create(clock);
        cb.RecordRateLimited();
        clock.Advance(Cooldown);
        cb.AllowRequest();

        cb.RecordRateLimited();

        cb.State.Should().Be(CircuitState.Open);
    }

    [Fact]
    public void ChunkError_CountsAsSingleError()
    {
        var cb = Create(new FakeClock());

        // A chunk failure is a single RecordError; four of them stay closed.
        for (var i = 0; i < 4; i++)
        {
            cb.RecordError();
        }

        cb.State.Should().Be(CircuitState.Closed);
    }
}
