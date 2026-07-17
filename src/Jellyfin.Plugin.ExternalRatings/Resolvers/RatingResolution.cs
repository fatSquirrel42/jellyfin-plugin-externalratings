namespace Jellyfin.Plugin.ExternalRatings.Resolvers;

/// <summary>
/// The outcome category of a rating resolution (spec §5.2).
/// </summary>
internal enum RatingResolution
{
    /// <summary>An authoritative score was found.</summary>
    Found,

    /// <summary>Authoritative 2xx answer without a matching entry.</summary>
    NoMatch,

    /// <summary>The resolver does not support the requested item level.</summary>
    NotSupportedForLevel,

    /// <summary>A transient failure (timeout, 429, 5xx, network, parsing).</summary>
    Error
}
