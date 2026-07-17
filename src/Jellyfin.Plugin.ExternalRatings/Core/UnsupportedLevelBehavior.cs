namespace Jellyfin.Plugin.ExternalRatings.Core;

/// <summary>
/// What to do when the level/provider is unsupported or no id is present (spec §10.1).
/// </summary>
internal enum UnsupportedLevelBehavior
{
    /// <summary>Skip the item, leaving any existing rating untouched (default).</summary>
    Skip,

    /// <summary>Clear the community rating (opt-in, always backed up first).</summary>
    ClearField
}
