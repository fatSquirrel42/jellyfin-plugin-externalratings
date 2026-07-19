using Jellyfin.Plugin.ExternalRatings.Core;
using Jellyfin.Plugin.ExternalRatings.Resolvers;

namespace Jellyfin.Plugin.ExternalRatings.Tests.Fakes;

/// <summary>
/// A scriptable <see cref="IRatingResolver"/> that records requests and can gate/await to exercise
/// concurrency (single-flight) behaviour.
/// </summary>
internal sealed class StubRatingResolver : IRatingResolver
{
    private readonly Func<RatingRequest, CancellationToken, Task<RatingResult>> _resolve;

    public StubRatingResolver(RatingResult result)
        : this((_, _) => Task.FromResult(result))
    {
    }

    public StubRatingResolver(Func<RatingRequest, RatingResult> resolve)
        : this((req, _) => Task.FromResult(resolve(req)))
    {
    }

    public StubRatingResolver(Func<RatingRequest, CancellationToken, Task<RatingResult>> resolve)
        => _resolve = resolve;

    public string Key { get; set; } = "stub";

    public string DisplayName { get; set; } = "Stub Resolver";

    public int CallCount { get; private set; }

    public List<RatingRequest> Requests { get; } = new();

    public IReadOnlyDictionary<ItemLevel, IReadOnlyCollection<string>> SupportedInputProviders { get; set; } =
        new Dictionary<ItemLevel, IReadOnlyCollection<string>>
        {
            [ItemLevel.Movie] = new[] { "Tmdb", "Imdb" },
            [ItemLevel.Series] = new[] { "Tmdb", "Imdb", "Tvdb" }
        };

    public IReadOnlyCollection<RatingSourceInfo> SupportedRatingSources { get; set; } =
        new[] { new RatingSourceInfo("myanimelist", "MyAnimeList", "0–10", false) };

    public async Task<RatingResult> ResolveAsync(RatingRequest request, CancellationToken cancellationToken)
    {
        lock (Requests)
        {
            CallCount++;
            Requests.Add(request);
        }

        return await _resolve(request, cancellationToken).ConfigureAwait(false);
    }
}
