using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ExternalRatings.Core;
using Jellyfin.Plugin.ExternalRatings.Resolvers.Imdb;

namespace Jellyfin.Plugin.ExternalRatings.Resolvers;

/// <summary>
/// Resolves IMDb community scores from IMDb's own <c>title.ratings</c> dataset held locally, with
/// no API, no key and no request quota.
/// </summary>
/// <remarks>
/// <para>
/// Unlike <see cref="MdblistResolver"/> this reaches every level the dataset covers, because an
/// IMDb rating row exists for episodes exactly as it does for films and series. Matching is by the
/// item's own IMDb id only — never by (series, season, episode) position, which diverges between
/// TheTVDB's and IMDb's numbering and would silently write the wrong episode's score
/// (see <c>docs/level-support-diagnosis.md</c> §3).
/// </para>
/// <para>
/// Season is the one level that is not a direct lookup: IMDb has no season entity, so the score is
/// the mean of the season's episodes, taken from <see cref="RatingRequest.MemberInputIds"/> because
/// only the host knows which episodes those are.
/// </para>
/// <para>
/// Deliberately not an <see cref="IBatchRatingResolver"/>. Batching exists to amortise HTTP calls;
/// a lookup here is a binary search over an in-memory array, so the prefetch phase has nothing to
/// save and is skipped.
/// </para>
/// </remarks>
internal sealed class ImdbDatasetResolver : IRatingResolver
{
    /// <summary>The stable configuration key that selects this resolver.</summary>
    public const string ResolverKey = "imdb-dataset";

    /// <summary>The single rating source this resolver can produce.</summary>
    public const string ImdbSource = "imdb";

    private static readonly IReadOnlyCollection<RatingSourceInfo> Sources = new[]
    {
        new RatingSourceInfo(ImdbSource, "IMDb", "0–10", false)
    };

    private static readonly string[] ImdbOnly = { "Imdb" };

    private readonly ImdbRatingsDataset _dataset;

    /// <summary>Initializes a new instance of the <see cref="ImdbDatasetResolver"/> class.</summary>
    /// <param name="dataset">The locally cached dataset.</param>
    public ImdbDatasetResolver(ImdbRatingsDataset dataset)
    {
        _dataset = dataset;
    }

    /// <inheritdoc />
    public string Key => ResolverKey;

    /// <inheritdoc />
    public string DisplayName => "IMDb (offline dataset)";

    /// <inheritdoc />
    public IReadOnlyDictionary<ItemLevel, IReadOnlyList<string>> SupportedInputProviders { get; } =
        new Dictionary<ItemLevel, IReadOnlyList<string>>
        {
            [ItemLevel.Movie] = ImdbOnly,
            [ItemLevel.Series] = ImdbOnly,
            [ItemLevel.Season] = ImdbOnly,
            [ItemLevel.Episode] = ImdbOnly
        };

    /// <inheritdoc />
    public IReadOnlyCollection<RatingSourceInfo> SupportedRatingSources => Sources;

    /// <inheritdoc />
    public async Task<RatingResult> ResolveAsync(RatingRequest request, CancellationToken cancellationToken)
    {
        if (!SupportedInputProviders.ContainsKey(request.Level))
        {
            return RatingResult.NotSupported();
        }

        // A config left over from another resolver can still name a source this one cannot serve.
        // Report it as an error rather than a no-match: an error never writes and is never cached
        // persistently, so a misconfiguration cannot clear ratings across the library.
        if (!string.Equals(request.TargetSource, ImdbSource, StringComparison.OrdinalIgnoreCase))
        {
            return RatingResult.ForError($"unsupported source '{request.TargetSource}'");
        }

        if (!string.Equals(request.InputProvider, "Imdb", StringComparison.OrdinalIgnoreCase))
        {
            return RatingResult.ForError($"unsupported input provider '{request.InputProvider}'");
        }

        var index = await _dataset.GetIndexAsync(cancellationToken).ConfigureAwait(false);

        // No dataset yet (first run offline, say). Treat it as an error, never a no-match: a
        // no-match is cached and, under ClearField, wipes the field.
        if (index.Count == 0)
        {
            return RatingResult.ForError("the IMDb dataset is not available");
        }

        return request.Level == ItemLevel.Season
            ? ResolveSeason(request, index)
            : index.TryGetRating(request.InputId, out var rating)
                ? RatingResult.ForScore(rating)
                : RatingResult.NoMatch();
    }

    /// <summary>
    /// A season has no id of its own to look up, so its score is the mean of its episodes'.
    /// </summary>
    /// <param name="request">The request, whose <see cref="RatingRequest.MemberInputIds"/> holds the episodes.</param>
    /// <param name="index">The ratings index.</param>
    /// <returns>The aggregated result.</returns>
    private RatingResult ResolveSeason(RatingRequest request, ImdbRatingsIndex index)
    {
        // One entry per episode the season holds; an episode with no id of its own is a blank,
        // which cannot resolve and therefore fails the completeness rule below.
        var members = request.MemberInputIds;
        if (members is null || members.Count == 0)
        {
            return RatingResult.NoMatch();
        }

        var found = new List<float>(members.Count);
        foreach (var memberId in members)
        {
            if (index.TryGetRating(memberId, out var rating))
            {
                found.Add(rating);
            }
        }

        var average = SeasonRatingAggregator.Average(found, members.Count);
        return average is float score ? RatingResult.ForScore(score) : RatingResult.NoMatch();
    }
}
