namespace Jellyfin.Plugin.ExternalRatings.Core;

/// <summary>
/// Host-agnostic mirror of the item-change reasons the realtime listener distinguishes (spec §15 step 9).
/// The listener shell maps MediaBrowser's <c>ItemUpdateType</c> onto this enum so the pure
/// <see cref="ListenerGate"/> policy never depends on host types (mirrors <c>ItemWriteReason</c> vs
/// <c>ItemUpdateType</c>). Any host value that is not a metadata-bearing change maps to
/// <see cref="Other"/> and is treated as ineligible.
/// </summary>
internal enum ItemChangeReason
{
    /// <summary>A trivial or non-metadata change (maps from <c>None</c>, and any unrecognized value).</summary>
    Other,

    /// <summary>An image-only change (maps from <c>ImageUpdate</c>); never enriched.</summary>
    ImageUpdate,

    /// <summary>Metadata was imported, for example from an NFO file or a scan (maps from <c>MetadataImport</c>).</summary>
    MetadataImport,

    /// <summary>Metadata was downloaded from a provider (maps from <c>MetadataDownload</c>).</summary>
    MetadataDownload,

    /// <summary>Metadata was edited, for example by a user or another plugin (maps from <c>MetadataEdit</c>).</summary>
    MetadataEdit
}
