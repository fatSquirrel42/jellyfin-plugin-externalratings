using System.Collections.Generic;
using Jellyfin.Plugin.ExternalRatings.Resolvers;

namespace Jellyfin.Plugin.ExternalRatings.Api;

/// <summary>
/// What one selectable resolver can do, so the config page can offer resolvers and preview their
/// levels and sources without saving and reloading first.
/// </summary>
public class ResolverCapabilities
{
    /// <summary>Gets the stable configuration key (for example <c>mdblist</c>).</summary>
    public string Key { get; init; } = string.Empty;

    /// <summary>Gets the human-readable name shown in the resolver dropdown.</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>Gets the item levels this resolver can produce ratings for.</summary>
    public IReadOnlyList<string> Levels { get; init; } = new List<string>();

    /// <summary>Gets the external rating sources this resolver can return.</summary>
    public IReadOnlyList<RatingSourceInfo> Sources { get; init; } = new List<RatingSourceInfo>();

    /// <summary>Gets a value indicating whether this resolver needs the mdblist API key.</summary>
    public bool RequiresApiKey { get; init; }
}
