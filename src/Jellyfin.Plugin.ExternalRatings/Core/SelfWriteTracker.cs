using System;
using System.Collections.Concurrent;
using Jellyfin.Plugin.ExternalRatings.Core.Abstractions;

namespace Jellyfin.Plugin.ExternalRatings.Core;

/// <summary>
/// Records the ids of items the plugin has written very recently, so the realtime listener can ignore
/// the change event our own write raises (spec §15 step 9). This makes the listener correct whether the
/// write reason is <c>None</c> (Plan A, already filtered by <see cref="ListenerGate"/>) or a
/// metadata reason (Plan B). Entries expire after a short window so a genuine later user edit of the
/// same item is not suppressed. Uses lazy eviction on read (same idiom as the pipeline error cache).
/// </summary>
internal sealed class SelfWriteTracker
{
    private readonly IClock _clock;
    private readonly TimeSpan _window;
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _writes = new();

    /// <summary>Initializes a new instance of the <see cref="SelfWriteTracker"/> class.</summary>
    /// <param name="clock">The clock.</param>
    /// <param name="window">How long a write is considered "recent" (the self-write suppression window).</param>
    public SelfWriteTracker(IClock clock, TimeSpan window)
    {
        _clock = clock;
        _window = window;
    }

    /// <summary>Marks that the plugin has just written the given item. Call this immediately before the write.</summary>
    /// <param name="itemId">The item id.</param>
    public void MarkWritten(Guid itemId) => _writes[itemId] = _clock.UtcNow;

    /// <summary>Gets a value indicating whether the plugin wrote the item within the suppression window.</summary>
    /// <param name="itemId">The item id.</param>
    /// <returns><see langword="true"/> if the item was written recently; otherwise <see langword="false"/>.</returns>
    public bool IsRecent(Guid itemId)
    {
        if (_writes.TryGetValue(itemId, out var writtenAt))
        {
            if (_clock.UtcNow - writtenAt <= _window)
            {
                return true;
            }

            _writes.TryRemove(itemId, out _);
        }

        return false;
    }
}
