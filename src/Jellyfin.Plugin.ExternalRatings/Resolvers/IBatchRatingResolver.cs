using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ExternalRatings.Core;

namespace Jellyfin.Plugin.ExternalRatings.Resolvers;

/// <summary>
/// Optional capability: resolves many ids of one (level × provider) in a single call (spec §5.2).
/// A whole-chunk failure surfaces as a single error (counts as one circuit-breaker failure, §7.5).
/// </summary>
internal interface IBatchRatingResolver : IRatingResolver
{
    /// <summary>Gets the maximum number of ids per batch call (mdblist: 200).</summary>
    int MaxBatchSize { get; }

    /// <summary>Resolves a batch of ids for one level and one input provider.</summary>
    /// <param name="level">The item level.</param>
    /// <param name="inputProvider">The input provider key.</param>
    /// <param name="inputIds">The input ids (at most <see cref="MaxBatchSize"/>).</param>
    /// <param name="targetSource">The external rating source.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A map from input id to its result. Missing ids are treated as <see cref="RatingResolution.NoMatch"/>.</returns>
    Task<IReadOnlyDictionary<string, RatingResult>> ResolveBatchAsync(
        ItemLevel level,
        string inputProvider,
        IReadOnlyCollection<string> inputIds,
        string targetSource,
        CancellationToken cancellationToken);
}
