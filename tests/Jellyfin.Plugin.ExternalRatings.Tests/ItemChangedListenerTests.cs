using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Jellyfin.Plugin.ExternalRatings;
using Jellyfin.Plugin.ExternalRatings.Tasks;
using Jellyfin.Plugin.ExternalRatings.Tests.Fakes;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.ExternalRatings.Tests;

public class ItemChangedListenerTests
{
    private static readonly Guid ItemId = Guid.Parse("66666666-0000-0000-0000-000000000001");

    private static readonly TimeSpan Window = TimeSpan.FromSeconds(3);

    // A tick interval long enough that the real timer never fires during a test; tests drive
    // DrainDueItems() manually after advancing the clock.
    private static readonly TimeSpan NoAutoTick = TimeSpan.FromHours(1);

    private sealed class Harness
    {
        public required ItemChangedListener Listener { get; init; }

        public required ILibraryManager LibraryManager { get; init; }

        public required ISingleItemEnricher Enricher { get; init; }

        public required FakeClock Clock { get; init; }

        public required Func<bool> IsEnabled { get; set; }
    }

    private static Harness Build(bool enabled = true, bool scanRunning = false, bool circuitOpen = false, bool selfWrite = false)
    {
        var clock = new FakeClock();
        var libraryManager = Substitute.For<ILibraryManager>();
        libraryManager.IsScanRunning.Returns(scanRunning);

        var enricher = Substitute.For<ISingleItemEnricher>();
        enricher.IsCircuitOpen.Returns(circuitOpen);
        enricher.WasSelfWrite(Arg.Any<Guid>()).Returns(selfWrite);
        enricher.EnrichItemAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        Func<bool> isEnabled = () => enabled;
        var listener = new ItemChangedListener(
            libraryManager, enricher, clock, NullLogger<ItemChangedListener>.Instance, isEnabled, Window, NoAutoTick);

        return new Harness
        {
            Listener = listener,
            LibraryManager = libraryManager,
            Enricher = enricher,
            Clock = clock,
            IsEnabled = isEnabled
        };
    }

    private static ItemChangeEventArgs Args(ItemUpdateType reason)
        => new() { Item = new Movie { Id = ItemId }, UpdateReason = reason };

    private static void RaiseUpdated(Harness h, ItemUpdateType reason)
        => h.LibraryManager.ItemUpdated += Raise.Event<EventHandler<ItemChangeEventArgs>>(new object(), Args(reason));

    private static void RaiseAdded(Harness h, ItemUpdateType reason)
        => h.LibraryManager.ItemAdded += Raise.Event<EventHandler<ItemChangeEventArgs>>(new object(), Args(reason));

    private static void DrainAfterWindow(Harness h)
    {
        h.Clock.Advance(Window + TimeSpan.FromSeconds(1));
        h.Listener.DrainDueItems();
    }

    [Fact]
    public async Task EligibleUpdate_EnrichesItem()
    {
        var h = Build();
        await h.Listener.StartAsync(CancellationToken.None);

        RaiseUpdated(h, ItemUpdateType.MetadataDownload);
        DrainAfterWindow(h);

        await h.Enricher.Received(1).EnrichItemAsync(ItemId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Add_EnrichesEvenWithNonMetadataReason()
    {
        var h = Build();
        await h.Listener.StartAsync(CancellationToken.None);

        RaiseAdded(h, ItemUpdateType.None);
        DrainAfterWindow(h);

        await h.Enricher.Received(1).EnrichItemAsync(ItemId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DisabledListener_DoesNotEnrich()
    {
        var h = Build(enabled: false);
        await h.Listener.StartAsync(CancellationToken.None);

        RaiseUpdated(h, ItemUpdateType.MetadataDownload);
        DrainAfterWindow(h);

        await h.Enricher.DidNotReceive().EnrichItemAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ScanRunning_DoesNotEnrich()
    {
        var h = Build(scanRunning: true);
        await h.Listener.StartAsync(CancellationToken.None);

        RaiseUpdated(h, ItemUpdateType.MetadataDownload);
        DrainAfterWindow(h);

        await h.Enricher.DidNotReceive().EnrichItemAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IneligibleUpdateReason_DoesNotEnrich()
    {
        var h = Build();
        await h.Listener.StartAsync(CancellationToken.None);

        RaiseUpdated(h, ItemUpdateType.None);
        DrainAfterWindow(h);

        await h.Enricher.DidNotReceive().EnrichItemAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SelfWrite_DoesNotEnrich()
    {
        var h = Build(selfWrite: true);
        await h.Listener.StartAsync(CancellationToken.None);

        RaiseUpdated(h, ItemUpdateType.MetadataEdit);
        DrainAfterWindow(h);

        await h.Enricher.DidNotReceive().EnrichItemAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Burst_ForSameId_CoalescesToOneEnrichment()
    {
        var h = Build();
        await h.Listener.StartAsync(CancellationToken.None);

        RaiseUpdated(h, ItemUpdateType.MetadataDownload);
        RaiseUpdated(h, ItemUpdateType.MetadataDownload);
        RaiseUpdated(h, ItemUpdateType.MetadataDownload);
        DrainAfterWindow(h);

        await h.Enricher.Received(1).EnrichItemAsync(ItemId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AfterStop_EventsAreIgnored()
    {
        var h = Build();
        await h.Listener.StartAsync(CancellationToken.None);
        await h.Listener.StopAsync(CancellationToken.None);

        RaiseUpdated(h, ItemUpdateType.MetadataDownload);
        h.Clock.Advance(Window + TimeSpan.FromSeconds(1));
        h.Listener.DrainDueItems();

        await h.Enricher.DidNotReceive().EnrichItemAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnricherException_DoesNotPropagate()
    {
        var h = Build();
        h.Enricher.EnrichItemAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException(new InvalidOperationException("boom")));
        await h.Listener.StartAsync(CancellationToken.None);

        RaiseUpdated(h, ItemUpdateType.MetadataDownload);

        // The drain fire-and-forgets the enrichment; the exception must be swallowed, not thrown here.
        var drain = () => DrainAfterWindow(h);
        drain.Should().NotThrow();
    }
}
