using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.ExternalRatings.Core;

/// <summary>
/// Coalesces a burst of change events for the same item into a single enrichment (spec §15 step 9).
/// Each <see cref="Enqueue"/> (re)sets the item's due time to <c>now + window</c>, so rapid repeats push
/// the due time out and collapse into one run; <see cref="CollectDue"/> returns and removes the items
/// whose quiet window has elapsed. Time is passed in (the listener supplies <c>IClock.UtcNow</c>), which
/// keeps the coalescing logic deterministic and unit-testable without a real timer.
/// </summary>
internal sealed class ItemChangeDebouncer
{
    private readonly TimeSpan _window;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, DateTimeOffset> _dueTimes = new();

    /// <summary>Initializes a new instance of the <see cref="ItemChangeDebouncer"/> class.</summary>
    /// <param name="window">The quiet window an item must be idle for before it becomes due.</param>
    public ItemChangeDebouncer(TimeSpan window)
    {
        _window = window;
    }

    /// <summary>Enqueues (or re-arms) an item, setting its due time to <paramref name="now"/> + window.</summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="now">The current time.</param>
    public void Enqueue(Guid itemId, DateTimeOffset now)
    {
        lock (_gate)
        {
            _dueTimes[itemId] = now + _window;
        }
    }

    /// <summary>Returns and removes all items whose due time is at or before <paramref name="now"/>.</summary>
    /// <param name="now">The current time.</param>
    /// <returns>The due item ids (possibly empty).</returns>
    public IReadOnlyList<Guid> CollectDue(DateTimeOffset now)
    {
        lock (_gate)
        {
            var due = new List<Guid>();
            foreach (var pair in _dueTimes)
            {
                if (pair.Value <= now)
                {
                    due.Add(pair.Key);
                }
            }

            foreach (var id in due)
            {
                _dueTimes.Remove(id);
            }

            return due;
        }
    }
}
