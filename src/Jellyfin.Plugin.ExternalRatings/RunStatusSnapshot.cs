using System;

namespace Jellyfin.Plugin.ExternalRatings;

/// <summary>
/// A public, primitives-only snapshot of the last enrichment run and the circuit-breaker state,
/// surfaced by the status endpoint (spec §10.2). Keeps the internal <c>RunSummary</c> internal.
/// </summary>
/// <param name="LastRunUtc">When the last run finished, or <see langword="null"/> if none ran yet.</param>
/// <param name="ItemsProcessed">Items processed in the last run.</param>
/// <param name="Updated">Ratings written in the last run.</param>
/// <param name="NoMatch">Authoritative no-matches in the last run.</param>
/// <param name="Errors">Errors in the last run.</param>
/// <param name="DryRunSkipped">Writes suppressed by dry run in the last run.</param>
/// <param name="CircuitOpen">Whether the global circuit breaker is currently open.</param>
public sealed record RunStatusSnapshot(
    DateTimeOffset? LastRunUtc,
    long ItemsProcessed,
    long Updated,
    long NoMatch,
    long Errors,
    long DryRunSkipped,
    bool CircuitOpen);
