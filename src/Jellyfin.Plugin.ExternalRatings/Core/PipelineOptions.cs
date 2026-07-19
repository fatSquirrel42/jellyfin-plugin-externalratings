using System;

namespace Jellyfin.Plugin.ExternalRatings.Core;

/// <summary>
/// The pipeline's behavioral options, read fresh per item so admin toggles (notably DryRun) take
/// effect immediately (spec §9.3).
/// </summary>
internal sealed record PipelineOptions
{
    /// <summary>Gets a value indicating whether writes are suppressed (default on, spec §4).</summary>
    public bool DryRun { get; init; } = true;

    /// <summary>Gets the behavior on an authoritative no-match.</summary>
    public NoMatchBehavior NoMatchBehavior { get; init; } = NoMatchBehavior.LeaveExisting;

    /// <summary>Gets the behavior when the level/provider is unsupported or no id is present.</summary>
    public UnsupportedLevelBehavior UnsupportedLevelBehavior { get; init; } = UnsupportedLevelBehavior.LeaveExisting;

    /// <summary>Gets the write threshold: a score is only written if it differs by more than this (spec §6).</summary>
    public float Epsilon { get; init; } = 0.05f;

    /// <summary>Gets the positive-cache time-to-live.</summary>
    public TimeSpan CacheTtl { get; init; } = TimeSpan.FromDays(7);

    /// <summary>Gets the negative-cache (no-match) time-to-live.</summary>
    public TimeSpan NegativeCacheTtl { get; init; } = TimeSpan.FromDays(1);

    /// <summary>Gets the in-memory error-cache time-to-live (spec §6, M10).</summary>
    public TimeSpan ErrorCacheTtl { get; init; } = TimeSpan.FromHours(1);
}
