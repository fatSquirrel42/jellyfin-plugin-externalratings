using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ExternalRatings.Core;
using Jellyfin.Plugin.ExternalRatings.Resolvers.Mdblist;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ExternalRatings.Resolvers;

/// <summary>
/// Resolves an external community score (initially MyAnimeList) through the mdblist API, both
/// single-item and batch (spec §8, §5.2). Pure HTTP + parse: budget counting and circuit-breaker
/// wiring live in the pipeline. The API key travels in the query string and is masked in every log
/// line and exception (H9).
/// </summary>
internal sealed class MdblistResolver : IBatchRatingResolver
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    // The rating sources mdblist exposes in a media object's `ratings[].source`, with their native
    // scales (for the UI conversion hint). `rescaled` marks sources whose native scale differs from
    // Jellyfin's 0–10. rogerebert is deliberately omitted: mdblist returns its native `value` but no
    // unified `score`, so ToResult always yields NoMatch — it would be a dead choice in the UI.
    private static readonly IReadOnlyCollection<RatingSourceInfo> Sources = new[]
    {
        new RatingSourceInfo("imdb", "IMDb", "0–10", false),
        new RatingSourceInfo("myanimelist", "MyAnimeList", "0–10", false),
        new RatingSourceInfo("metacriticuser", "Metacritic (user score)", "0–10", false),
        new RatingSourceInfo("trakt", "Trakt", "0–100", true),
        new RatingSourceInfo("tmdb", "TMDb", "0–100", true),
        new RatingSourceInfo("metacritic", "Metacritic (critic score)", "0–100", true),
        new RatingSourceInfo("tomatoes", "Rotten Tomatoes (critics)", "0–100", true),
        new RatingSourceInfo("popcorn", "Rotten Tomatoes (audience)", "0–100", true),
        new RatingSourceInfo("letterboxd", "Letterboxd", "0–5", true)
    };

    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly ILogger<MdblistResolver> _logger;

    /// <summary>Initializes a new instance of the <see cref="MdblistResolver"/> class.</summary>
    /// <param name="httpClient">The HTTP client (base address <c>https://api.mdblist.com</c>).</param>
    /// <param name="apiKey">The mdblist API key.</param>
    /// <param name="logger">The logger.</param>
    public MdblistResolver(HttpClient httpClient, string apiKey, ILogger<MdblistResolver> logger)
    {
        _httpClient = httpClient;
        _apiKey = apiKey;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Key => "mdblist";

    /// <inheritdoc />
    public string DisplayName => "mdblist";

    /// <inheritdoc />
    public int MaxBatchSize => 200;

    /// <inheritdoc />
    public IReadOnlyDictionary<ItemLevel, IReadOnlyList<string>> SupportedInputProviders { get; } =
        new Dictionary<ItemLevel, IReadOnlyList<string>>
        {
            // Descending priority. Season/Episode are absent on purpose: mdblist exposes no
            // per-episode scores on the free tier (see docs/level-support-diagnosis.md §1).
            [ItemLevel.Movie] = new[] { "Tmdb", "Imdb" },
            [ItemLevel.Series] = new[] { "Tmdb", "Imdb", "Tvdb" }
        };

    /// <inheritdoc />
    public IReadOnlyCollection<RatingSourceInfo> SupportedRatingSources => Sources;

    /// <inheritdoc />
    public async Task<RatingResult> ResolveAsync(RatingRequest request, CancellationToken cancellationToken)
    {
        var type = MdblistUrls.MapType(request.Level);
        if (type is null)
        {
            return RatingResult.NotSupported();
        }

        var provider = MdblistUrls.MapProvider(request.InputProvider);
        var url = MdblistUrls.BuildSingle(provider, type, request.InputId, _apiKey);

        HttpResponseMessage response;
        string body;
        try
        {
            response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
            body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Genuine caller cancellation must propagate: the pipeline rethrows it and releases the
            // breaker's half-open probe. Only a real transport fault — including an HttpClient request
            // timeout, whose token is not the caller's — is turned into an error result below.
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return TransportError("single", url, ex);
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            _logger.LogDebug("mdblist single {Url} -> NoMatch (404)", MdblistUrls.MaskApiKey(url));
            return RatingResult.NoMatch();
        }

        if (!response.IsSuccessStatusCode)
        {
            return LogAndError("single", url, "status " + (int)response.StatusCode);
        }

        MdblistMediaResponse? media;
        try
        {
            media = JsonSerializer.Deserialize<MdblistMediaResponse>(body, JsonOptions);
        }
        catch (JsonException)
        {
            return LogAndError("single", url, "parse");
        }

        return ToResult(media, request.TargetSource, url, "single");
    }

    /// <summary>
    /// Reads the mdblist <c>/user</c> account endpoint and returns the number of API requests already
    /// used today (the §7.3 cold-start budget read). The counts live in the response body, not in
    /// headers. Any transport, status, or parse failure returns <see langword="null"/> so the run
    /// falls back to the local counter rather than aborting.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The used request count, or <see langword="null"/> if it could not be read.</returns>
    public async Task<int?> GetUsedRequestCountAsync(CancellationToken cancellationToken)
    {
        var url = MdblistUrls.BuildUser(_apiKey);

        HttpResponseMessage response;
        string body;
        try
        {
            response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
            body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            _logger.LogWarning("mdblist /user cold-start read failed ({Error}); using the local budget counter", ex.GetType().Name);
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("mdblist /user returned status {Status}; using the local budget counter", (int)response.StatusCode);
            return null;
        }

        try
        {
            var user = JsonSerializer.Deserialize<MdblistUserResponse>(body, JsonOptions);
            return user?.ApiRequestsCount;
        }
        catch (JsonException)
        {
            _logger.LogWarning("mdblist /user body could not be parsed; using the local budget counter");
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, RatingResult>> ResolveBatchAsync(
        ItemLevel level,
        string inputProvider,
        IReadOnlyCollection<string> inputIds,
        string targetSource,
        CancellationToken cancellationToken)
    {
        var results = new Dictionary<string, RatingResult>(StringComparer.Ordinal);
        var type = MdblistUrls.MapType(level);
        if (type is null)
        {
            foreach (var id in inputIds)
            {
                results[id] = RatingResult.NotSupported();
            }

            return results;
        }

        var provider = MdblistUrls.MapProvider(inputProvider);

        foreach (var chunk in Chunk(inputIds, MaxBatchSize))
        {
            var chunkResults = await ResolveChunkAsync(provider, type, chunk, targetSource, cancellationToken)
                .ConfigureAwait(false);
            foreach (var pair in chunkResults)
            {
                results[pair.Key] = pair.Value;
            }
        }

        return results;
    }

    private static IEnumerable<IReadOnlyList<string>> Chunk(IReadOnlyCollection<string> ids, int size)
    {
        var current = new List<string>(size);
        foreach (var id in ids)
        {
            current.Add(id);
            if (current.Count == size)
            {
                yield return current;
                current = new List<string>(size);
            }
        }

        if (current.Count > 0)
        {
            yield return current;
        }
    }

    private async Task<Dictionary<string, RatingResult>> ResolveChunkAsync(
        string provider,
        string type,
        IReadOnlyList<string> chunk,
        string targetSource,
        CancellationToken cancellationToken)
    {
        var url = MdblistUrls.BuildBatch(provider, type, _apiKey);
        var payload = JsonSerializer.Serialize(new { ids = chunk });

        HttpResponseMessage response;
        string body;
        try
        {
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            response = await _httpClient.PostAsync(url, content, cancellationToken).ConfigureAwait(false);
            body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Propagate genuine caller cancellation (see ResolveAsync); only real transport faults and
            // HttpClient timeouts become per-id error results.
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return ChunkTransportError(chunk, url, ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            return ChunkError(chunk, url, "status " + (int)response.StatusCode);
        }

        List<MdblistMediaResponse>? media;
        try
        {
            media = JsonSerializer.Deserialize<List<MdblistMediaResponse>>(body, JsonOptions);
        }
        catch (JsonException)
        {
            return ChunkError(chunk, url, "parse");
        }

        var byId = new Dictionary<string, MdblistMediaResponse>(StringComparer.Ordinal);
        foreach (var item in media ?? new List<MdblistMediaResponse>())
        {
            var id = item.GetId(provider);
            if (id is not null)
            {
                byId[id] = item;
            }
        }

        var results = new Dictionary<string, RatingResult>(StringComparer.Ordinal);
        foreach (var id in chunk)
        {
            results[id] = byId.TryGetValue(id, out var item)
                ? ToResult(item, targetSource, url, "batch")
                : RatingResult.NoMatch();
        }

        return results;
    }

    private RatingResult ToResult(MdblistMediaResponse? media, string targetSource, string url, string mode)
    {
        var rating = media?.Ratings?
            .FirstOrDefault(r => string.Equals(r.Source, targetSource, StringComparison.OrdinalIgnoreCase));

        // mdblist normalizes every source onto a unified 0–100 `score`, so reading it (and dividing by
        // 10) yields Jellyfin's 0–10 CommunityRating for any source without a per-source scale table. A
        // source can carry a native `value` but no `score` (for example rogerebert) — treated as NoMatch.
        if (rating?.Score is not double score)
        {
            _logger.LogDebug("mdblist {Mode} {Url} -> NoMatch", mode, MdblistUrls.MaskApiKey(url));
            return RatingResult.NoMatch();
        }

        if (score is < 0 or > 100)
        {
            // H14: Found ⇒ 0 ≤ score ≤ 10 after normalization. An out-of-range source score is a bug.
            _logger.LogError(
                "mdblist {Mode} {Url} returned out-of-range score {Score}",
                mode,
                MdblistUrls.MaskApiKey(url),
                score.ToString(CultureInfo.InvariantCulture));
            return RatingResult.ForError("out-of-range score");
        }

        return RatingResult.ForScore((float)(score / 10.0));
    }

    private Dictionary<string, RatingResult> ChunkError(
        IReadOnlyList<string> chunk,
        string url,
        string detail)
    {
        _logger.LogError("mdblist batch {Url} chunk error: {Detail}", MdblistUrls.MaskApiKey(url), detail);
        return ChunkResults(chunk, detail);
    }

    // Transport failures log the full exception (cause + stack) so a DNS/TLS/connection fault is
    // distinguishable; the URL is masked (H9) and .NET transport exceptions do not embed the request
    // URI, so the API key is not leaked. The short detail still feeds RatingResult for the status view.
    private Dictionary<string, RatingResult> ChunkTransportError(IReadOnlyList<string> chunk, string url, Exception ex)
    {
        _logger.LogError(ex, "mdblist batch {Url} transport failure", MdblistUrls.MaskApiKey(url));
        return ChunkResults(chunk, "transport: " + ex.GetType().Name);
    }

    private static Dictionary<string, RatingResult> ChunkResults(IReadOnlyList<string> chunk, string detail)
    {
        var results = new Dictionary<string, RatingResult>(StringComparer.Ordinal);
        foreach (var id in chunk)
        {
            results[id] = RatingResult.ForError(detail);
        }

        return results;
    }

    private RatingResult LogAndError(string mode, string url, string detail)
    {
        _logger.LogError("mdblist {Mode} {Url} error: {Detail}", mode, MdblistUrls.MaskApiKey(url), detail);
        return RatingResult.ForError(detail);
    }

    private RatingResult TransportError(string mode, string url, Exception ex)
    {
        _logger.LogError(ex, "mdblist {Mode} {Url} transport failure", mode, MdblistUrls.MaskApiKey(url));
        return RatingResult.ForError("transport: " + ex.GetType().Name);
    }
}
