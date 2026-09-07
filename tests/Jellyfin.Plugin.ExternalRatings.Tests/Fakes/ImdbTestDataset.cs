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
/// Builds an <see cref="ImdbRatingsDataset"/> backed by a canned gzipped TSV in a throwaway
/// directory, for tests that need a working dataset but do not care how it got there.
/// </summary>
internal static class ImdbTestDataset
{
    /// <summary>Default rows: Breaking Bad, its episode "Ozymandias", and a Diabolical episode.</summary>
    public const string DefaultRows =
        "tt0903747\t9.5\t2671907\n" +
        "tt2301451\t9.5\t507558\n" +
        "tt16364366\t6.8\t3217\n";

    public static ImdbRatingsDataset Create(out string directory, string? rows = null)
    {
        directory = Path.Combine(Path.GetTempPath(), "extratings-ds-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        var payload = Gzip("tconst\taverageRating\tnumVotes\n" + (rows ?? DefaultRows));
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) }));

        return new ImdbRatingsDataset(
            new HttpClient(handler),
            directory,
            () => TimeSpan.FromHours(24),
            new FakeClock(),
            NullLogger.Instance);
    }

    public static void Cleanup(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
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
