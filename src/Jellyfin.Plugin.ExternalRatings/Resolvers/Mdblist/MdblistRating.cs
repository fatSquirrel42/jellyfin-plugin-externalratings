using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.ExternalRatings.Resolvers.Mdblist;

/// <summary>
/// One entry of an mdblist media object's <c>ratings</c> array. Only <c>source</c> and the native
/// <c>value</c> are consumed; <c>value</c> may be <see langword="null"/> when the source has no score.
/// </summary>
internal sealed class MdblistRating
{
    [JsonPropertyName("source")]
    public string? Source { get; set; }

    [JsonPropertyName("value")]
    public double? Value { get; set; }
}
