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
        bool isRecentSelfWrite = false,
        bool isLocked = false)
        => ListenerGate.Evaluate(enabled, isScanRunning, breakerOpen, isAdd, reason, isRecentSelfWrite, isLocked);

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
        // Even an otherwise-eligible automatic update is dropped if it echoes our own write.
        Evaluate(reason: ItemChangeReason.MetadataDownload, isRecentSelfWrite: true)
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
    public void Skips_UpdateWithMetadataEdit_ManualEditsAreNotAutoTriggered()
    {
        // A manual user edit (MetadataEdit) must NOT trigger enrichment — otherwise the plugin would
        // instantly overwrite a value the user just set by hand. Only automatic updates are eligible.
        Evaluate(isAdd: false, reason: ItemChangeReason.MetadataEdit).Should().Be(GateDecision.SkipIneligibleReason);
    }

    [Fact]
    public void Processes_Add_EvenWithNonMetadataReason()
    {
        // A newly added item should be enriched even if the add carries a non-metadata reason.
        Evaluate(isAdd: true, reason: ItemChangeReason.Other).Should().Be(GateDecision.Process);
    }

    [Fact]
    public void Skips_WhenLocked_EvenForEligibleUpdate()
    {
        // A locked item is user-protected: the plugin must never overwrite its rating (spec §15 step 10),
        // even on an otherwise-eligible automatic metadata update.
        Evaluate(reason: ItemChangeReason.MetadataDownload, isLocked: true).Should().Be(GateDecision.SkipLocked);
    }

    [Fact]
    public void Skips_WhenLocked_EvenForAdd()
    {
        // Locking wins over the always-eligible add path too.
        Evaluate(isAdd: true, isLocked: true).Should().Be(GateDecision.SkipLocked);
    }

    [Fact]
    public void DisabledTakesPrecedence_OverOtherOpenGates()
    {
        Evaluate(enabled: false, isScanRunning: true, breakerOpen: true)
            .Should().Be(GateDecision.SkipDisabled);
    }
}
