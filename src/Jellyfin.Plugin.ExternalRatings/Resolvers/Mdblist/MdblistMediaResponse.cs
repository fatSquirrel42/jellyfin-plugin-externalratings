using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.ExternalRatings.Resolvers.Mdblist;

/// <summary>
/// An mdblist media object (single-item response, or one element of a batch array). <c>ids</c> maps
/// provider names to heterogeneous values (string, number, or null), used to match a batch element
/// back to its requested input id.
/// </summary>
internal sealed class MdblistMediaResponse
{
    [JsonPropertyName("ids")]
    public Dictionary<string, JsonElement>? Ids { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("ratings")]
    public List<MdblistRating>? Ratings { get; set; }

    /// <summary>Returns the id for a provider as a string, or <see langword="null"/> if absent/null.</summary>
    /// <param name="provider">The mdblist provider segment (for example <c>tmdb</c>).</param>
    /// <returns>The id as a string, or <see langword="null"/>.</returns>
    public string? GetId(string provider)
    {
        if (Ids is null || !Ids.TryGetValue(provider, out var element))
        {
            return null;
        }

        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetRawText(),
            _ => null
        };
    }
}
