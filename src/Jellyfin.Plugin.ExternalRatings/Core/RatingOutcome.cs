namespace Jellyfin.Plugin.ExternalRatings.Core;

/// <summary>
/// The terminal outcome of processing one item through the pipeline (spec §6).
/// </summary>
internal enum RatingOutcome
{
    /// <summary>A new score was written.</summary>
    Updated,

    /// <summary>A score was found but the change was within epsilon, so nothing was written.</summary>
    SkippedNoChange,

    /// <summary>A write was due but suppressed because DryRun is on.</summary>
    SkippedDryRun,

    /// <summary>The community rating was cleared (opt-in behavior).</summary>
    Cleared,

    /// <summary>Authoritative no-match; the existing value was left in place.</summary>
    NoMatch,

    /// <summary>The level/provider is not supported.</summary>
    NotSupported,

    /// <summary>No usable input id was present.</summary>
    SkippedNoId,

    /// <summary>A transient error occurred; nothing was written and the item should be retried.</summary>
    Error,

    /// <summary>The circuit breaker was open; the resolver was not called.</summary>
    CircuitOpen
}
