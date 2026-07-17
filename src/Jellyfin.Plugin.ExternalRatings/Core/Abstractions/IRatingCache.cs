using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.ExternalRatings.Core.Abstractions;

/// <summary>
/// The persistent, write-behind resolution cache (spec §7.1). Expiry is evaluated against <see cref="IClock"/>.
/// </summary>
internal interface IRatingCache
{
    /// <summary>Loads the persisted state into memory. A corrupt or version-mismatched store starts empty.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes once loading is done.</returns>
    Task InitializeAsync(CancellationToken cancellationToken);

    /// <summary>Attempts to read a non-expired entry.</summary>
    /// <param name="key">The cache key.</param>
    /// <param name="entry">The entry, if present and unexpired.</param>
    /// <returns><see langword="true"/> if a live entry exists.</returns>
    bool TryGet(RatingCacheKey key, out RatingCacheEntry entry);

    /// <summary>Stores (or replaces) an entry in memory (write-behind).</summary>
    /// <param name="key">The cache key.</param>
    /// <param name="entry">The entry, carrying its own expiry.</param>
    void Set(RatingCacheKey key, RatingCacheEntry entry);

    /// <summary>Removes a single entry.</summary>
    /// <param name="key">The cache key.</param>
    void Remove(RatingCacheKey key);

    /// <summary>Clears both the in-memory state and the persisted store (spec §7.1, §9.3).</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes once the store is cleared.</returns>
    Task ClearAsync(CancellationToken cancellationToken);

    /// <summary>Drops expired entries (called per run).</summary>
    void PruneExpired();

    /// <summary>Persists the current in-memory state atomically (run-end / periodic / shutdown).</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes once the flush is durable.</returns>
    Task FlushAsync(CancellationToken cancellationToken);
}
