using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ExternalRatings.Core.Abstractions;

namespace Jellyfin.Plugin.ExternalRatings.Core;

/// <summary>
/// An <see cref="IItemWriter"/> decorator that records every write in a <see cref="SelfWriteTracker"/>
/// so the realtime listener can ignore the change event our own write raises (spec §15 step 9). The
/// id is marked <b>before</b> the inner write, because the host can raise the echo synchronously from
/// within the write call.
/// </summary>
internal sealed class SelfWriteTrackingItemWriter : IItemWriter
{
    private readonly IItemWriter _inner;
    private readonly SelfWriteTracker _tracker;

    /// <summary>Initializes a new instance of the <see cref="SelfWriteTrackingItemWriter"/> class.</summary>
    /// <param name="inner">The underlying writer.</param>
    /// <param name="tracker">The self-write tracker to record into.</param>
    public SelfWriteTrackingItemWriter(IItemWriter inner, SelfWriteTracker tracker)
    {
        _inner = inner;
        _tracker = tracker;
    }

    /// <inheritdoc />
    public float? GetCommunityRating(RatingItemRef item) => _inner.GetCommunityRating(item);

    /// <inheritdoc />
    public Task WriteAsync(RatingItemRef item, float? value, ItemWriteReason reason, CancellationToken cancellationToken)
    {
        // Mark before the write: the host may raise the ItemUpdated echo synchronously inside the write.
        _tracker.MarkWritten(item.ItemId);
        return _inner.WriteAsync(item, value, reason, cancellationToken);
    }
}
