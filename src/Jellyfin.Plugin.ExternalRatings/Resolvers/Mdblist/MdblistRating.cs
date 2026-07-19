using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.ExternalRatings.Resolvers.Mdblist;

/// <summary>
/// One entry of an mdblist media object's <c>ratings</c> array. <c>source</c> and mdblist's
/// unified <c>score</c> (0–100 across every source) are consumed; the native <c>value</c> is
/// retained for reference but not used for enrichment. Both <c>value</c> and <c>score</c> may be
/// <see langword="null"/> when the source has no rating.
/// </summary>
internal sealed class MdblistRating
{
    [JsonPropertyName("source")]
    public string? Source { get; set; }

    [JsonPropertyName("value")]
    public double? Value { get; set; }

    [JsonPropertyName("score")]
    public double? Score { get; set; }
}
