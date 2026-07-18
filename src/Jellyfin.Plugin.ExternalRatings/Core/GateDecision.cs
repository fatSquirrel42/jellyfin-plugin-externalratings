namespace Jellyfin.Plugin.ExternalRatings.Core;

/// <summary>
/// The outcome of <see cref="ListenerGate"/> for a single library-change event: either process the item
/// or skip it with a specific reason (kept distinct so the listener can log <em>why</em> an event was
/// dropped, which matters for the live self-write/loop verification).
/// </summary>
internal enum GateDecision
{
    /// <summary>The event is eligible; enrich the item.</summary>
    Process,

    /// <summary>The realtime listener is disabled in configuration.</summary>
    SkipDisabled,

    /// <summary>A library scan is running; the post-scan full pass covers this item.</summary>
    SkipScanRunning,

    /// <summary>The circuit breaker is open (listener paused).</summary>
    SkipCircuitOpen,

    /// <summary>The change reason is not a metadata-bearing update.</summary>
    SkipIneligibleReason,

    /// <summary>The item was written by the plugin itself very recently (self-write echo).</summary>
    SkipRecentSelfWrite,

    /// <summary>The item is locked (<see cref="MediaBrowser.Controller.Entities.BaseItem.IsLocked"/>); the user protects its metadata, so the plugin must not overwrite it (spec §15 step 10).</summary>
    SkipLocked
}
