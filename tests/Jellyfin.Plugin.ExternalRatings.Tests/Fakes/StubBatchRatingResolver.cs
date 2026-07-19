using Jellyfin.Plugin.ExternalRatings.Core;
using Jellyfin.Plugin.ExternalRatings.Resolvers;

namespace Jellyfin.Plugin.ExternalRatings.Tests.Fakes;

/// <summary>
/// A scriptable <see cref="IBatchRatingResolver"/> that records each batch call and returns a
/// per-id result computed by the supplied function. <see cref="ResolveAsync"/> throws — the
/// prefetch path must go through <see cref="ResolveBatchAsync"/>.
/// </summary>
internal sealed class StubBatchRatingResolver : IBatchRatingResolver
{
    private readonly Func<ItemLevel, string, IReadOnlyCollection<string>, string, IReadOnlyDictionary<string, RatingResult>> _batch;

    public StubBatchRatingResolver(Func<string, RatingResult> perId)
        : this((_, _, ids, _) =>
        {
            var map = new Dictionary<string, RatingResult>(StringComparer.Ordinal);
            foreach (var id in ids)
            {
                map[id] = perId(id);
            }

            return map;
        })
    {
    }

    public StubBatchRatingResolver(
        Func<ItemLevel, string, IReadOnlyCollection<string>, string, IReadOnlyDictionary<string, RatingResult>> batch)
        => _batch = batch;

    public string Key { get; set; } = "mdblist";

    public string DisplayName => "stub-batch";

    public int MaxBatchSize { get; set; } = 200;

    public IReadOnlyDictionary<ItemLevel, IReadOnlyCollection<string>> SupportedInputProviders { get; set; } =
        new Dictionary<ItemLevel, IReadOnlyCollection<string>>
        {
            [ItemLevel.Movie] = new[] { "Tmdb", "Imdb" },
            [ItemLevel.Series] = new[] { "Tmdb", "Imdb", "Tvdb" }
        };

    public IReadOnlyCollection<RatingSourceInfo> SupportedRatingSources { get; set; } =
        new[] { new RatingSourceInfo("myanimelist", "MyAnimeList", "0–10", false) };

    public List<BatchCall> BatchCalls { get; } = new();

    public Task<RatingResult> ResolveAsync(RatingRequest request, CancellationToken cancellationToken)
        => throw new InvalidOperationException("Prefetch must resolve via ResolveBatchAsync, not ResolveAsync.");

    public Task<IReadOnlyDictionary<string, RatingResult>> ResolveBatchAsync(
        ItemLevel level,
        string inputProvider,
        IReadOnlyCollection<string> inputIds,
        string targetSource,
        CancellationToken cancellationToken)
    {
        BatchCalls.Add(new BatchCall(level, inputProvider, inputIds.ToArray(), targetSource));
        return Task.FromResult(_batch(level, inputProvider, inputIds, targetSource));
    }
}

/// <summary>A recorded batch call.</summary>
internal sealed record BatchCall(ItemLevel Level, string Provider, string[] Ids, string TargetSource);
