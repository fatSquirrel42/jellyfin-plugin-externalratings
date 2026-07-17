namespace Jellyfin.Plugin.ExternalRatings.Core;

/// <summary>
/// The single input id chosen for an item, plus whether other level-allowed ids went unused (H12).
/// </summary>
/// <param name="Provider">The canonical provider key of the chosen id.</param>
/// <param name="Id">The chosen id value.</param>
/// <param name="HasUnusedAlternatives">Whether other level-allowed provider ids were present but not chosen.</param>
internal readonly record struct InputIdSelection(string Provider, string Id, bool HasUnusedAlternatives);
