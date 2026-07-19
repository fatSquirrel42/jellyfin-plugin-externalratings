using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ExternalRatings.Core.Abstractions;

namespace Jellyfin.Plugin.ExternalRatings.Persistence;

/// <summary>
/// Durable, write-ahead backup of original ratings (spec §9.1). Backed by an append-only JSON-Lines
/// log: every new backup and every removal is a single O(1) append that is flushed before the operation
/// returns, so a crash can never lose a backup a later item write depended on — and a large first run no
/// longer rewrites the whole file per item (the previous full-rewrite-per-backup was O(n²)). The log is
/// compacted (rewritten to one line per live entry) on load and after an orphan prune, so it does not
/// grow without bound. Every mutation is serialized through <see cref="_fileGate"/>.
/// </summary>
internal sealed class BackupStore : IBackupStore, IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    private readonly ICacheFileStore _fileStore;
    private readonly IClock _clock;
    private readonly ConcurrentDictionary<Guid, BackupEntry> _entries = new();
    private readonly SemaphoreSlim _fileGate = new(1, 1);

    /// <summary>Initializes a new instance of the <see cref="BackupStore"/> class.</summary>
    /// <param name="fileStore">The backing file store.</param>
    /// <param name="clock">The clock.</param>
    public BackupStore(ICacheFileStore fileStore, IClock clock)
    {
        _fileStore = fileStore;
        _clock = clock;
    }

    /// <inheritdoc />
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _fileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _entries.Clear();

            var bytes = await _fileStore.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (bytes is null || bytes.Length == 0)
            {
                return;
            }

            foreach (var line in Encoding.UTF8.GetString(bytes).Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                BackupRecordDto? record;
                try
                {
                    record = JsonSerializer.Deserialize<BackupRecordDto>(line, SerializerOptions);
                }
                catch (JsonException)
                {
                    // Skip a corrupt line; the rest of the log is still usable (fail-safe, spec §9.1).
                    continue;
                }

                if (record is null || !Guid.TryParse(record.ItemId, out var itemId))
                {
                    continue;
                }

                if (record.Removed)
                {
                    _entries.TryRemove(itemId, out _);
                }
                else
                {
                    _entries[itemId] = new BackupEntry(record.OriginalRating, record.BackedUpAt);
                }
            }

            // Compact so tombstones and superseded lines do not accumulate across restarts.
            await _fileStore.WriteAtomicAsync(SerializeAll(), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _fileGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task EnsureBackedUpAsync(Guid itemId, float? originalRating, CancellationToken cancellationToken)
    {
        await _fileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Never overwrite an existing backup (spec §9.1).
            if (_entries.ContainsKey(itemId))
            {
                return;
            }

            var entry = new BackupEntry(originalRating, _clock.UtcNow);
            _entries[itemId] = entry;

            // Write-ahead: append (and flush) the record before returning, so the write that follows this
            // call can never outrun its backup on disk.
            await AppendRecordAsync(
                new BackupRecordDto
                {
                    ItemId = ToKey(itemId),
                    OriginalRating = entry.OriginalRating,
                    BackedUpAt = entry.BackedUpAt
                },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _fileGate.Release();
        }
    }

    /// <inheritdoc />
    public bool TryGet(Guid itemId, out BackupEntry entry) => _entries.TryGetValue(itemId, out entry);

    /// <inheritdoc />
    public IReadOnlyCollection<Guid> GetItemIds() => _entries.Keys.ToArray();

    /// <inheritdoc />
    public async Task RemoveAsync(Guid itemId, CancellationToken cancellationToken)
    {
        await _fileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_entries.TryRemove(itemId, out _))
            {
                await AppendRecordAsync(
                    new BackupRecordDto { ItemId = ToKey(itemId), Removed = true },
                    cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _fileGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task PruneOrphansAsync(IReadOnlySet<Guid> liveItemIds, CancellationToken cancellationToken)
    {
        await _fileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var changed = false;
            foreach (var id in _entries.Keys)
            {
                if (!liveItemIds.Contains(id) && _entries.TryRemove(id, out _))
                {
                    changed = true;
                }
            }

            // Persist the prune with a single compacting rewrite. Without this the prune was memory-only,
            // so orphans resurrected from the log on the next load (H10 was a no-op across restarts). One
            // O(n) write per run, not per item.
            if (changed)
            {
                await _fileStore.WriteAtomicAsync(SerializeAll(), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _fileGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task ClearAsync(CancellationToken cancellationToken)
    {
        _entries.Clear();

        await _fileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _entries.Clear();
            await _fileStore.DeleteAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _fileGate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _fileGate.Dispose();

    private static string ToKey(Guid itemId) => itemId.ToString("D", CultureInfo.InvariantCulture);

    private async Task AppendRecordAsync(BackupRecordDto record, CancellationToken cancellationToken)
    {
        var line = JsonSerializer.Serialize(record, SerializerOptions) + "\n";
        await _fileStore.AppendAsync(Encoding.UTF8.GetBytes(line), cancellationToken).ConfigureAwait(false);
    }

    private byte[] SerializeAll()
    {
        var builder = new StringBuilder();
        foreach (var pair in _entries)
        {
            var record = new BackupRecordDto
            {
                ItemId = ToKey(pair.Key),
                OriginalRating = pair.Value.OriginalRating,
                BackedUpAt = pair.Value.BackedUpAt
            };
            builder.Append(JsonSerializer.Serialize(record, SerializerOptions));
            builder.Append('\n');
        }

        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    private sealed class BackupRecordDto
    {
        public string ItemId { get; set; } = string.Empty;

        public float? OriginalRating { get; set; }

        public DateTimeOffset BackedUpAt { get; set; }

        public bool Removed { get; set; }
    }
}
