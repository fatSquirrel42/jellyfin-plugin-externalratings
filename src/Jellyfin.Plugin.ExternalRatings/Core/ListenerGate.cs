namespace Jellyfin.Plugin.ExternalRatings.Core;

/// <summary>
/// Pure policy deciding whether a library-change event should trigger single-item enrichment
/// (spec §15 step 9). It has no host dependencies — the listener shell maps host state onto the
/// primitive inputs — so the whole gating policy is unit-testable.
/// </summary>
internal static class ListenerGate
{
    /// <summary>Evaluates the gate for one change event.</summary>
    /// <param name="enabled">Whether the realtime listener is enabled in configuration.</param>
    /// <param name="isScanRunning">Whether a library scan is currently running.</param>
    /// <param name="breakerOpen">Whether the circuit breaker is open.</param>
    /// <param name="isAdd">Whether the event is an item-add (new item) rather than an update.</param>
    /// <param name="reason">The mapped change reason (ignored for adds).</param>
    /// <param name="isRecentSelfWrite">Whether the item was written by the plugin itself very recently.</param>
    /// <returns>The gate decision.</returns>
    public static GateDecision Evaluate(
        bool enabled,
        bool isScanRunning,
        bool breakerOpen,
        bool isAdd,
        ItemChangeReason reason,
        bool isRecentSelfWrite)
    {
        if (!enabled)
        {
            return GateDecision.SkipDisabled;
        }

        if (isScanRunning)
        {
            return GateDecision.SkipScanRunning;
        }

        if (breakerOpen)
        {
            return GateDecision.SkipCircuitOpen;
        }

        if (isRecentSelfWrite)
        {
            return GateDecision.SkipRecentSelfWrite;
        }

        // Adds are always eligible (a new item is worth enriching regardless of the add's reason);
        // updates must carry an *automatic* metadata reason. MetadataEdit (a manual user edit) is
        // deliberately excluded so the plugin never overwrites a value the user just set by hand — and
        // as a side effect our own write (which carries MetadataEdit) is filtered here too, on top of
        // the self-write guard above.
        if (!isAdd && !IsAutomaticMetadataReason(reason))
        {
            return GateDecision.SkipIneligibleReason;
        }

        return GateDecision.Process;
    }

    private static bool IsAutomaticMetadataReason(ItemChangeReason reason) => reason switch
    {
        ItemChangeReason.MetadataImport => true,
        ItemChangeReason.MetadataDownload => true,
        _ => false
    };
}
