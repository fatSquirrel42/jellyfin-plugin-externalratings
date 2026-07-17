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
    public IReadOnlyDictionary<ItemLevel, IReadOnlyCollection<string>> SupportedInputProviders { get; } =
        new Dictionary<ItemLevel, IReadOnlyCollection<string>>
        {
            [ItemLevel.Movie] = InputIdSelector.ProviderPriority(ItemLevel.Movie),
            [ItemLevel.Series] = InputIdSelector.ProviderPriority(ItemLevel.Series)
        };

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
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return LogAndError("single", url, "transport: " + ex.GetType().Name);
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
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return ChunkError(chunk, url, "transport: " + ex.GetType().Name);
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

        if (rating?.Value is not double value)
        {
            _logger.LogDebug("mdblist {Mode} {Url} -> NoMatch", mode, MdblistUrls.MaskApiKey(url));
            return RatingResult.NoMatch();
        }

        if (value is < 0 or > 10)
        {
            // H14: Found ⇒ 0 ≤ score ≤ 10. An out-of-range value is a resolver/source bug.
            _logger.LogError(
                "mdblist {Mode} {Url} returned out-of-range score {Score}",
                mode,
                MdblistUrls.MaskApiKey(url),
                value.ToString(CultureInfo.InvariantCulture));
            return RatingResult.ForError("out-of-range score");
        }

        return RatingResult.ForScore((float)value);
    }

    private Dictionary<string, RatingResult> ChunkError(
        IReadOnlyList<string> chunk,
        string url,
        string detail)
    {
        _logger.LogError("mdblist batch {Url} chunk error: {Detail}", MdblistUrls.MaskApiKey(url), detail);
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
}
