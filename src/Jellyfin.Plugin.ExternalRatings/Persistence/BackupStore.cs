using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ExternalRatings.Core.Abstractions;

namespace Jellyfin.Plugin.ExternalRatings.Persistence;

/// <summary>
/// Durable, write-ahead backup of original ratings (spec §9.1). Every mutation is flushed to the
/// backing <see cref="ICacheFileStore"/> before the operation returns, so a crash can never lose a
/// backup that a subsequent item write depended on.
/// </summary>
internal sealed class BackupStore : IBackupStore, IDisposable
{
    private const int CurrentSchemaVersion = 1;

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

            BackupFileDto? dto;
            try
            {
                dto = JsonSerializer.Deserialize<BackupFileDto>(bytes, SerializerOptions);
            }
            catch (JsonException)
            {
                // Corrupt store: fail-safe by starting empty (spec §9.1).
                return;
            }

            if (dto is null || dto.SchemaVersion != CurrentSchemaVersion || dto.Entries is null)
            {
                return;
            }

            foreach (var entry in dto.Entries)
            {
                if (Guid.TryParse(entry.ItemId, out var itemId))
                {
                    _entries[itemId] = new BackupEntry(entry.OriginalRating, entry.BackedUpAt);
                }
            }
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

            _entries[itemId] = new BackupEntry(originalRating, _clock.UtcNow);
            await PersistLockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _fileGate.Release();
        }
    }

    /// <inheritdoc />
    public bool TryGet(Guid itemId, out BackupEntry entry) => _entries.TryGetValue(itemId, out entry);

    /// <inheritdoc />
    public async Task RemoveAsync(Guid itemId, CancellationToken cancellationToken)
    {
        await _fileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _entries.TryRemove(itemId, out _);
            await PersistLockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _fileGate.Release();
        }
    }

    /// <inheritdoc />
    public void PruneOrphans(IReadOnlySet<Guid> liveItemIds)
    {
        foreach (var id in _entries.Keys)
        {
            if (!liveItemIds.Contains(id))
            {
                _entries.TryRemove(id, out _);
            }
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

    private async Task PersistLockedAsync(CancellationToken cancellationToken)
    {
        var dto = new BackupFileDto { SchemaVersion = CurrentSchemaVersion };
        foreach (var pair in _entries)
        {
            dto.Entries.Add(new BackupEntryDto
            {
                ItemId = pair.Key.ToString("D", System.Globalization.CultureInfo.InvariantCulture),
                OriginalRating = pair.Value.OriginalRating,
                BackedUpAt = pair.Value.BackedUpAt
            });
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(dto, SerializerOptions);
        await _fileStore.WriteAtomicAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    private sealed class BackupFileDto
    {
        public int SchemaVersion { get; set; }

        public List<BackupEntryDto> Entries { get; set; } = new();
    }

    private sealed class BackupEntryDto
    {
        public string ItemId { get; set; } = string.Empty;

        public float? OriginalRating { get; set; }

        public DateTimeOffset BackedUpAt { get; set; }
    }
}
