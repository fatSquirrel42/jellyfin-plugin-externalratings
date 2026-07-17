using System;
using Jellyfin.Plugin.ExternalRatings.Core.Abstractions;

namespace Jellyfin.Plugin.ExternalRatings.Persistence;

/// <summary>
/// Tracks the day's HTTP request count with server reconciliation and reset detection (spec §7.3).
/// Pure state plus <see cref="IClock"/>; it performs no HTTP itself (Humble Shell).
/// </summary>
internal sealed class DailyRequestCounter
{
    private readonly IClock _clock;
    private readonly object _gate = new();

    private int _count;
    private DateTime _lastDate;

    /// <summary>Initializes a new instance of the <see cref="DailyRequestCounter"/> class.</summary>
    /// <param name="clock">The clock.</param>
    public DailyRequestCounter(IClock clock)
    {
        _clock = clock;
        _lastDate = _clock.UtcNow.UtcDateTime.Date;
    }

    /// <summary>Gets the current request count for the day.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                RollIfNewDay();
                return _count;
            }
        }
    }

    /// <summary>Records one HTTP request (retries count too, spec §7.3).</summary>
    public void RecordRequest()
    {
        lock (_gate)
        {
            RollIfNewDay();
            _count++;
        }
    }

    /// <summary>Adopts the authoritative server count at run start (the /user cold start).</summary>
    /// <param name="serverUsedCount">The server-reported used count.</param>
    public void InitializeFromColdStart(int serverUsedCount) => AdoptServerCount(serverUsedCount);

    /// <summary>Reconciles with a server count seen in a response header.</summary>
    /// <param name="serverUsedCount">The server-reported used count.</param>
    public void SyncFromHeader(int serverUsedCount) => AdoptServerCount(serverUsedCount);

    /// <summary>Whether the daily limit is reached.</summary>
    /// <param name="dailyLimit">The configured daily limit.</param>
    /// <returns><see langword="true"/> if the count is at or above the limit.</returns>
    public bool IsExhausted(int dailyLimit)
    {
        lock (_gate)
        {
            RollIfNewDay();
            return _count >= dailyLimit;
        }
    }

    private void AdoptServerCount(int serverUsedCount)
    {
        lock (_gate)
        {
            // The server is authoritative and monotone within a day; a value below the local count
            // means the server rolled over (H11 reset detection). Either way, trust the server.
            _lastDate = _clock.UtcNow.UtcDateTime.Date;
            _count = serverUsedCount;
        }
    }

    private void RollIfNewDay()
    {
        var today = _clock.UtcNow.UtcDateTime.Date;
        if (today > _lastDate)
        {
            _lastDate = today;
            _count = 0;
        }
    }
}
