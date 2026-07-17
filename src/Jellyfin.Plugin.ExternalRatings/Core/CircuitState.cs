namespace Jellyfin.Plugin.ExternalRatings.Core;

/// <summary>
/// The state of the global circuit breaker (spec §7.5).
/// </summary>
internal enum CircuitState
{
    /// <summary>Requests flow normally.</summary>
    Closed,

    /// <summary>Tripped; requests are blocked until the cooldown elapses.</summary>
    Open,

    /// <summary>Cooldown elapsed; a single probe request is allowed to decide the next state.</summary>
    HalfOpen
}
