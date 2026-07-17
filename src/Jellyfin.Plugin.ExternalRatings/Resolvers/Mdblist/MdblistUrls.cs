using System.Globalization;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.ExternalRatings.Core;

namespace Jellyfin.Plugin.ExternalRatings.Resolvers.Mdblist;

/// <summary>
/// Builds mdblist request paths and masks the API key for logs/exceptions (spec §7.4, H9). The key
/// travels in the query string because mdblist offers no API-key header (only OAuth Bearer), so
/// every logged or thrown URL must go through <see cref="MaskApiKey"/>.
/// </summary>
internal static class MdblistUrls
{
    private static readonly Regex ApiKeyPattern = new(
        "(?i)(apikey=)[^&]*",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Maps a Jellyfin provider name to its mdblist path segment.</summary>
    /// <param name="jellyfinProvider">The Jellyfin provider name (for example <c>Tmdb</c>).</param>
    /// <returns>The lowercased mdblist provider segment.</returns>
    public static string MapProvider(string jellyfinProvider)
        => jellyfinProvider.ToLowerInvariant();

    /// <summary>Maps an item level to the mdblist media type.</summary>
    /// <param name="level">The item level.</param>
    /// <returns><c>movie</c>, <c>show</c>, or <see langword="null"/> for unsupported levels.</returns>
    public static string? MapType(ItemLevel level) => level switch
    {
        ItemLevel.Movie => "movie",
        ItemLevel.Series => "show",
        _ => null
    };

    /// <summary>Builds the single-item request path.</summary>
    /// <param name="provider">The mdblist provider segment.</param>
    /// <param name="type">The mdblist media type.</param>
    /// <param name="id">The media id.</param>
    /// <param name="apiKey">The API key.</param>
    /// <returns>A relative request path.</returns>
    public static string BuildSingle(string provider, string type, string id, string apiKey)
        => string.Format(CultureInfo.InvariantCulture, "{0}/{1}/{2}?apikey={3}", provider, type, id, apiKey);

    /// <summary>Builds the batch request path.</summary>
    /// <param name="provider">The mdblist provider segment.</param>
    /// <param name="type">The mdblist media type.</param>
    /// <param name="apiKey">The API key.</param>
    /// <returns>A relative request path.</returns>
    public static string BuildBatch(string provider, string type, string apiKey)
        => string.Format(CultureInfo.InvariantCulture, "{0}/{1}?apikey={2}", provider, type, apiKey);

    /// <summary>Masks the API-key value in a URL for safe logging (H9).</summary>
    /// <param name="url">The URL, possibly containing an <c>apikey</c> query parameter.</param>
    /// <returns>The URL with the key value replaced by <c>***</c>.</returns>
    public static string MaskApiKey(string url) => ApiKeyPattern.Replace(url, "$1***");
}
