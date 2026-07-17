using Jellyfin.Plugin.ExternalRatings.Core.Abstractions;

namespace Jellyfin.Plugin.ExternalRatings.Tests.Fakes;

/// <summary>
/// An in-memory <see cref="IBackupStore"/> that records backup calls (and their ordering via a shared
/// <see cref="CallRecorder"/>) and honours the "never overwritten" contract.
/// </summary>
internal sealed class RecordingBackupStore : IBackupStore
{
    private readonly CallRecorder? _recorder;
    private readonly Dictionary<Guid, BackupEntry> _entries = new();

    public RecordingBackupStore(CallRecorder? recorder = null) => _recorder = recorder;

    public List<Guid> Backups { get; } = new();

    public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task EnsureBackedUpAsync(Guid itemId, float? originalRating, CancellationToken cancellationToken)
    {
        _recorder?.Record($"Backup:{itemId}");
        if (!_entries.ContainsKey(itemId))
        {
            _entries[itemId] = new BackupEntry(originalRating, DateTimeOffset.UnixEpoch);
            Backups.Add(itemId);
        }

        return Task.CompletedTask;
    }

    public bool TryGet(Guid itemId, out BackupEntry entry) => _entries.TryGetValue(itemId, out entry);

    public Task RemoveAsync(Guid itemId, CancellationToken cancellationToken)
    {
        _entries.Remove(itemId);
        return Task.CompletedTask;
    }

    public void PruneOrphans(IReadOnlySet<Guid> liveItemIds)
    {
        foreach (var id in _entries.Keys.ToList())
        {
            if (!liveItemIds.Contains(id))
            {
                _entries.Remove(id);
            }
        }
    }

    public Task ClearAsync(CancellationToken cancellationToken)
    {
        _entries.Clear();
        return Task.CompletedTask;
    }
}
