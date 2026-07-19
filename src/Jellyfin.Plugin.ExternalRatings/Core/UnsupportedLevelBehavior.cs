namespace Jellyfin.Plugin.ExternalRatings.Core;

/// <summary>
/// What to do when the level/provider is unsupported or no id is present (spec §10.1).
/// </summary>
internal enum UnsupportedLevelBehavior
{
    /// <summary>Leave any existing community rating untouched (skip the item).</summary>
    LeaveExisting,

    /// <summary>Clear the community rating (opt-in, always backed up first).</summary>
    ClearField
}
