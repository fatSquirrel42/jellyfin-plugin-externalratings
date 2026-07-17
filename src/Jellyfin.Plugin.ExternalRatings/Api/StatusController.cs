using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mime;
using Jellyfin.Plugin.ExternalRatings.Core;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.ExternalRatings.Api;

/// <summary>
/// Read-only, admin-gated status endpoint for the plugin (spec §10.2). This is a Humble Object: it
/// contains no decision logic, only reads the configuration and delegates NFO detection to the
/// testable core.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("Plugins/ExternalRatings")]
[Produces(MediaTypeNames.Application.Json)]
public class StatusController : ControllerBase
{
    private readonly ILibraryManager _libraryManager;
    private readonly RatingEnrichmentService _enrichmentService;

    /// <summary>
    /// Initializes a new instance of the <see cref="StatusController"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager, supplied by the host container.</param>
    /// <param name="enrichmentService">The shared enrichment service, for live run status.</param>
    public StatusController(ILibraryManager libraryManager, RatingEnrichmentService enrichmentService)
    {
        _libraryManager = libraryManager;
        _enrichmentService = enrichmentService;
    }

    /// <summary>
    /// Gets the current plugin status.
    /// </summary>
    /// <returns>The plugin status.</returns>
    [HttpGet("Status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<StatusResponse> GetStatus()
    {
        var config = Plugin.Instance!.Configuration;

        var snapshots = _libraryManager.GetVirtualFolders().Select(ToSnapshot).ToList();
        var librariesWithNfo = NfoSaverDetector.Detect(snapshots, config.EnabledLibraries);
        var lastRun = _enrichmentService.GetStatusSnapshot();

        return new StatusResponse
        {
            DryRun = config.DryRun,
            ActiveResolverKey = config.ActiveResolverKey,
            ProcessedLevels = PluginConfigurationMapper.ParseLevels(config)
                .Select(level => level.ToString()).ToList(),
            DailyRequestLimit = config.DailyRequestLimit,
            WriteReasonPlan = "A (ItemUpdateType.None)",
            LibrariesWithNfoSaver = librariesWithNfo,
            NfoWritesPossible = librariesWithNfo.Count > 0,
            LastRun = lastRun
        };
    }

    private static LibraryNfoSnapshot ToSnapshot(VirtualFolderInfo folder)
    {
        var id = Guid.TryParse(folder.ItemId, out var parsed) ? parsed : Guid.Empty;
        var options = folder.LibraryOptions;
        IReadOnlyList<string>? savers = options?.MetadataSavers;
        return new LibraryNfoSnapshot(id, folder.Name, options?.SaveLocalMetadata ?? false, savers);
    }
}
