using System;

namespace Jellyfin.Plugin.ExternalRatings.Core.Abstractions;

/// <summary>
/// One backed-up original rating (spec §9.1).
/// </summary>
/// <param name="OriginalRating">The item's original community rating (nullable — it may have had none).</param>
/// <param name="BackedUpAt">When the backup was taken.</param>
internal readonly record struct BackupEntry(float? OriginalRating, DateTimeOffset BackedUpAt);
