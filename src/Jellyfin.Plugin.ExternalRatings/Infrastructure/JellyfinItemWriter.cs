using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ExternalRatings.Core.Abstractions;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.ExternalRatings.Infrastructure;

/// <summary>
/// The Jellyfin-backed <see cref="IItemWriter"/> (Humble Shell, spec §12.1): reads and persists
/// <c>CommunityRating</c> via <see cref="ILibraryManager"/> with a fixed write reason. No decision
/// logic lives here.
/// </summary>
internal sealed class JellyfinItemWriter : IItemWriter
{
    /// <summary>
    /// Plan B (spec §6.6): write with <see cref="ItemUpdateType.MetadataEdit"/> so the change flows
    /// through the host's metadata savers exactly like a manual edit — the NFO saver rewrites the
    /// media-folder NFO for libraries where it is enabled (and does nothing where it is not), keeping
    /// the DB and NFO consistent. The write reason therefore echoes back through
    /// <c>ILibraryManager.ItemUpdated</c>; the realtime listener relies on the <c>SelfWriteTracker</c>
    /// (and the pipeline's no-change idempotency) to avoid re-triggering on our own write. Live
    /// verification (2026-07-18) confirmed the None variant persisted but did not update the NFO, which
    /// is why Plan B is used here.
    /// </summary>
    private const ItemUpdateType WriteUpdateReason = ItemUpdateType.MetadataEdit;

    private readonly ILibraryManager _libraryManager;

    /// <summary>Initializes a new instance of the <see cref="JellyfinItemWriter"/> class.</summary>
    /// <param name="libraryManager">The library manager.</param>
    public JellyfinItemWriter(ILibraryManager libraryManager)
    {
        _libraryManager = libraryManager;
    }

    /// <inheritdoc />
    public float? GetCommunityRating(RatingItemRef item)
        => _libraryManager.GetItemById(item.ItemId)?.CommunityRating;

    /// <inheritdoc />
    public async Task WriteAsync(RatingItemRef item, float? value, ItemWriteReason reason, CancellationToken cancellationToken)
    {
        var baseItem = _libraryManager.GetItemById(item.ItemId);
        if (baseItem is null)
        {
            return;
        }

        baseItem.CommunityRating = value;
        await _libraryManager
            .UpdateItemAsync(baseItem, baseItem.GetParent(), WriteUpdateReason, cancellationToken)
            .ConfigureAwait(false);
    }
}
