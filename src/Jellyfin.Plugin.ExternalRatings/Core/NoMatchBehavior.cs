namespace Jellyfin.Plugin.ExternalRatings.Core;

/// <summary>
/// What to do on an authoritative no-match (spec §10.1).
/// </summary>
internal enum NoMatchBehavior
{
    /// <summary>Leave any existing community rating untouched (default).</summary>
    LeaveExisting,

    /// <summary>Clear the community rating (opt-in, always backed up first).</summary>
    ClearField
}
