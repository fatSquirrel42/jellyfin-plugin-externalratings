using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.ExternalRatings.Core.Abstractions;

/// <summary>
/// The seam for reading and persisting an item's <c>CommunityRating</c>. The concrete
/// Jellyfin implementation is a thin, logic-free shell (spec §12.1 "Humble Shell").
/// </summary>
internal interface IItemWriter
{
    /// <summary>Reads the current community rating of an item.</summary>
    /// <param name="item">The item reference.</param>
    /// <returns>The current rating, or <see langword="null"/> if unset.</returns>
    float? GetCommunityRating(RatingItemRef item);

    /// <summary>
    /// Sets the community rating (a <see langword="null"/> value clears it) and persists the change durably.
    /// </summary>
    /// <param name="item">The item reference.</param>
    /// <param name="value">The value to write, or <see langword="null"/> to clear.</param>
    /// <param name="reason">The write reason.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the write is persisted.</returns>
    Task WriteAsync(RatingItemRef item, float? value, ItemWriteReason reason, CancellationToken cancellationToken);
}
