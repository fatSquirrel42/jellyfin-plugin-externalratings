using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ExternalRatings.Core;
using Jellyfin.Plugin.ExternalRatings.Core.Abstractions;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ExternalRatings.Tasks;

/// <summary>
/// The realtime item listener (spec §15 step 9): a hosted service that subscribes to library
/// add/update events and enriches a single item's community rating as it changes, instead of waiting
/// for the next full pass. It is a thin shell — the gating policy (<see cref="ListenerGate"/>), the
/// per-item coalescing (<see cref="ItemChangeDebouncer"/>) and the enrichment itself
/// (<see cref="ISingleItemEnricher"/>) are all separately tested. Event handlers never throw into the
/// host, and self-writes are ignored so a plugin write cannot re-trigger the listener.
/// </summary>
internal sealed class ItemChangedListener : IHostedService, IDisposable
{
    private static readonly TimeSpan DefaultDebounceWindow = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan DefaultTickInterval = TimeSpan.FromSeconds(1);

    // Upper bound on how long StopAsync waits for in-flight enrichments to drain before giving up, so a
    // hung HTTP call cannot block shutdown indefinitely.
    private static readonly TimeSpan StopDrainTimeout = TimeSpan.FromSeconds(5);

    private readonly ILibraryManager _libraryManager;
    private readonly ISingleItemEnricher _enricher;
    private readonly IClock _clock;
    private readonly ILogger<ItemChangedListener> _logger;
    private readonly Func<bool> _isEnabled;
    private readonly ItemChangeDebouncer _debouncer;
    private readonly TimeSpan _tickInterval;
    private readonly ConcurrentDictionary<Guid, byte> _inFlight = new();
    private readonly ConcurrentDictionary<Guid, Task> _running = new();

    private CancellationTokenSource? _cts;
    private Timer? _ticker;

