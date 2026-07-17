using Jellyfin.Plugin.ExternalRatings.Core;

namespace Jellyfin.Plugin.ExternalRatings.Resolvers;

/// <summary>
/// A request to resolve one rating for one item using exactly one input id (spec §5.2).
/// </summary>
/// <param name="Level">The item level.</param>
/// <param name="InputProvider">The input provider key (e.g. Tmdb, Imdb, Tvdb).</param>
/// <param name="InputId">The provider-specific id value.</param>
/// <param name="TargetSource">The external rating source to resolve (e.g. myanimelist).</param>
internal sealed record RatingRequest(ItemLevel Level, string InputProvider, string InputId, string TargetSource);
