using System;
using System.Linq;
using FluentAssertions;
using Jellyfin.Plugin.ExternalRatings.Core;
using Xunit;

namespace Jellyfin.Plugin.ExternalRatings.Tests;

public class ItemChangeDebouncerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan Window = TimeSpan.FromSeconds(5);

    private static readonly Guid IdA = Guid.Parse("33333333-0000-0000-0000-00000000000a");
    private static readonly Guid IdB = Guid.Parse("33333333-0000-0000-0000-00000000000b");

    [Fact]
    public void CollectDue_Empty_BeforeWindowElapses()
    {
        var debouncer = new ItemChangeDebouncer(Window);
        debouncer.Enqueue(IdA, T0);

        debouncer.CollectDue(T0 + TimeSpan.FromSeconds(4)).Should().BeEmpty();
    }

    [Fact]
    public void CollectDue_ReturnsItem_AtWindowEdge()
    {
        var debouncer = new ItemChangeDebouncer(Window);
        debouncer.Enqueue(IdA, T0);

        debouncer.CollectDue(T0 + Window).Should().ContainSingle().Which.Should().Be(IdA);
    }

    [Fact]
    public void CollectDue_RemovesItem_SoItIsReturnedOnce()
    {
        var debouncer = new ItemChangeDebouncer(Window);
        debouncer.Enqueue(IdA, T0);

        debouncer.CollectDue(T0 + Window).Should().ContainSingle();
        debouncer.CollectDue(T0 + Window + Window).Should().BeEmpty();
    }

    [Fact]
    public void Enqueue_SameIdMultipleTimes_CollapsesToOne()
    {
        var debouncer = new ItemChangeDebouncer(Window);
        debouncer.Enqueue(IdA, T0);
        debouncer.Enqueue(IdA, T0);
        debouncer.Enqueue(IdA, T0);

        debouncer.CollectDue(T0 + Window).Should().ContainSingle().Which.Should().Be(IdA);
    }

    [Fact]
    public void ReEnqueue_PushesDueTimeOut()
    {
        var debouncer = new ItemChangeDebouncer(Window);
        debouncer.Enqueue(IdA, T0);
        debouncer.Enqueue(IdA, T0 + TimeSpan.FromSeconds(3)); // re-armed: due at T0+8

        debouncer.CollectDue(T0 + Window).Should().BeEmpty();                 // T0+5, not yet
        debouncer.CollectDue(T0 + TimeSpan.FromSeconds(8)).Should().ContainSingle().Which.Should().Be(IdA);
    }

    [Fact]
    public void CollectDue_ReturnsMultipleDueItems()
    {
        var debouncer = new ItemChangeDebouncer(Window);
        debouncer.Enqueue(IdA, T0);
        debouncer.Enqueue(IdB, T0);

        debouncer.CollectDue(T0 + Window).OrderBy(id => id).Should()
            .Equal(new[] { IdA, IdB }.OrderBy(id => id));
    }
}