    /// <summary>Initializes a new instance of the <see cref="ItemChangedListener"/> class.</summary>
    /// <param name="libraryManager">The library manager (event source and scan-state gate).</param>
    /// <param name="enricher">The single-item enrichment seam.</param>
    /// <param name="clock">The clock (drives the debounce window).</param>
    /// <param name="logger">The logger.</param>
    public ItemChangedListener(
        ILibraryManager libraryManager,
        ISingleItemEnricher enricher,
        IClock clock,
        ILogger<ItemChangedListener> logger)
        : this(
            libraryManager,
            enricher,
            clock,
            logger,
            () => Plugin.Instance?.Configuration.EnableRealtimeListener ?? false,
            DefaultDebounceWindow,
            DefaultTickInterval)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ItemChangedListener"/> class with explicit dependencies (tests).</summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="enricher">The single-item enrichment seam.</param>
    /// <param name="clock">The clock.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="isEnabled">Reads whether the realtime listener is enabled (fresh per event).</param>
    /// <param name="debounceWindow">The per-item coalesce window.</param>
    /// <param name="tickInterval">How often due items are drained.</param>
    internal ItemChangedListener(
        ILibraryManager libraryManager,
        ISingleItemEnricher enricher,
        IClock clock,
        ILogger<ItemChangedListener> logger,
        Func<bool> isEnabled,
        TimeSpan debounceWindow,
        TimeSpan tickInterval)
    {
        _libraryManager = libraryManager;
        _enricher = enricher;
        _clock = clock;
        _logger = logger;
        _isEnabled = isEnabled;
        _debouncer = new ItemChangeDebouncer(debounceWindow);
        _tickInterval = tickInterval;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = new CancellationTokenSource();
        _libraryManager.ItemAdded += OnItemAdded;
        _libraryManager.ItemUpdated += OnItemUpdated;
        _ticker = new Timer(_ => DrainDueItems(), null, _tickInterval, _tickInterval);
        _logger.LogInformation("External Ratings realtime listener started");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemAdded;
        _libraryManager.ItemUpdated -= OnItemUpdated;

        if (_ticker is not null)
        {
            await _ticker.DisposeAsync().ConfigureAwait(false);
            _ticker = null;
        }

        // Drain in-flight enrichments (bounded) so shutdown does not abandon an item mid-resolve and does
        // not dispose _cts while a task still holds its token. EnrichAsync swallows its own exceptions, so
        // Task.WhenAll only surfaces the drain timeout / stop-token cancellation.
        var pending = _running.Values.ToArray();
        if (pending.Length > 0)
        {
            try
            {
                await Task.WhenAll(pending).WaitAsync(StopDrainTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                _logger.LogWarning("External Ratings realtime listener stop: enrichment did not drain within {Timeout}", StopDrainTimeout);
            }
        }

        // Persist the realtime cache tail resolved since the last throttled flush.
        try
        {
            await _enricher.FlushPendingAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            _logger.LogWarning(ex, "External Ratings realtime listener stop: final cache flush failed");
        }

        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _ticker?.Dispose();
        _cts?.Dispose();
    }

    /// <summary>Drains items whose debounce window has elapsed and enriches each once. Also the timer callback.</summary>
    internal void DrainDueItems()
    {
        var cts = _cts;
        if (cts is null || cts.IsCancellationRequested)
        {
            return;
        }

        foreach (var id in _debouncer.CollectDue(_clock.UtcNow))
        {
            if (_inFlight.TryAdd(id, 0))
            {
                // Reserve the dedup slot first, then record the task (only if still running) so StopAsync
                // can drain it; a synchronously-completed enrichment needs no draining.
                var task = EnrichAsync(id, cts.Token);
                if (!task.IsCompleted)
                {
                    _running[id] = task;
                }
            }
        }
    }

    private static ItemChangeReason MapReason(ItemUpdateType reason)
    {
        // ItemUpdateType is a [Flags] enum; a change can carry several reasons at once, so any
        // metadata-bearing flag makes the change eligible.
        if (reason.HasFlag(ItemUpdateType.MetadataDownload))
        {
            return ItemChangeReason.MetadataDownload;
        }

        if (reason.HasFlag(ItemUpdateType.MetadataImport))
        {
            return ItemChangeReason.MetadataImport;
        }

        if (reason.HasFlag(ItemUpdateType.MetadataEdit))
        {
            return ItemChangeReason.MetadataEdit;
        }

        if (reason.HasFlag(ItemUpdateType.ImageUpdate))
        {
            return ItemChangeReason.ImageUpdate;
        }

        return ItemChangeReason.Other;
    }

    private void OnItemAdded(object? sender, ItemChangeEventArgs e) => OnChanged(e, isAdd: true);

    private void OnItemUpdated(object? sender, ItemChangeEventArgs e) => OnChanged(e, isAdd: false);

    private void OnChanged(ItemChangeEventArgs e, bool isAdd)
    {
        try
        {
            var item = e.Item;
            if (item is null)
            {
                return;
            }

            var enabled = _isEnabled();
            var reason = MapReason(e.UpdateReason);
            var decision = ListenerGate.Evaluate(
                enabled,
                _libraryManager.IsScanRunning,
                _enricher.IsCircuitOpen,
                isAdd,
                reason,
                _enricher.WasSelfWrite(item.Id),
                item.IsLocked);

            if (decision != GateDecision.Process)
            {
                _logger.LogDebug(
                    "External Ratings realtime listener skipped {ItemId} (add={IsAdd}, reason={Reason}): {Decision}",
                    item.Id,
                    isAdd,
                    reason,
                    decision);
                return;
            }

            _debouncer.Enqueue(item.Id, _clock.UtcNow);
        }
        catch (Exception ex)
        {
            // An exception must never propagate into the host's event dispatch.
            _logger.LogError(ex, "External Ratings realtime listener failed handling a change event");
        }
    }

    private async Task EnrichAsync(Guid itemId, CancellationToken cancellationToken)
    {
        try
        {
            await _enricher.EnrichItemAsync(itemId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutting down; ignore.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "External Ratings realtime enrichment failed for {ItemId}", itemId);
        }
        finally
        {
            _inFlight.TryRemove(itemId, out _);
            _running.TryRemove(itemId, out _);
        }
    }
}
