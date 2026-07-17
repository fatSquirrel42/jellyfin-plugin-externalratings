using System;

namespace Jellyfin.Plugin.ExternalRatings.Core;

/// <summary>
/// Per-run counters and the correlating run id (spec §6.7, §7.6). Clock-free; times are supplied by
/// the caller.
/// </summary>
internal sealed class RunSummary
{
    private readonly object _gate = new();

    /// <summary>Gets the correlating run id.</summary>
    public Guid RunId { get; } = Guid.NewGuid();

    /// <summary>Gets the number of items processed.</summary>
    public long ItemsProcessed { get; private set; }

    /// <summary>Gets the number of scores written.</summary>
    public long Updated { get; private set; }

    /// <summary>Gets the number of items skipped because the change was within epsilon.</summary>
    public long SkippedNoChange { get; private set; }

    /// <summary>Gets the number of writes suppressed by DryRun.</summary>
    public long DryRunSkipped { get; private set; }

    /// <summary>Gets the number of ratings cleared.</summary>
    public long Cleared { get; private set; }

    /// <summary>Gets the number of authoritative no-matches.</summary>
    public long NoMatch { get; private set; }

    /// <summary>Gets the number of unsupported-level items.</summary>
    public long NotSupported { get; private set; }

    /// <summary>Gets the number of items skipped for lack of a usable id.</summary>
    public long SkippedNoId { get; private set; }

    /// <summary>Gets the number of errors.</summary>
    public long Errors { get; private set; }

    /// <summary>Gets the number of cache hits.</summary>
    public long CacheHits { get; private set; }

    /// <summary>Gets the count of no-matches that still had unused alternative ids (H12).</summary>
    public long NoMatchWithUnusedAlternatives { get; private set; }

    /// <summary>Gets the number of items skipped because the circuit was open.</summary>
    public long CircuitOpenSkips { get; private set; }

    /// <summary>Records a terminal outcome.</summary>
    /// <param name="outcome">The outcome.</param>
    public void Record(RatingOutcome outcome)
    {
        lock (_gate)
        {
            ItemsProcessed++;
            switch (outcome)
            {
                case RatingOutcome.Updated:
                    Updated++;
                    break;
                case RatingOutcome.SkippedNoChange:
                    SkippedNoChange++;
                    break;
                case RatingOutcome.SkippedDryRun:
                    DryRunSkipped++;
                    break;
                case RatingOutcome.Cleared:
                    Cleared++;
                    break;
                case RatingOutcome.NoMatch:
                    NoMatch++;
                    break;
                case RatingOutcome.NotSupported:
                    NotSupported++;
                    break;
                case RatingOutcome.SkippedNoId:
                    SkippedNoId++;
                    break;
                case RatingOutcome.Error:
                    Errors++;
                    break;
                case RatingOutcome.CircuitOpen:
                    CircuitOpenSkips++;
                    break;
            }
        }
    }

    /// <summary>Records that a cache hit avoided a resolver call.</summary>
    public void RecordCacheHit()
    {
        lock (_gate)
        {
            CacheHits++;
        }
    }

    /// <summary>Records a no-match that had unused alternative ids (H12).</summary>
    public void RecordNoMatchWithUnusedAlternatives()
    {
        lock (_gate)
        {
            NoMatchWithUnusedAlternatives++;
        }
    }
}
