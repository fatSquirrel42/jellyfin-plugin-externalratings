namespace Jellyfin.Plugin.ExternalRatings.Core;

/// <summary>
/// The Jellyfin item hierarchy level an enrichment operation targets.
/// </summary>
internal enum ItemLevel
{
    /// <summary>A movie.</summary>
    Movie,

    /// <summary>A series (show).</summary>
    Series,

    /// <summary>A season within a series.</summary>
    Season,

    /// <summary>An episode within a season.</summary>
    Episode
}
