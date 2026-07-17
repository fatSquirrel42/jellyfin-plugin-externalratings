using System;

namespace Jellyfin.Plugin.ExternalRatings.Core.Abstractions;

/// <summary>
/// A minimal, Jellyfin-free reference to an item the pipeline operates on.
/// </summary>
/// <param name="ItemId">The Jellyfin item id.</param>
/// <param name="DisplayName">An optional display name for logging.</param>
internal readonly record struct RatingItemRef(Guid ItemId, string? DisplayName);
