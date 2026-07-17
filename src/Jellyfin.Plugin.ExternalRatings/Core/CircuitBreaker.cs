using System;
using Jellyfin.Plugin.ExternalRatings.Core.Abstractions;

namespace Jellyfin.Plugin.ExternalRatings.Core;

/// <summary>
/// A single global circuit breaker shared across all trigger paths (spec §7.5). Opens after a
/// threshold of consecutive errors or immediately on a rate-limit signal, then blocks until a
/// cooldown elapses, after which one probe decides whether to close again.
/// </summary>
internal sealed class CircuitBreaker
{
    private readonly IClock _clock;
    private readonly int _failureThreshold;
    private readonly TimeSpan _cooldown;
    private readonly object _gate = new();

    private CircuitState _state = CircuitState.Closed;
    private int _consecutiveErrors;
    private DateTimeOffset _openedAt;
    private bool _probeOutstanding;

    /// <summary>Initializes a new instance of the <see cref="CircuitBreaker"/> class.</summary>
    /// <param name="clock">The clock.</param>
    /// <param name="failureThreshold">Consecutive errors that open the breaker (default 5).</param>
    /// <param name="cooldown">The cooldown before a probe is allowed (default 30 minutes).</param>
    public CircuitBreaker(IClock clock, int failureThreshold = 5, TimeSpan? cooldown = null)
    {
        _clock = clock;
        _failureThreshold = failureThreshold;
        _cooldown = cooldown ?? TimeSpan.FromMinutes(30);
    }

    /// <summary>Gets the current, time-aware state.</summary>
    public CircuitState State
    {
        get
        {
            lock (_gate)
            {
                return EffectiveState();
            }
        }
    }

    /// <summary>Gets a value indicating whether the breaker is tripped (the listener-pause flag).</summary>
    public bool IsOpen
    {
        get
        {
            lock (_gate)
            {
                return EffectiveState() == CircuitState.Open;
            }
        }
    }

    /// <summary>Decides whether a request may proceed, handing out a single probe when half-open.</summary>
    /// <returns><see langword="true"/> if the request may proceed.</returns>
    public bool AllowRequest()
    {
        lock (_gate)
        {
            Normalize();

            switch (_state)
            {
                case CircuitState.Closed:
                    return true;
                case CircuitState.HalfOpen:
                    if (_probeOutstanding)
                    {
                        return false;
                    }

                    _probeOutstanding = true;
                    return true;
                default:
                    return false;
            }
        }
    }

    /// <summary>Records a successful call.</summary>
    public void RecordSuccess()
    {
        lock (_gate)
        {
            _consecutiveErrors = 0;
            _probeOutstanding = false;
            _state = CircuitState.Closed;
        }
    }

    /// <summary>Records a failed call (a whole-chunk failure counts as one).</summary>
    public void RecordError()
    {
        lock (_gate)
        {
            Normalize();

            if (_state == CircuitState.HalfOpen)
            {
                Trip();
                return;
            }

            if (_state == CircuitState.Open)
            {
                return;
            }

            _consecutiveErrors++;
            if (_consecutiveErrors >= _failureThreshold)
            {
                Trip();
            }
        }
    }

    /// <summary>Records a rate-limit (HTTP 429) signal, which opens the breaker immediately.</summary>
    public void RecordRateLimited()
    {
        lock (_gate)
        {
            Trip();
        }
    }

    private CircuitState EffectiveState()
    {
        if (_state == CircuitState.Open && _clock.UtcNow - _openedAt >= _cooldown)
        {
            return CircuitState.HalfOpen;
        }

        return _state;
    }

    private void Normalize()
    {
        if (_state == CircuitState.Open && _clock.UtcNow - _openedAt >= _cooldown)
        {
            _state = CircuitState.HalfOpen;
            _probeOutstanding = false;
        }
    }

    private void Trip()
    {
        _state = CircuitState.Open;
        _openedAt = _clock.UtcNow;
        _consecutiveErrors = 0;
        _probeOutstanding = false;
    }
}
