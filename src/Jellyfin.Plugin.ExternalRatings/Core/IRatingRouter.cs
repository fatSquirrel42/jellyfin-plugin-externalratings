using System.Collections.Generic;
using Jellyfin.Plugin.ExternalRatings.Resolvers;

namespace Jellyfin.Plugin.ExternalRatings.Core;

/// <summary>
/// Chooses which resolver serves an item. The rating source is the user's choice; which backend
/// reaches it is not.
/// </summary>
internal interface IRatingRouter
{
    /// <summary>Routes one work item.</summary>
    /// <param name="item">The work item.</param>
    /// <returns>The decision; its resolver is <see langword="null"/> when nothing can serve the item.</returns>
    RouteDecision Route(RatingWorkItem item);

    /// <summary>
    /// The batch-capable candidate, if any. Phase-0 prefetch exists only to amortise HTTP
    /// round-trips, so a locally-answering resolver is deliberately not one.
    /// </summary>
    /// <returns>The batch resolver, or <see langword="null"/>.</returns>
    IBatchRatingResolver? TryGetBatchResolver();

    /// <summary>The levels some resolver can serve for a source, in canonical order.</summary>
    /// <param name="targetSource">The rating source key.</param>
    /// <returns>The servable levels, empty when no resolver offers the source at all.</returns>
    IReadOnlyList<ItemLevel> ServableLevels(string targetSource);
}
