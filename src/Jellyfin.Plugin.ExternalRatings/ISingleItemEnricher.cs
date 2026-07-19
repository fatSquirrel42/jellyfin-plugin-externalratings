using System;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.ExternalRatings;

/// <summary>
/// The seam the realtime listener depends on (spec §15 step 9), implemented by
/// <see cref="RatingEnrichmentService"/>. Exposing it as an interface lets the listener be unit-tested
/// without constructing the real composition root, and keeps the facade's internal domain types off the
/// listener's dependency surface.
/// </summary>
internal interface ISingleItemEnricher
{
    /// <summary>Gets a value indicating whether the circuit breaker is currently open.</summary>
    bool IsCircuitOpen { get; }

    /// <summary>Enriches a single item's community rating from the external source.</summary>
    /// <param name="itemId">The Jellyfin item id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the item has been processed.</returns>
    Task EnrichItemAsync(Guid itemId, CancellationToken cancellationToken);

    /// <summary>Gets a value indicating whether the plugin wrote the given item within the self-write window.</summary>
    /// <param name="itemId">The item id.</param>
    /// <returns><see langword="true"/> if the item was written by the plugin recently.</returns>
    bool WasSelfWrite(Guid itemId);

    /// <summary>Flushes any pending realtime cache entries to disk (called by the listener on shutdown).</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes once the cache is flushed.</returns>
    Task FlushPendingAsync(CancellationToken cancellationToken);
}
