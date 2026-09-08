using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ExternalRatings.Core;
using Jellyfin.Plugin.ExternalRatings.Resolvers.Imdb;
using Microsoft.Extensions.Logging;

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
    private readonly ImdbEpisodeDataset _episodes;
    private readonly ILogger _logger;

    /// <summary>Initializes a new instance of the <see cref="ImdbDatasetResolver"/> class.</summary>
    /// <param name="dataset">The locally cached ratings dataset.</param>
    /// <param name="episodes">The locally cached episode map, used to resolve seasons.</param>
    /// <param name="logger">The logger.</param>
    public ImdbDatasetResolver(ImdbRatingsDataset dataset, ImdbEpisodeDataset episodes, ILogger logger)
    {
        _dataset = dataset;
        _episodes = episodes;
        _logger = logger;
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
            ? await ResolveSeasonAsync(request, index, cancellationToken).ConfigureAwait(false)
            : index.TryGetRating(request.InputId, out var rating)
                ? RatingResult.ForScore(rating)
                : RatingResult.NoMatch();
    }

    /// <summary>
    /// Scores a season as the mean of *IMDb's* episodes for it, not of the episodes on disk.
    /// </summary>
    /// <remarks>
    /// Which IMDb season that is comes from the episodes themselves: their ids map to a
    /// (series, season) pair. Never from the Jellyfin season number — translating that is the
    /// positional guess rejected in <c>docs/level-support-diagnosis.md</c> §3, and it is wrong for
    /// any show whose numbering diverges. The approach also self-checks: if the members land in
    /// more than one IMDb season, this Jellyfin season has no IMDb counterpart and nothing is
    /// written rather than something guessed.
    /// </remarks>
    /// <param name="request">The request; <see cref="RatingRequest.MemberInputIds"/> identifies the season.</param>
    /// <param name="index">The ratings index.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The aggregated result.</returns>
    private async Task<RatingResult> ResolveSeasonAsync(
        RatingRequest request,
        ImdbRatingsIndex index,
        CancellationToken cancellationToken)
    {
        var members = request.MemberInputIds;
        if (members is null || members.Count == 0)
        {
            return RatingResult.NoMatch();
        }

        // The map's series filter is maintained by the host through EnsureCoversAsync — only it
        // knows which series an episode belongs to before the map exists.
        var map = await _episodes.GetMapAsync(cancellationToken).ConfigureAwait(false);

        // Same reasoning as the ratings index above: an unavailable episode map is an error, never
        // a no-match, or a failed download would clear every season rating in the library.
        if (map.EpisodeCount == 0)
        {
            return RatingResult.ForError("the IMDb episode dataset is not available");
        }

        var seasons = new HashSet<(long Parent, int Season)>();
        foreach (var memberId in members)
        {
            if (map.TryGetSeasonOf(memberId, out var season))
            {
                seasons.Add(season);
            }
        }

        if (seasons.Count == 0)
        {
            // Nothing to anchor on. IMDb has no season 0 at all, so a specials season lands here
            // by construction.
            _logger.LogDebug(
                "No IMDb season could be identified for {InputId} from {MemberCount} member(s)",
                request.InputId,
                members.Count);
            return RatingResult.NoMatch();
        }

        if (seasons.Count > 1)
        {
            _logger.LogWarning(
                "{InputId} spans {SeasonCount} IMDb seasons, so it has no single counterpart; leaving it unscored",
                request.InputId,
                seasons.Count);
            return RatingResult.NoMatch();
        }

        var identified = default((long Parent, int Season));
        foreach (var season in seasons)
        {
            identified = season;
        }

        var episodes = map.GetEpisodesOf(identified.Parent, identified.Season);
        var found = new List<float>(episodes.Count);
        foreach (var episode in episodes)
        {
            if (index.TryGetRating(episode, out var rating))
            {
                found.Add(rating);
            }
        }

        var average = SeasonRatingAggregator.Average(found);
        return average is float score ? RatingResult.ForScore(score) : RatingResult.NoMatch();
    }
}
