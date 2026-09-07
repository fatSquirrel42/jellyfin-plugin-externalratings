using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Jellyfin.Plugin.ExternalRatings.Resolvers.Imdb;
using Jellyfin.Plugin.ExternalRatings.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ExternalRatings.Tests;

public sealed class ImdbRatingsDatasetTests : IDisposable
{
    private readonly string _dir;

    public ImdbRatingsDatasetTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "extratings-imdb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
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

    private static string Tsv(params (string Id, string Rating)[] rows)
    {
        var sb = new StringBuilder("tconst\taverageRating\tnumVotes\n");
        foreach (var (id, rating) in rows)
        {
            sb.Append(id).Append('\t').Append(rating).Append("\t10\n");
        }

        return sb.ToString();
    }

    private static FakeHttpMessageHandler Serves(byte[] payload, Action? onRequest = null)
        => new((_, _) =>
        {
            onRequest?.Invoke();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            });
        });

    private ImdbRatingsDataset Create(HttpMessageHandler handler, FakeClock clock, TimeSpan? refresh = null)
        => new(new HttpClient(handler), _dir, () => refresh ?? TimeSpan.FromHours(24), clock, NullLogger.Instance);

    [Fact]
    public async Task DownloadsAndIndexes_WhenNothingIsCached()
    {
        var handler = Serves(Gzip(Tsv(("tt0903747", "9.5"), ("tt2301451", "9.5"))));
        var clock = new FakeClock();
        using var dataset = Create(handler, clock);

        var index = await dataset.GetIndexAsync(CancellationToken.None);

        index.Count.Should().Be(2);
        index.TryGetRating("tt0903747", out var rating).Should().BeTrue();
        rating.Should().Be(9.5f);
        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Uri.ToString().Should().Be(ImdbRatingsDataset.DatasetUrl);
    }

    [Fact]
    public async Task SecondCallWithinTheInterval_DoesNotDownloadAgain()
    {
        var handler = Serves(Gzip(Tsv(("tt0000100", "6.0"))));
        var clock = new FakeClock();
        using var dataset = Create(handler, clock, TimeSpan.FromHours(24));

        await dataset.GetIndexAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromHours(23));
        var index = await dataset.GetIndexAsync(CancellationToken.None);

        index.Count.Should().Be(1);
        handler.Requests.Should().ContainSingle("the cached copy is still fresh");
    }

    [Fact]
    public async Task PastTheInterval_DownloadsAgainAndPicksUpNewRows()
    {
        var first = Gzip(Tsv(("tt0000100", "6.0")));
        var second = Gzip(Tsv(("tt0000100", "6.0"), ("tt0000200", "7.0")));
        var payload = first;
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) }));

        var clock = new FakeClock();
        using var dataset = Create(handler, clock, TimeSpan.FromHours(24));

        (await dataset.GetIndexAsync(CancellationToken.None)).Count.Should().Be(1);

        payload = second;
        clock.Advance(TimeSpan.FromHours(25));
        var index = await dataset.GetIndexAsync(CancellationToken.None);

        index.Count.Should().Be(2);
        index.TryGetRating("tt0000200", out _).Should().BeTrue();
        handler.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task FailedRefresh_KeepsServingTheCachedCopy()
    {
        var payload = Gzip(Tsv(("tt0000100", "6.0")));
        var fail = false;
        var handler = new FakeHttpMessageHandler((_, _) => fail
            ? throw new HttpRequestException("network down")
            : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) }));

        var clock = new FakeClock();
        using var dataset = Create(handler, clock, TimeSpan.FromHours(24));

        (await dataset.GetIndexAsync(CancellationToken.None)).Count.Should().Be(1);

        fail = true;
        clock.Advance(TimeSpan.FromHours(25));
        var index = await dataset.GetIndexAsync(CancellationToken.None);

        index.Count.Should().Be(1, "a transient failure must not drop a working dataset");
        index.TryGetRating("tt0000100", out _).Should().BeTrue();
    }

    [Fact]
    public async Task FailedFirstDownload_ReturnsEmptyInsteadOfThrowing()
    {
        // Throwing here would abort the whole scheduled enrichment pass.
        var handler = FakeHttpMessageHandler.Throws(new HttpRequestException("offline"));
        var clock = new FakeClock();
        using var dataset = Create(handler, clock);

        var index = await dataset.GetIndexAsync(CancellationToken.None);

        index.Count.Should().Be(0);
    }

    [Fact]
    public async Task HttpErrorStatus_IsTreatedAsAFailedRefresh()
    {
        var handler = new FakeHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        var clock = new FakeClock();
        using var dataset = Create(handler, clock);

        var index = await dataset.GetIndexAsync(CancellationToken.None);

        index.Count.Should().Be(0);
    }

    [Fact]
    public async Task CorruptCachedFile_IsDiscardedAndRedownloadedNextTime()
    {
        var good = Gzip(Tsv(("tt0000100", "6.0")));
        var serveGood = false;
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(serveGood ? good : new byte[] { 1, 2, 3, 4, 5 })
            }));

        var clock = new FakeClock();
        using var dataset = Create(handler, clock);

        (await dataset.GetIndexAsync(CancellationToken.None)).Count.Should().Be(0);

        serveGood = true;
        var index = await dataset.GetIndexAsync(CancellationToken.None);

        index.Count.Should().Be(1, "the corrupt file is deleted, so the next call downloads again");
    }

    [Fact]
    public async Task ParsesFromDiskWithoutDownloading_WhenAFreshCopyAlreadyExists()
    {
        await File.WriteAllBytesAsync(
            Path.Combine(_dir, "imdb-title.ratings.tsv.gz"),
            Gzip(Tsv(("tt0000100", "6.0"))));

        var handler = FakeHttpMessageHandler.Throws(new HttpRequestException("must not be called"));
        var clock = new FakeClock();
        using var dataset = Create(handler, clock, TimeSpan.FromHours(24));

        var index = await dataset.GetIndexAsync(CancellationToken.None);

        index.Count.Should().Be(1);
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ReportsCountAndTimestamp()
    {
        var handler = Serves(Gzip(Tsv(("tt0000100", "6.0"))));
        var clock = new FakeClock();
        using var dataset = Create(handler, clock);

        dataset.Count.Should().Be(0);
        dataset.LoadedFromUtc.Should().BeNull();

        await dataset.GetIndexAsync(CancellationToken.None);

        dataset.Count.Should().Be(1);
        dataset.LoadedFromUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task ConcurrentCallers_DownloadOnlyOnce()
    {
        var downloads = 0;
        var handler = Serves(Gzip(Tsv(("tt0000100", "6.0"))), () => Interlocked.Increment(ref downloads));
        var clock = new FakeClock();
        using var dataset = Create(handler, clock);

        var results = await Task.WhenAll(
            Task.Run(() => dataset.GetIndexAsync(CancellationToken.None)),
            Task.Run(() => dataset.GetIndexAsync(CancellationToken.None)),
            Task.Run(() => dataset.GetIndexAsync(CancellationToken.None)));

        downloads.Should().Be(1);
        results.Should().OnlyContain(r => r.Count == 1);
    }
}
