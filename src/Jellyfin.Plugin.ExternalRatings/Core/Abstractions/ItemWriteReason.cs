namespace Jellyfin.Plugin.ExternalRatings.Core.Abstractions;

/// <summary>
/// The reason recorded with a write, mapped by the Jellyfin adapter to a WriteUpdateReason (spec §6.6).
/// </summary>
internal enum ItemWriteReason
{
    /// <summary>The community rating was set to a resolved score.</summary>
    RatingUpdated,

    /// <summary>The community rating was cleared.</summary>
    RatingCleared,

    /// <summary>The community rating was restored to its original value (admin action).</summary>
    RatingRestored
}
