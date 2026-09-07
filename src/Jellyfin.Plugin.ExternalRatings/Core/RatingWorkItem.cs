using System.Collections.Generic;
using Jellyfin.Plugin.ExternalRatings.Core.Abstractions;

namespace Jellyfin.Plugin.ExternalRatings.Core;

/// <summary>
/// One unit of work for the pipeline: an item, its level, its provider ids, and the target source.
/// </summary>
/// <param name="Ref">The item reference.</param>
/// <param name="Level">The item level.</param>
/// <param name="ProviderIds">The available provider ids (case-insensitive keys).</param>
/// <param name="TargetSource">The external rating source to resolve.</param>
/// <param name="MemberInputIds">
/// For a Season, the input ids of its episodes; <see langword="null"/> at every other level. A
/// season has no external id of its own to resolve — IMDb has no season entity — so its score is
/// aggregated from its members, and only the host knows what they are.
/// </param>
internal sealed record RatingWorkItem(
    RatingItemRef Ref,
    ItemLevel Level,
    IReadOnlyDictionary<string, string> ProviderIds,
    string TargetSource,
    IReadOnlyList<string>? MemberInputIds = null);
