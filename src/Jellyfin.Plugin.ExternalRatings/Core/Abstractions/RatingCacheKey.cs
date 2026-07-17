using Jellyfin.Plugin.ExternalRatings.Core;

namespace Jellyfin.Plugin.ExternalRatings.Core.Abstractions;

/// <summary>
/// The cache key: resolver + targetSource + inputProvider + inputId + level (spec §7.1).
/// </summary>
/// <param name="Resolver">The resolver key.</param>
/// <param name="TargetSource">The external rating source.</param>
/// <param name="InputProvider">The input provider key.</param>
/// <param name="InputId">The input id value.</param>
/// <param name="Level">The item level.</param>
internal readonly record struct RatingCacheKey(
    string Resolver,
    string TargetSource,
    string InputProvider,
    string InputId,
    ItemLevel Level);
