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
    /// Plan A (spec §6.6): write with <see cref="ItemUpdateType.None"/>, which sits below the NFO
    /// saver gate (MetadataDownload) and the listener filter, so plugin writes produce no NFO files
    /// and do not re-trigger the listener. The single place to switch to Plan B (MetadataEdit).
    /// </summary>
    private const ItemUpdateType WriteUpdateReason = ItemUpdateType.None;

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
