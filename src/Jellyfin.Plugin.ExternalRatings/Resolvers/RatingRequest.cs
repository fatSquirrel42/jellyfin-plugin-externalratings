using System.Collections.Generic;
using Jellyfin.Plugin.ExternalRatings.Core;

namespace Jellyfin.Plugin.ExternalRatings.Resolvers;

/// <summary>
/// A request to resolve one rating for one item using exactly one input id (spec §5.2).
/// </summary>
/// <param name="Level">The item level.</param>
/// <param name="InputProvider">The input provider key (e.g. Tmdb, Imdb, Tvdb).</param>
/// <param name="InputId">The provider-specific id value.</param>
/// <param name="TargetSource">The external rating source to resolve (e.g. myanimelist).</param>
/// <param name="MemberInputIds">
/// For a Season, the input ids of its episodes; <see langword="null"/> otherwise. Resolvers that
/// aggregate a season from its episodes read this instead of <paramref name="InputId"/>.
/// </param>
internal sealed record RatingRequest(
    ItemLevel Level,
    string InputProvider,
    string InputId,
    string TargetSource,
    IReadOnlyList<string>? MemberInputIds = null);
