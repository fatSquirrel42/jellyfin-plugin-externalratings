using System;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ExternalRatings.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ExternalRatings.Resolvers.Imdb;

/// <summary>
/// Owns the on-disk copy of IMDb's <c>title.ratings.tsv.gz</c> and the
/// <see cref="ImdbRatingsIndex"/> parsed from it, refreshing both on a configurable interval.
/// </summary>
/// <remarks>
/// <para>
/// The dataset is ~8.6 MB compressed and IMDb regenerates it daily, so the refresh interval only
/// needs to be in the same order of magnitude. <c>title.episode.tsv.gz</c> is deliberately not
/// fetched — see <c>docs/level-support-diagnosis.md</c> §9.
/// </para>
/// <para>
/// A failed refresh is never fatal while a previous copy exists: the stale file keeps serving and
/// the next call retries. That matters because this runs inside a scheduled task, where throwing
/// would abort the whole enrichment pass over a transient network blip.
/// </para>
/// <para>
/// IMDb licenses these files for personal and non-commercial use, so they are downloaded onto the
/// user's own server at runtime and never redistributed with the plugin.
/// </para>
/// </remarks>
internal sealed class ImdbRatingsDataset : IDisposable
{
    /// <summary>The official dataset location.</summary>
    public const string DatasetUrl = "https://datasets.imdbws.com/title.ratings.tsv.gz";

    private const string FileName = "imdb-title.ratings.tsv.gz";
    private const string StampFileName = "imdb-title.ratings.stamp";

    private readonly HttpClient _httpClient;
    private readonly string _filePath;
    private readonly string _stampPath;
    private readonly Func<TimeSpan> _refreshInterval;
    private readonly IClock _clock;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private ImdbRatingsIndex _index = ImdbRatingsIndex.Empty;
    private DateTimeOffset? _indexLoadedFrom;
    private bool _disposed;

    /// <summary>Initializes a new instance of the <see cref="ImdbRatingsDataset"/> class.</summary>
    /// <param name="httpClient">An HTTP client that must not be the mdblist one (this download is not on that quota).</param>
    /// <param name="directory">The directory to cache the dataset in.</param>
    /// <param name="refreshInterval">Accessor for how long a cached copy stays fresh.</param>
    /// <param name="clock">The clock.</param>
    /// <param name="logger">The logger.</param>
    public ImdbRatingsDataset(
        HttpClient httpClient,
        string directory,
        Func<TimeSpan> refreshInterval,
        IClock clock,
        ILogger logger)
    {
        _httpClient = httpClient;
        _filePath = Path.Combine(directory, FileName);
        _stampPath = Path.Combine(directory, StampFileName);
        _refreshInterval = refreshInterval;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Gets the number of rated titles currently indexed (0 before the first load).</summary>
    public int Count => _index.Count;

    /// <summary>Gets when the currently loaded file was downloaded, or <see langword="null"/>.</summary>
    public DateTimeOffset? LoadedFromUtc => _indexLoadedFrom;

    /// <summary>
    /// Returns the index, downloading and/or parsing first if the cached copy is missing or stale.
    /// Returns an empty index rather than throwing when there is nothing usable at all.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The index.</returns>
    public async Task<ImdbRatingsIndex> GetIndexAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsStale())
            {
                await TryRefreshAsync(cancellationToken).ConfigureAwait(false);
            }

            EnsureParsed();
            return _index;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gate.Dispose();
    }

    private bool IsStale()
    {
        var downloadedAt = DownloadedAtUtc();
        if (downloadedAt is null)
        {
            return true;
        }

        var interval = _refreshInterval();
        return interval > TimeSpan.Zero && _clock.UtcNow - downloadedAt.Value >= interval;
    }

    /// <summary>
    /// When the cached file was downloaded, as recorded by <see cref="IClock"/> in a sidecar stamp.
    /// </summary>
    /// <remarks>
    /// The filesystem's own mtime is deliberately not the source of truth. Reading it would route
    /// the freshness decision around the injected clock, which both breaks the test seam and makes
    /// the interval depend on wall-clock/timezone behaviour of the file system. The mtime is only a
    /// fallback for a file written before the stamp existed.
    /// </remarks>
    private DateTimeOffset? DownloadedAtUtc()
    {
        if (!File.Exists(_filePath))
        {
            return null;
        }

        try
        {
            if (File.Exists(_stampPath)
                && DateTimeOffset.TryParse(
                    File.ReadAllText(_stampPath),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var stamped))
            {
                return stamped;
            }
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Could not read the IMDb dataset stamp; falling back to the file timestamp");
        }

        return new DateTimeOffset(File.GetLastWriteTimeUtc(_filePath), TimeSpan.Zero);
    }

    private void WriteStamp()
    {
        try
        {
            File.WriteAllText(_stampPath, _clock.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        }
        catch (IOException ex)
        {
            // Losing the stamp only costs an extra refresh next time.
            _logger.LogDebug(ex, "Could not write the IMDb dataset stamp");
        }
    }

    private async Task TryRefreshAsync(CancellationToken cancellationToken)
    {
        var temp = _filePath + ".tmp";
        try
        {
            _logger.LogInformation("Downloading the IMDb ratings dataset from {Url}", DatasetUrl);

            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);

            using (var response = await _httpClient
                .GetAsync(DatasetUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();

                var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None);
                await using (file.ConfigureAwait(false))
                {
                    await response.Content.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
                }
            }

            File.Move(temp, _filePath, overwrite: true);
            WriteStamp();

            // Force a re-parse; the file on disk changed underneath the loaded index.
            _indexLoadedFrom = null;
            _index = ImdbRatingsIndex.Empty;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            if (File.Exists(_filePath))
            {
                _logger.LogWarning(ex, "IMDb dataset refresh failed; continuing with the cached copy from {Timestamp}", DownloadedAtUtc());
            }
            else
            {
                _logger.LogError(ex, "IMDb dataset download failed and no cached copy exists; ratings cannot be resolved yet");
            }
        }
        finally
        {
            TryDelete(temp);
        }
    }

    private void EnsureParsed()
    {
        var timestamp = DownloadedAtUtc();
        if (timestamp is null)
        {
            return;
        }

        if (_indexLoadedFrom == timestamp)
        {
            return;
        }

        try
        {
            var started = _clock.UtcNow;

            var file = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using (file)
            {
                using var gzip = new GZipStream(file, CompressionMode.Decompress);
                _index = ImdbRatingsIndex.Load(gzip);
            }

            _indexLoadedFrom = timestamp;
            _logger.LogInformation(
                "Indexed {Count} IMDb title ratings in {Elapsed}",
                _index.Count,
                (_clock.UtcNow - started).ToString("g", CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            // A truncated or corrupt download must not wedge the plugin: drop it so the next call
            // treats the dataset as missing and downloads again.
            _logger.LogWarning(ex, "The cached IMDb dataset could not be read; discarding it");
            _index = ImdbRatingsIndex.Empty;
            _indexLoadedFrom = null;
            TryDelete(_filePath);
            TryDelete(_stampPath);
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Could not delete {Path}", path);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogDebug(ex, "Could not delete {Path}", path);
        }
    }
}
