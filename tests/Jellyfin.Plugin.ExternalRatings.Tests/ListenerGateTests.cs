using FluentAssertions;
using Jellyfin.Plugin.ExternalRatings.Core;
using Xunit;

namespace Jellyfin.Plugin.ExternalRatings.Tests;

public class ListenerGateTests
{
    // Baseline: an eligible metadata update with every gate open.
    private static GateDecision Evaluate(
        bool enabled = true,
        bool isScanRunning = false,
        bool breakerOpen = false,
        bool isAdd = false,
        ItemChangeReason reason = ItemChangeReason.MetadataDownload,
        bool isRecentSelfWrite = false)
        => ListenerGate.Evaluate(enabled, isScanRunning, breakerOpen, isAdd, reason, isRecentSelfWrite);

    [Fact]
    public void Processes_EligibleMetadataUpdate_WhenAllGatesOpen()
    {
        Evaluate().Should().Be(GateDecision.Process);
    }

    [Fact]
    public void Skips_WhenDisabled()
    {
        Evaluate(enabled: false).Should().Be(GateDecision.SkipDisabled);
    }

    [Fact]
    public void Skips_WhenScanRunning()
    {
        Evaluate(isScanRunning: true).Should().Be(GateDecision.SkipScanRunning);
    }

    [Fact]
    public void Skips_WhenBreakerOpen()
    {
        Evaluate(breakerOpen: true).Should().Be(GateDecision.SkipCircuitOpen);
    }

    [Fact]
    public void Skips_WhenRecentSelfWrite()
    {
        // Plan B robustness: even a metadata-reason update is dropped if it echoes our own write.
        Evaluate(reason: ItemChangeReason.MetadataEdit, isRecentSelfWrite: true)
            .Should().Be(GateDecision.SkipRecentSelfWrite);
    }

    // Plan A: our own write carries None -> maps to Other -> ignored (no self-trigger).
    [Fact]
    public void Skips_UpdateWithReasonOther()
    {
        Evaluate(isAdd: false, reason: ItemChangeReason.Other).Should().Be(GateDecision.SkipIneligibleReason);
    }

    [Fact]
    public void Skips_UpdateWithReasonImageUpdate()
    {
        Evaluate(isAdd: false, reason: ItemChangeReason.ImageUpdate).Should().Be(GateDecision.SkipIneligibleReason);
    }

    [Fact]
    public void Processes_UpdateWithMetadataImport()
    {
        Evaluate(isAdd: false, reason: ItemChangeReason.MetadataImport).Should().Be(GateDecision.Process);
    }

    [Fact]
    public void Processes_UpdateWithMetadataDownload()
    {
        Evaluate(isAdd: false, reason: ItemChangeReason.MetadataDownload).Should().Be(GateDecision.Process);
    }

    [Fact]
    public void Processes_UpdateWithMetadataEdit()
    {
        Evaluate(isAdd: false, reason: ItemChangeReason.MetadataEdit).Should().Be(GateDecision.Process);
    }

    [Fact]
    public void Processes_Add_EvenWithNonMetadataReason()
    {
        // A newly added item should be enriched even if the add carries a non-metadata reason.
        Evaluate(isAdd: true, reason: ItemChangeReason.Other).Should().Be(GateDecision.Process);
    }

    [Fact]
    public void DisabledTakesPrecedence_OverOtherOpenGates()
    {
        Evaluate(enabled: false, isScanRunning: true, breakerOpen: true)
            .Should().Be(GateDecision.SkipDisabled);
    }
}
