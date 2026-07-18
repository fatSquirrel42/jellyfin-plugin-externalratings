namespace Jellyfin.Plugin.ExternalRatings.Core;

/// <summary>
/// Serializes the plugin's mutually-exclusive long/state-changing operations — a full enrichment pass,
/// a restore-all, and a cache clear — so at most one runs at a time (spec §15 step 10).
/// <para>
/// This guards against a <em>logical</em> race, not merely a data race: e.g. a restore removes a
/// backup and writes the original, while a concurrent full pass re-backs-up that restored value and
/// re-applies the external rating, silently undoing the restore. The stores are already thread-safe;
/// this gate enforces that such operations never overlap. The two exposed <c>InProgress</c> flags are
/// read lock-free by the realtime listener's fast path so it can skip work a full pass/restore covers.
/// </para>
/// </summary>
internal sealed class ExclusiveOperationGate
{
    private readonly object _lock = new();
    private volatile Operation _current = Operation.None;

    private enum Operation
    {
        None,
        FullRun,
        Restore,
        ClearCache
    }

    /// <summary>Gets a value indicating whether a full enrichment pass currently holds the gate.</summary>
    public bool IsFullRunInProgress => _current == Operation.FullRun;

    /// <summary>Gets a value indicating whether a restore-all currently holds the gate.</summary>
    public bool IsRestoreInProgress => _current == Operation.Restore;

    /// <summary>Attempts to begin a full enrichment pass.</summary>
    /// <returns><see langword="true"/> if the gate was acquired; <see langword="false"/> if another operation is in progress.</returns>
    public bool TryBeginFullRun() => TryBegin(Operation.FullRun);

    /// <summary>Attempts to begin a restore-all.</summary>
    /// <returns><see langword="true"/> if the gate was acquired; <see langword="false"/> if another operation is in progress.</returns>
    public bool TryBeginRestore() => TryBegin(Operation.Restore);

    /// <summary>Attempts to begin a cache clear.</summary>
    /// <returns><see langword="true"/> if the gate was acquired; <see langword="false"/> if another operation is in progress.</returns>
    public bool TryBeginClearCache() => TryBegin(Operation.ClearCache);

    /// <summary>Releases the gate after the current operation completes.</summary>
    public void End()
    {
        lock (_lock)
        {
            _current = Operation.None;
        }
    }

    private bool TryBegin(Operation operation)
    {
        lock (_lock)
        {
            if (_current != Operation.None)
            {
                return false;
            }

            _current = operation;
            return true;
        }
    }
}
