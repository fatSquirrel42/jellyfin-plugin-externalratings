namespace Jellyfin.Plugin.ExternalRatings.Api;

/// <summary>
/// The result of a "Restore all" action (spec §9.2, §15 step 10): how many backup entries were
/// processed (originals written back and backups cleared).
/// </summary>
public class RestoreResult
{
    /// <summary>
    /// Gets the number of backup entries restored.
    /// </summary>
    public int RestoredCount { get; init; }
}
