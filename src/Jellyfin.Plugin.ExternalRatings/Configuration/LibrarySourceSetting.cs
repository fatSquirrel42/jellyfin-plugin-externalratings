using System;

namespace Jellyfin.Plugin.ExternalRatings.Configuration;

/// <summary>
/// A per-library rating-source override (spec §10.1). Maps a library (CollectionFolder id, as stored
/// in <see cref="PluginConfiguration.EnabledLibraries"/>) to the external rating source used for its
/// items, overriding <see cref="PluginConfiguration.RatingSource"/>. A list of these replaces a
/// <see cref="System.Collections.Generic.Dictionary{TKey, TValue}"/>, which Jellyfin's
/// <see cref="System.Xml.Serialization.XmlSerializer"/> cannot serialize (M6).
/// </summary>
public class LibrarySourceSetting
{
    /// <summary>
    /// Gets or sets the library (CollectionFolder) id this override applies to.
    /// </summary>
    public Guid LibraryId { get; set; }

    /// <summary>
    /// Gets or sets the external rating source for this library (for example <c>imdb</c>).
    /// </summary>
    public string Source { get; set; } = string.Empty;
}
