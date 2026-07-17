using Jellyfin.Plugin.ExternalRatings.Core.Abstractions;

namespace Jellyfin.Plugin.ExternalRatings.Tests.Fakes;

/// <summary>
/// An in-memory <see cref="IItemWriter"/> that records every write (and its ordering via a shared
/// <see cref="CallRecorder"/>) and lets tests seed the "current" community rating.
/// </summary>
internal sealed class RecordingItemWriter : IItemWriter
{
    private readonly CallRecorder? _recorder;
    private readonly Dictionary<Guid, float?> _ratings = new();

    public RecordingItemWriter(CallRecorder? recorder = null) => _recorder = recorder;

    public List<WriteRecord> Writes { get; } = new();

    public void SeedRating(Guid itemId, float? value) => _ratings[itemId] = value;

    public float? GetCommunityRating(RatingItemRef item)
        => _ratings.TryGetValue(item.ItemId, out var value) ? value : null;

    public Task WriteAsync(RatingItemRef item, float? value, ItemWriteReason reason, CancellationToken cancellationToken)
    {
        _recorder?.Record($"Write:{item.ItemId}");
        _ratings[item.ItemId] = value;
        Writes.Add(new WriteRecord(item.ItemId, value, reason));
        return Task.CompletedTask;
    }

    internal sealed record WriteRecord(Guid ItemId, float? Value, ItemWriteReason Reason);
}
