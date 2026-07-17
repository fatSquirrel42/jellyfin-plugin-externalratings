using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ExternalRatings.Core;
using Jellyfin.Plugin.ExternalRatings.Resolvers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Jellyfin.Plugin.ExternalRatings.Tests;

/// <summary>
/// Opt-in live V1 verification (spec §11) against the real mdblist API. Skipped unless the
/// MDBLIST_API_KEY environment variable is set. Run explicitly with
/// <c>dotnet test --filter "Category=Live"</c>. It captures real response shapes (scrubbed) to a
/// temp folder and measures the two undocumented facts: the batch quota cost and the no-match shape.
/// The API key is never written to a file or the test output.
/// </summary>
[Trait("Category", "Live")]
public class MdblistLiveTests
{
    private const string Host = "https://api.mdblist.com/";
    private readonly ITestOutputHelper _output;

    public MdblistLiveTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static string? ApiKey => Environment.GetEnvironmentVariable("MDBLIST_API_KEY");

    [SkippableFact]
    public async Task CaptureV1()
    {
        var key = ApiKey;
        Skip.If(string.IsNullOrWhiteSpace(key), "MDBLIST_API_KEY not set; live V1 capture skipped.");

        var captureDir = Path.Combine(Path.GetTempPath(), "mdblist-capture");
        Directory.CreateDirectory(captureDir);
        _output.WriteLine("Capture dir: " + captureDir);

        using var client = new HttpClient { BaseAddress = new Uri(Host) };

        // 1. /user cold start — free-tier limit + used count.
        var user = await GetAsync(client, $"user?apikey={key}", key!, captureDir, "user.json");
        _output.WriteLine($"/user: status={user.Status} remaining={user.Remaining} limitHeader={user.Limit}");

        // 2. Known anime movie (Spirited Away) and show (Cowboy Bebop) via imdb.
        var movie = await GetAsync(client, $"imdb/movie/tt0245429?apikey={key}", key!, captureDir, "movie_found.live.json");
        _output.WriteLine($"movie: status={movie.Status} remaining={movie.Remaining}");
        var show = await GetAsync(client, $"imdb/show/tt0213338?apikey={key}", key!, captureDir, "show_found.live.json");
        _output.WriteLine($"show: status={show.Status} remaining={show.Remaining}");

        // 3. Unknown id — record 404 vs 200-with-empty-ratings (no-match shape).
        var unknown = await GetAsync(client, $"tmdb/movie/999999999?apikey={key}", key!, captureDir, "no_match.live.json");
        _output.WriteLine($"unknown-id: status={unknown.Status} remaining={unknown.Remaining}");

        // 4. tvdb + movie probe (semantically unlikely; syntactically allowed).
        var tvdbMovie = await GetAsync(client, $"tvdb/movie/578?apikey={key}", key!, captureDir, "tvdb_movie.live.json");
        _output.WriteLine($"tvdb/movie: status={tvdbMovie.Status} remaining={tvdbMovie.Remaining}");

        // 5. Batch POST — capture shape + measure quota cost via X-RateLimit-Remaining delta.
        var remainingBeforeBatch = tvdbMovie.Remaining;
        var batch = await PostAsync(
            client,
            $"tmdb/movie?apikey={key}",
            "{\"ids\":[\"129\",\"578\"]}",
            key!,
            captureDir,
            "batch.live.json");
        _output.WriteLine($"batch: status={batch.Status} remaining={batch.Remaining}");

        if (remainingBeforeBatch is int before && batch.Remaining is int after)
        {
            _output.WriteLine($"BATCH QUOTA COST (remaining delta over one batch POST): {before - after}");
        }

        // 6. Resolver end-to-end sanity (uses the production parse path).
        var resolver = new MdblistResolver(
            new HttpClient { BaseAddress = new Uri(Host) },
            key!,
            NullLogger<MdblistResolver>.Instance);
        var resolved = await resolver.ResolveAsync(
            new RatingRequest(ItemLevel.Movie, "Imdb", "tt0245429", "myanimelist"),
            CancellationToken.None);
        _output.WriteLine($"resolver movie -> {resolved.Resolution} score={resolved.Score}");

        // No hard asserts: this is a capture/measurement run. Reaching here without throwing is success.
        Assert.True(true);
    }

    private async Task<Capture> GetAsync(HttpClient client, string url, string key, string dir, string file)
    {
        using var response = await client.GetAsync(url, CancellationToken.None);
        var body = await response.Content.ReadAsStringAsync(CancellationToken.None);
        Write(dir, file, body, key, response);
        return new Capture((int)response.StatusCode, Header(response, "X-RateLimit-Remaining"), Header(response, "X-RateLimit-Limit"));
    }

    private async Task<Capture> PostAsync(HttpClient client, string url, string json, string key, string dir, string file)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(url, content, CancellationToken.None);
        var body = await response.Content.ReadAsStringAsync(CancellationToken.None);
        Write(dir, file, body, key, response);
        return new Capture((int)response.StatusCode, Header(response, "X-RateLimit-Remaining"), Header(response, "X-RateLimit-Limit"));
    }

    private static void Write(string dir, string file, string body, string key, HttpResponseMessage response)
    {
        // Bodies do not contain the key (it rides in the query string), but scrub defensively.
        var scrubbed = body.Replace(key, "***", StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(dir, file), scrubbed);

        var headers = string.Join(
            Environment.NewLine,
            response.Headers
                .Where(h => h.Key.StartsWith("X-RateLimit", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(h.Key, "Retry-After", StringComparison.OrdinalIgnoreCase))
                .Select(h => $"{h.Key}: {string.Join(",", h.Value)}"));
        File.WriteAllText(Path.Combine(dir, file + ".headers.txt"), headers);
    }

    private static int? Header(HttpResponseMessage response, string name)
    {
        if (response.Headers.TryGetValues(name, out var values)
            && int.TryParse(values.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private sealed record Capture(int Status, int? Remaining, int? Limit);
}
