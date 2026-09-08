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
/// For a Season, one entry per episode it holds — the episode's input id, or an empty string
/// where the episode has none. <see langword="null"/> at every other level. Resolvers that
/// aggregate a season read this instead of <paramref name="InputId"/>, and the *count* is the
/// season's episode total, which is what makes an unmatched episode fail completeness rather
/// than silently shrink the set.
/// </param>
internal sealed record RatingRequest(
    ItemLevel Level,
    string InputProvider,
    string InputId,
    string TargetSource,
    IReadOnlyList<string>? MemberInputIds = null);
