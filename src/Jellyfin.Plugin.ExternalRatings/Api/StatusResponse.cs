using System.Collections.Generic;
using Jellyfin.Plugin.ExternalRatings.Resolvers;

namespace Jellyfin.Plugin.ExternalRatings.Api;

/// <summary>
/// The plugin status returned by <see cref="StatusController"/> (spec §10.2). This step exposes the
/// configuration-derived state and the NFO-saver warning (M7). Live run/realtime summaries, the H12
/// counter, the daily budget, and the circuit-breaker state are added once the pipeline services are
/// registered (§15 step 7).
/// </summary>
public class StatusResponse
{
    /// <summary>
    /// Gets a value indicating whether writes are currently suppressed (dry run).
    /// </summary>
    public bool DryRun { get; init; }

    /// <summary>
    /// Gets the key of the active resolver.
    /// </summary>
    public string ActiveResolverKey { get; init; } = string.Empty;

    /// <summary>
    /// Gets the item levels the active resolver supports (its capability feature flag).
    /// </summary>
    public IReadOnlyList<string> SupportedLevels { get; init; } = new List<string>();

    /// <summary>
    /// Gets the external rating sources the active resolver can return, with their display names and
    /// native scales. The config page renders its source dropdowns from this list.
    /// </summary>
    public IReadOnlyList<RatingSourceInfo> SupportedSources { get; init; } = new List<RatingSourceInfo>();

    /// <summary>
    /// Gets the capabilities of every selectable resolver, not just the active one, so the config
    /// page can switch resolvers and show the resulting levels and sources without a save/reload.
    /// </summary>
    public IReadOnlyList<ResolverCapabilities> Resolvers { get; init; } = new List<ResolverCapabilities>();

    /// <summary>
    /// Gets the item levels currently enabled in the configuration (intersected with the active
    /// resolver's capability at run time).
    /// </summary>
    public IReadOnlyList<string> EnabledLevels { get; init; } = new List<string>();

    /// <summary>
    /// Gets the configured daily HTTP request limit.
    /// </summary>
    public int DailyRequestLimit { get; init; }

    /// <summary>
    /// Gets the write-reason strategy in effect (Plan A / Plan B; spec §6.6).
    /// </summary>
    public string WriteReasonPlan { get; init; } = string.Empty;

    /// <summary>
    /// Gets the names of enabled libraries that have an active NFO saver (M7).
    /// </summary>
    public IReadOnlyList<string> LibrariesWithNfoSaver { get; init; } = new List<string>();

    /// <summary>
    /// Gets a value indicating whether a rating write could produce NFO writes in a media folder.
    /// </summary>
    public bool NfoWritesPossible { get; init; }

    /// <summary>
    /// Gets the snapshot of the last enrichment run and the circuit-breaker state.
    /// </summary>
    public RunStatusSnapshot? LastRun { get; init; }
}
