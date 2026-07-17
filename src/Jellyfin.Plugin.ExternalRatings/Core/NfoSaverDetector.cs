using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.ExternalRatings.Core;

/// <summary>
/// Detects which enabled libraries have the NFO metadata saver active (M7). When active, a rating
/// write can trigger an NFO write in the media folder, so the config page warns the user
/// (spec §6.6, §10.2). The NFO saver reports the name <c>"Nfo"</c> in <c>LibraryOptions.MetadataSavers</c>.
/// </summary>
internal static class NfoSaverDetector
{
    private const string NfoSaverName = "Nfo";

    /// <summary>Returns the display names of enabled libraries with an active NFO saver.</summary>
    /// <param name="libraries">The projected library options.</param>
    /// <param name="enabledLibraries">The ids of the libraries the plugin is enabled for.</param>
    /// <returns>The affected library names, in input order.</returns>
    /// <remarks>
    /// The NFO saver is considered active only when the name <c>"Nfo"</c> is explicitly present in
    /// <c>MetadataSavers</c> (case-insensitive) and local-metadata writing is enabled; a null or
    /// empty saver list is treated as inactive. (The precise meaning of a null saver list is a
    /// live-host verification point.)
    /// </remarks>
    public static IReadOnlyList<string> Detect(
        IEnumerable<LibraryNfoSnapshot> libraries,
        IReadOnlyCollection<Guid> enabledLibraries)
    {
        ArgumentNullException.ThrowIfNull(libraries);
        ArgumentNullException.ThrowIfNull(enabledLibraries);

        var affected = new List<string>();
        foreach (var library in libraries)
        {
            if (!enabledLibraries.Contains(library.Id))
            {
                continue;
            }

            if (library.SaveLocalMetadata && HasNfoSaver(library.MetadataSavers))
            {
                affected.Add(library.Name);
            }
        }

        return affected;
    }

    private static bool HasNfoSaver(IReadOnlyList<string>? savers)
    {
        if (savers is null)
        {
            return false;
        }

        foreach (var saver in savers)
        {
            if (string.Equals(saver, NfoSaverName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
