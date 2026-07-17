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
internal sealed record RatingWorkItem(
    RatingItemRef Ref,
    ItemLevel Level,
    IReadOnlyDictionary<string, string> ProviderIds,
    string TargetSource);
