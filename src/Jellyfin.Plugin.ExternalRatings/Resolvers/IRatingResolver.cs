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

    /// <summary>
    /// Gets the input providers this resolver supports, per item level, each list in descending
    /// priority order. Presence of a level key is the resolver's "I support this level" flag, and
    /// the list order is what <see cref="Core.InputIdSelector"/> picks by.
    /// </summary>
    IReadOnlyDictionary<ItemLevel, IReadOnlyList<string>> SupportedInputProviders { get; }

    /// <summary>Gets the external rating sources this resolver can return (its selectable-source capability).</summary>
    IReadOnlyCollection<RatingSourceInfo> SupportedRatingSources { get; }

    /// <summary>Resolves a single rating request.</summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The resolution result.</returns>
    Task<RatingResult> ResolveAsync(RatingRequest request, CancellationToken cancellationToken);
}
