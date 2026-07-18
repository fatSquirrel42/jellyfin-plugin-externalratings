using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.ExternalRatings.Resolvers.Mdblist;

/// <summary>
/// The mdblist <c>/user</c> account response. The daily budget counts live in the body (not in
/// <c>X-RateLimit-*</c> headers on this endpoint, per §7.3 / MdblistV1Notes); the cold-start budget
/// read adopts <see cref="ApiRequestsCount"/> as the authoritative used count for the day.
/// </summary>
internal sealed class MdblistUserResponse
{
    /// <summary>Gets or sets the daily request allowance.</summary>
    [JsonPropertyName("api_requests")]
    public int? ApiRequests { get; set; }

    /// <summary>Gets or sets the number of API requests already used today (authoritative).</summary>
    [JsonPropertyName("api_requests_count")]
    public int? ApiRequestsCount { get; set; }

    /// <summary>Gets or sets the rate limit.</summary>
    [JsonPropertyName("rate_limit")]
    public int? RateLimit { get; set; }

    /// <summary>Gets or sets the remaining rate-limit budget.</summary>
    [JsonPropertyName("rate_limit_remaining")]
    public int? RateLimitRemaining { get; set; }

    /// <summary>Gets or sets the Unix timestamp (UTC midnight) at which the budget resets.</summary>
    [JsonPropertyName("rate_limit_reset")]
    public long? RateLimitReset { get; set; }
}
