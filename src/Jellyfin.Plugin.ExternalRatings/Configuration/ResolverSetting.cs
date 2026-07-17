namespace Jellyfin.Plugin.ExternalRatings.Configuration;

/// <summary>
/// A single resolver configuration entry (spec §10.1). A list of these replaces a
/// <see cref="System.Collections.Generic.Dictionary{TKey, TValue}"/>, which Jellyfin's
/// <see cref="System.Xml.Serialization.XmlSerializer"/> cannot serialize (M6).
/// </summary>
public class ResolverSetting
{
    /// <summary>
    /// Gets or sets the setting key (for example <c>mdblist.apiKey</c>).
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the setting value.
    /// </summary>
    public string Value { get; set; } = string.Empty;
}
