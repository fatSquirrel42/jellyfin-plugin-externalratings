using System.Collections.Generic;

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
    /// Gets the selectable rating sources, each with the item levels it can be served at. The
    /// config page builds its source dropdown, scale hint and level coverage from this. There is no
    /// provider to choose: the plugin routes per item from (source × level × available ids).
    /// </summary>
    public IReadOnlyList<SourceCapabilities> SupportedSources { get; init; } = new List<SourceCapabilities>();

    /// <summary>
    /// Gets the item levels currently enabled in the configuration. The choice is global; whether a
    /// given library's source can actually serve a level is decided per item.
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
