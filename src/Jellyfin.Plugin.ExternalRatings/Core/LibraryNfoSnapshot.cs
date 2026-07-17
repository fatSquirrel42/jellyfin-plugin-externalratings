using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.ExternalRatings.Core;

/// <summary>
/// A host-independent projection of a library's NFO-relevant options (M7). The thin API shell maps
/// Jellyfin's <c>VirtualFolderInfo</c>/<c>LibraryOptions</c> onto this type so the detection logic
/// stays in the testable core without pulling <c>MediaBrowser.Model</c> into unit tests.
/// </summary>
/// <param name="Id">The library id.</param>
/// <param name="Name">The library display name.</param>
/// <param name="SaveLocalMetadata">Whether the library writes local metadata.</param>
/// <param name="MetadataSavers">The enabled metadata saver names (may be <see langword="null"/>).</param>
internal readonly record struct LibraryNfoSnapshot(
    Guid Id,
    string Name,
    bool SaveLocalMetadata,
    IReadOnlyList<string>? MetadataSavers);
