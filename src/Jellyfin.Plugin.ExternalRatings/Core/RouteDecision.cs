using Jellyfin.Plugin.ExternalRatings.Resolvers;

namespace Jellyfin.Plugin.ExternalRatings.Core;

/// <summary>
/// Which resolver serves one work item, and with which input id.
/// </summary>
/// <param name="Resolver">
/// The resolver to use, or <see langword="null"/> when nothing can serve the item.
/// </param>
/// <param name="Selection">
/// The input id chosen for <paramref name="Resolver"/>, or <see langword="null"/> alongside a null
/// resolver. Carried here so the pipeline does not repeat the selection the router already made.
/// </param>
/// <param name="LevelServableBySource">
/// Whether *some* resolver could serve this item's (source, level) pair given a suitable id. It is
/// what separates "this source has no score at this level" from "the item has no usable id" — the
/// two land on the same write decision but are counted apart.
/// </param>
internal readonly record struct RouteDecision(
    IRatingResolver? Resolver,
    InputIdSelection? Selection,
    bool LevelServableBySource);
