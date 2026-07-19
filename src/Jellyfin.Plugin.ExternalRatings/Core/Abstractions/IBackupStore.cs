using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.ExternalRatings.Core.Abstractions;

/// <summary>
/// Durable write-ahead backup of original ratings before any first write per item (spec §9.1).
/// </summary>
internal interface IBackupStore
{
    /// <summary>Loads the persisted backups into memory. A corrupt store starts empty.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes once loading is done.</returns>
    Task InitializeAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Ensures the item's original rating is durably recorded before its first write. A no-op if an
    /// entry already exists (never overwritten). Must be durable before the returned task completes.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="originalRating">The original rating to record.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes once the entry is durable.</returns>
    Task EnsureBackedUpAsync(Guid itemId, float? originalRating, CancellationToken cancellationToken);

    /// <summary>Attempts to read a backup entry.</summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="entry">The entry, if present.</param>
    /// <returns><see langword="true"/> if an entry exists.</returns>
    bool TryGet(Guid itemId, out BackupEntry entry);

    /// <summary>Gets the ids of all backed-up items (a snapshot; used by restore, spec §9.2).</summary>
    /// <returns>The backed-up item ids.</returns>
    IReadOnlyCollection<Guid> GetItemIds();

    /// <summary>Removes a backup entry (used by restore, spec §9.2 step 5).</summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes once removal is durable.</returns>
    Task RemoveAsync(Guid itemId, CancellationToken cancellationToken);

    /// <summary>Removes entries whose item no longer exists and persists the change (orphan pruning per
    /// run, H10). Async because the removal must be durable — a memory-only prune resurrects on reload.</summary>
    /// <param name="liveItemIds">The set of item ids that still exist.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes once the prune is durable.</returns>
    Task PruneOrphansAsync(IReadOnlySet<Guid> liveItemIds, CancellationToken cancellationToken);

    /// <summary>Removes all entries from memory and the persisted store.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes once the store is cleared.</returns>
    Task ClearAsync(CancellationToken cancellationToken);
}
