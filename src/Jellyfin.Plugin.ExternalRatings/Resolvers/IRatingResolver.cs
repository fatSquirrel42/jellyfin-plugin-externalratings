using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ExternalRatings.Core;

namespace Jellyfin.Plugin.ExternalRatings.Resolvers;

/// <summary>
/// Resolves an external community score for a single item (spec §5.2).
/// </summary>
internal interface IRatingResolver
{
    /// <summary>Gets the stable resolver key (e.g. "mdblist").</summary>
    string Key { get; }

    /// <summary>Gets the human-readable display name.</summary>
    string DisplayName { get; }

    /// <summary>Gets the input providers this resolver supports, per item level.</summary>
    IReadOnlyDictionary<ItemLevel, IReadOnlyCollection<string>> SupportedInputProviders { get; }

    /// <summary>Resolves a single rating request.</summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The resolution result.</returns>
    Task<RatingResult> ResolveAsync(RatingRequest request, CancellationToken cancellationToken);
}
