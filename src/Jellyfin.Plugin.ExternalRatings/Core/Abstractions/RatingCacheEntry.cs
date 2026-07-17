using System;
using Jellyfin.Plugin.ExternalRatings.Resolvers;

namespace Jellyfin.Plugin.ExternalRatings.Core.Abstractions;

/// <summary>
/// A cached resolution. Only <see cref="RatingResolution.Found"/> and <see cref="RatingResolution.NoMatch"/>
/// are ever persisted; <see cref="RatingResolution.Error"/> is in-memory only (spec §6 table).
/// </summary>
/// <param name="Kind">The cached resolution kind.</param>
/// <param name="Score">The score for a found entry, otherwise <see langword="null"/>.</param>
/// <param name="ExpiresAt">The absolute expiry instant.</param>
internal readonly record struct RatingCacheEntry(RatingResolution Kind, float? Score, DateTimeOffset ExpiresAt);
