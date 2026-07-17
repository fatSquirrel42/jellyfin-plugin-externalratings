using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ExternalRatings.Core;
using Jellyfin.Plugin.ExternalRatings.Core.Abstractions;
using Jellyfin.Plugin.ExternalRatings.Resolvers;

namespace Jellyfin.Plugin.ExternalRatings.Persistence;

/// <summary>
/// The persistent, write-behind resolution cache (spec §7.1). Entries live in memory and are only
/// flushed to the backing <see cref="ICacheFileStore"/> on demand (run-end / periodic / shutdown).
/// </summary>
internal sealed class FileRatingCache : IRatingCache, IDisposable
{
    private const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    private readonly ICacheFileStore _fileStore;
    private readonly IClock _clock;
    private readonly ConcurrentDictionary<RatingCacheKey, RatingCacheEntry> _entries = new();
    private readonly SemaphoreSlim _fileGate = new(1, 1);

    /// <summary>Initializes a new instance of the <see cref="FileRatingCache"/> class.</summary>
    /// <param name="fileStore">The backing file store.</param>
    /// <param name="clock">The clock used to evaluate expiry.</param>
    public FileRatingCache(ICacheFileStore fileStore, IClock clock)
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

            CacheFileDto? dto;
            try
            {
                dto = JsonSerializer.Deserialize<CacheFileDto>(bytes, SerializerOptions);
            }
            catch (JsonException)
            {
                // Corrupt store: fail-safe by starting empty (spec §7.1).
                return;
            }

            if (dto is null || dto.SchemaVersion != CurrentSchemaVersion || dto.Entries is null)
            {
                return;
            }

            foreach (var entry in dto.Entries)
            {
                if (!Enum.TryParse<ItemLevel>(entry.Level, out var level)
                    || !Enum.TryParse<RatingResolution>(entry.Kind, out var kind))
                {
                    continue;
                }

                var key = new RatingCacheKey(entry.Resolver, entry.TargetSource, entry.InputProvider, entry.InputId, level);
                _entries[key] = new RatingCacheEntry(kind, entry.Score, entry.ExpiresAt);
            }
        }
        finally
        {
            _fileGate.Release();
        }
    }

    /// <inheritdoc />
    public bool TryGet(RatingCacheKey key, out RatingCacheEntry entry)
    {
        if (_entries.TryGetValue(key, out entry) && entry.ExpiresAt > _clock.UtcNow)
        {
            return true;
        }

        entry = default;
        return false;
    }

    /// <inheritdoc />
    public void Set(RatingCacheKey key, RatingCacheEntry entry) => _entries[key] = entry;

    /// <inheritdoc />
    public void Remove(RatingCacheKey key) => _entries.TryRemove(key, out _);

    /// <inheritdoc />
    public async Task ClearAsync(CancellationToken cancellationToken)
    {
        // Clear memory first so the effect is immediate, then delete the file under the gate so a
        // concurrent flush cannot reanimate the cleared cache (spec §9.3).
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
    public void PruneExpired()
    {
        var now = _clock.UtcNow;
        foreach (var pair in _entries)
        {
            if (pair.Value.ExpiresAt <= now)
            {
                _entries.TryRemove(pair.Key, out _);
            }
        }
    }

    /// <inheritdoc />
    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        await _fileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var dto = new CacheFileDto { SchemaVersion = CurrentSchemaVersion };
            foreach (var pair in _entries)
            {
                dto.Entries.Add(new CacheEntryDto
                {
                    Resolver = pair.Key.Resolver,
                    TargetSource = pair.Key.TargetSource,
                    InputProvider = pair.Key.InputProvider,
                    InputId = pair.Key.InputId,
                    Level = pair.Key.Level.ToString(),
                    Kind = pair.Value.Kind.ToString(),
                    Score = pair.Value.Score,
                    ExpiresAt = pair.Value.ExpiresAt
                });
            }

            var bytes = JsonSerializer.SerializeToUtf8Bytes(dto, SerializerOptions);
            await _fileStore.WriteAtomicAsync(bytes, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _fileGate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _fileGate.Dispose();

    private sealed class CacheFileDto
    {
        public int SchemaVersion { get; set; }

        public List<CacheEntryDto> Entries { get; set; } = new();
    }

    private sealed class CacheEntryDto
    {
        public string Resolver { get; set; } = string.Empty;

        public string TargetSource { get; set; } = string.Empty;

        public string InputProvider { get; set; } = string.Empty;

        public string InputId { get; set; } = string.Empty;

        public string Level { get; set; } = string.Empty;

        public string Kind { get; set; } = string.Empty;

        public float? Score { get; set; }

        public DateTimeOffset ExpiresAt { get; set; }
    }
}
