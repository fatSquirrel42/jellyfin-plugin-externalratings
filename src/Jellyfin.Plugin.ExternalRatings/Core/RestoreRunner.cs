using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ExternalRatings.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ExternalRatings.Core;

/// <summary>
/// Restores every backed-up item's community rating to its original value and removes the backup entry
/// (spec §9.2, §15 step 10). This is the "Restore all" admin action, the inverse of enrichment.
/// <para>
/// It operates purely over backup ids via <see cref="IBackupStore"/> and writes through
/// <see cref="IItemWriter"/>, so it has no Jellyfin host dependency and is fully testable. It needs no
/// item name (the writer resolves the item by id; <see cref="RatingItemRef.DisplayName"/> is log-only)
/// and no lock awareness — restore is a deliberate reset that ignores <c>IsLocked</c> on purpose.
/// </para>
/// </summary>
internal sealed class RestoreRunner
{
    private readonly IBackupStore _backup;
    private readonly IItemWriter _writer;
    private readonly ILogger<RestoreRunner> _logger;

    /// <summary>Initializes a new instance of the <see cref="RestoreRunner"/> class.</summary>
    /// <param name="backup">The backup store holding original ratings.</param>
    /// <param name="writer">The item writer (must be the self-write-tracking writer so the restore's own write does not re-trigger the realtime listener).</param>
    /// <param name="logger">The logger.</param>
    public RestoreRunner(IBackupStore backup, IItemWriter writer, ILogger<RestoreRunner> logger)
    {
        _backup = backup;
        _writer = writer;
        _logger = logger;
    }

    /// <summary>Restores all backed-up items to their original ratings and clears their backups.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The number of backup entries processed.</returns>
    public async Task<int> RestoreAllAsync(CancellationToken cancellationToken)
    {
        var ids = _backup.GetItemIds();
        _logger.LogInformation("External Ratings restore starting for {Count} backed-up item(s)", ids.Count);

        var restored = 0;
        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // A null original restores the "no rating" state (clears the field). Items no longer in the
            // library make WriteAsync a silent no-op; we still remove the stale backup either way.
            var original = _backup.TryGet(id, out var entry) ? entry.OriginalRating : null;
            await _writer.WriteAsync(new RatingItemRef(id, null), original, ItemWriteReason.RatingRestored, cancellationToken).ConfigureAwait(false);
            await _backup.RemoveAsync(id, cancellationToken).ConfigureAwait(false);
            restored++;
        }

        _logger.LogInformation("External Ratings restore done: {Count} item(s) restored", restored);
        return restored;
    }
}
