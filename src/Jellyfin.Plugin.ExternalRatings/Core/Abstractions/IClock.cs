using System;

namespace Jellyfin.Plugin.ExternalRatings.Core.Abstractions;

/// <summary>
/// A test seam for the current time. The core never reads the system clock directly (spec §12.1).
/// </summary>
internal interface IClock
{
    /// <summary>Gets the current UTC instant.</summary>
    DateTimeOffset UtcNow { get; }
}
