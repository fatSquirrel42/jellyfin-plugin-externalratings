using System.Collections.Generic;

namespace Jellyfin.Plugin.ExternalRatings.Api;

/// <summary>
/// One selectable rating source and what it can reach. The config page renders its source dropdown,
/// its scale hint and its level coverage from this.
/// </summary>
/// <remarks>
/// Which backend serves a source is deliberately absent: the user picks a source, and the plugin
/// routes per item from (source × level × available ids). See <c>Core/RatingRouter</c>.
/// </remarks>
public class SourceCapabilities
{
    /// <summary>Gets the source key written to the configuration (for example <c>imdb</c>).</summary>
    public string Key { get; init; } = string.Empty;

    /// <summary>Gets the human-readable name shown in the dropdown.</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>Gets the source's native scale, for the conversion hint (for example <c>0–100</c>).</summary>
    public string Scale { get; init; } = string.Empty;

    /// <summary>Gets a value indicating whether the native scale differs from Jellyfin's 0–10.</summary>
    public bool Rescaled { get; init; }

    /// <summary>
    /// Gets the item levels some resolver can serve this source at. A level missing here is not
    /// merely skipped for a library on this source — with the shipped <c>ClearField</c> default its
    /// rating is emptied.
    /// </summary>
    public IReadOnlyList<string> Levels { get; init; } = new List<string>();

    /// <summary>Gets a value indicating whether reaching this source needs the mdblist API key.</summary>
    public bool RequiresApiKey { get; init; }
}
