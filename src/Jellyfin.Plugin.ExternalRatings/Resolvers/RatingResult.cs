namespace Jellyfin.Plugin.ExternalRatings.Resolvers;

/// <summary>
/// The result of resolving a single rating request.
/// </summary>
/// <param name="Resolution">The outcome category.</param>
/// <param name="Score">The resolved score (only meaningful for <see cref="RatingResolution.Found"/>).</param>
/// <param name="ErrorDetail">Optional diagnostic detail for <see cref="RatingResolution.Error"/>.</param>
internal sealed record RatingResult(RatingResolution Resolution, float? Score, string? ErrorDetail = null)
{
    /// <summary>Creates a <see cref="RatingResolution.Found"/> result. No H14 enforcement here; the pipeline validates.</summary>
    /// <param name="score">The resolved score.</param>
    /// <returns>A found result.</returns>
    public static RatingResult ForScore(float score) => new(RatingResolution.Found, score);

    /// <summary>Creates a <see cref="RatingResolution.NoMatch"/> result.</summary>
    /// <returns>A no-match result.</returns>
    public static RatingResult NoMatch() => new(RatingResolution.NoMatch, null);

    /// <summary>Creates a <see cref="RatingResolution.NotSupportedForLevel"/> result.</summary>
    /// <returns>A not-supported result.</returns>
    public static RatingResult NotSupported() => new(RatingResolution.NotSupportedForLevel, null);

    /// <summary>Creates a <see cref="RatingResolution.Error"/> result.</summary>
    /// <param name="detail">Diagnostic detail (masked before logging where needed).</param>
    /// <returns>An error result.</returns>
    public static RatingResult ForError(string detail) => new(RatingResolution.Error, null, detail);
}
