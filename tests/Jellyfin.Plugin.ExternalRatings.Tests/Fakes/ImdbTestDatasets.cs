using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Jellyfin.Plugin.ExternalRatings.Resolvers.Imdb;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.ExternalRatings.Tests.Fakes;

/// <summary>
/// A working pair of IMDb datasets — ratings and episode map — backed by canned gzipped TSVs in a
/// throwaway directory, for tests that need them to work but do not care how.
/// </summary>
/// <remarks>
/// The handler dispatches on URL: both files come from <c>datasets.imdbws.com</c>, so answering
/// every request with the same payload (as the earlier single-file fake did) would feed the ratings
/// TSV to the episode parser.
/// </remarks>
internal sealed class ImdbTestDatasets : IDisposable
{
    /// <summary>Default rows: Breaking Bad, its episode "Ozymandias", and a Diabolical episode.</summary>
    public const string DefaultRatingRows =
        "tt0903747\t9.5\t2671907\n" +
        "tt2301451\t9.5\t507558\n" +
        "tt16364366\t6.8\t3217\n";

    /// <summary>The two rated episodes above, both placed in Breaking Bad's season 5.</summary>
    public const string DefaultEpisodeRows =
        "tt2301451\ttt0903747\t5\t14\n" +
        "tt16364366\ttt0903747\t5\t15\n";

    private bool _disposed;

    private ImdbTestDatasets(
        string directory,
        FakeHttpMessageHandler handler,
        ImdbRatingsDataset ratings,
        ImdbEpisodeDataset episodes)
    {
        Directory = directory;
        Handler = handler;
        Ratings = ratings;
        Episodes = episodes;
    }

    /// <summary>Gets the throwaway cache directory both datasets were given.</summary>
    public string Directory { get; }

    /// <summary>Gets the handler, so a test can count downloads per URL.</summary>
    public FakeHttpMessageHandler Handler { get; }

    /// <summary>Gets the ratings dataset.</summary>
    public ImdbRatingsDataset Ratings { get; }

    /// <summary>Gets the episode map dataset.</summary>
    public ImdbEpisodeDataset Episodes { get; }

    /// <summary>Builds a fresh pair in its own directory.</summary>
    /// <param name="ratingRows">Rows for <c>title.ratings</c>, header excluded.</param>
    /// <param name="episodeRows">Rows for <c>title.episode</c>, header excluded.</param>
    /// <param name="episodesOffline">When set, the episode file fails to download.</param>
    /// <returns>The pair, to be disposed by the caller.</returns>
    public static ImdbTestDatasets Create(
        string? ratingRows = null,
        string? episodeRows = null,
        bool episodesOffline = false)
    {
        var directory = Path.Combine(Path.GetTempPath(), "extratings-ds-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);

        var ratingPayload = Gzip("tconst\taverageRating\tnumVotes\n" + (ratingRows ?? DefaultRatingRows));
        var episodePayload = Gzip(
            "tconst\tparentTconst\tseasonNumber\tepisodeNumber\n" + (episodeRows ?? DefaultEpisodeRows));

        var handler = new FakeHttpMessageHandler((request, _) =>
        {
            var wantsEpisodes = string.Equals(
                request.RequestUri!.ToString(), ImdbEpisodeDataset.DatasetUrl, StringComparison.Ordinal);

            if (wantsEpisodes && episodesOffline)
            {
                throw new HttpRequestException("offline");
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(wantsEpisodes ? episodePayload : ratingPayload)
            });
        });

        var client = new HttpClient(handler);
        var clock = new FakeClock();

        return new ImdbTestDatasets(
            directory,
            handler,
            new ImdbRatingsDataset(client, directory, () => TimeSpan.FromHours(24), clock, NullLogger.Instance),
            new ImdbEpisodeDataset(client, directory, () => TimeSpan.FromHours(24), clock, NullLogger.Instance));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Ratings.Dispose();
        Episodes.Dispose();

        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
            // A leaked temp dir must not fail a test run.
        }
    }

    private static byte[] Gzip(string tsv)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            var bytes = Encoding.UTF8.GetBytes(tsv);
            gzip.Write(bytes, 0, bytes.Length);
        }

        return output.ToArray();
    }
}
