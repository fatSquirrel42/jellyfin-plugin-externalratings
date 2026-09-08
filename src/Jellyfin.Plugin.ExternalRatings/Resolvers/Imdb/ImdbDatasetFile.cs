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
/// One gzipped file from IMDb's non-commercial dataset, kept on disk and re-downloaded on an
/// interval, with whatever the caller parses out of it held alongside.
/// </summary>
/// <typeparam name="TParsed">The in-memory form the file is parsed into.</typeparam>
/// <remarks>
/// <para>
/// Shared by the two files the plugin uses (<c>title.ratings</c> and <c>title.episode</c>) because
/// the awkward parts are identical and none of them are obvious: freshness comes from a sidecar
/// stamp rather than the file's mtime, a missing file is stale *before* the interval is consulted,
/// a failed refresh keeps serving the previous copy, and a corrupt archive is deleted so the next
/// call re-downloads instead of failing forever.
/// </para>
/// <para>
/// Each file gets its own instance and therefore its own gate. Sharing one gate across both would
/// serialise the 54 MB episode download behind every ratings lookup.
/// </para>
/// <para>
/// IMDb licenses these files for personal and non-commercial use, so they are downloaded onto the
/// user's own server at runtime and never redistributed with the plugin.
/// </para>
/// </remarks>
internal sealed class ImdbDatasetFile<TParsed> : IDisposable
    where TParsed : class
{
    private readonly HttpClient _httpClient;
    private readonly string _url;
    private readonly string _description;
    private readonly string _filePath;
    private readonly string _stampPath;
    private readonly Func<TimeSpan> _refreshInterval;
    private readonly IClock _clock;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private TParsed? _parsed;
    private DateTimeOffset? _parsedFrom;
    private bool _disposed;

    /// <summary>Initializes a new instance of the <see cref="ImdbDatasetFile{TParsed}"/> class.</summary>
    /// <param name="httpClient">An HTTP client that must not be the mdblist one (this is not on that quota).</param>
    /// <param name="url">The dataset URL.</param>
    /// <param name="description">A short name for log messages, for example <c>title.ratings</c>.</param>
    /// <param name="directory">The directory to cache the file in.</param>
    /// <param name="fileName">The cached file's name.</param>
    /// <param name="refreshInterval">
    /// Accessor for how long a cached copy stays fresh. <see cref="TimeSpan.Zero"/> means never
    /// re-download — a missing copy is still fetched.
    /// </param>
    /// <param name="clock">The clock.</param>
    /// <param name="logger">The logger.</param>
    public ImdbDatasetFile(
        HttpClient httpClient,
        string url,
        string description,
        string directory,
        string fileName,
        Func<TimeSpan> refreshInterval,
        IClock clock,
        ILogger logger)
    {
        _httpClient = httpClient;
        _url = url;
        _description = description;
        _filePath = Path.Combine(directory, fileName);
        _stampPath = _filePath + ".stamp";
        _refreshInterval = refreshInterval;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Gets when the currently parsed copy was downloaded, or <see langword="null"/>.</summary>
    public DateTimeOffset? LoadedFromUtc => _parsedFrom;

    /// <summary>
    /// Returns the parsed content, downloading and/or parsing first if the cached copy is missing or
    /// stale. Returns <see langword="null"/> rather than throwing when there is nothing usable.
    /// </summary>
    /// <param name="parse">Parses a decompressed stream. Called only when a re-parse is needed.</param>
    /// <param name="describeParsed">Renders the parsed content for the "indexed" log line.</param>
    /// <param name="forceReparse">
    /// Called with the currently parsed content to decide whether it must be rebuilt even though the
    /// file has not changed — the episode map uses it when the set of series it was filtered to no
    /// longer covers what is being asked for.
    /// </param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The parsed content, or <see langword="null"/>.</returns>
    public async Task<TParsed?> GetAsync(
        Func<Stream, TParsed> parse,
        Func<TParsed, string> describeParsed,
        Func<TParsed, bool>? forceReparse,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsStale())
            {
                await TryRefreshAsync(cancellationToken).ConfigureAwait(false);
            }

            if (forceReparse is not null && _parsed is not null && forceReparse(_parsed))
            {
                _parsedFrom = null;
            }

            EnsureParsed(parse, describeParsed);
            return _parsed;
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
        // A missing copy is always stale, checked *before* the interval: an interval of zero (the
        // configuration's "Never") means "never re-download", not "never download" — with no file
        // at all there would be nothing to answer with.
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
            _logger.LogDebug(ex, "Could not read the stamp for {Dataset}; falling back to the file timestamp", _description);
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
            _logger.LogDebug(ex, "Could not write the stamp for {Dataset}", _description);
        }
    }

    private async Task TryRefreshAsync(CancellationToken cancellationToken)
    {
        var temp = _filePath + ".tmp";
        try
        {
            _logger.LogInformation("Downloading the IMDb {Dataset} dataset from {Url}", _description, _url);

            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);

            using (var response = await _httpClient
                .GetAsync(_url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
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

            // Force a re-parse; the file on disk changed underneath what is loaded.
            _parsedFrom = null;
            _parsed = null;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            if (File.Exists(_filePath))
            {
                _logger.LogWarning(
                    ex,
                    "IMDb {Dataset} refresh failed; continuing with the cached copy from {Timestamp}",
                    _description,
                    DownloadedAtUtc());
            }
            else
            {
                _logger.LogError(
                    ex,
                    "IMDb {Dataset} download failed and no cached copy exists; it cannot be used yet",
                    _description);
            }
        }
        finally
        {
            TryDelete(temp);
        }
    }

    private void EnsureParsed(Func<Stream, TParsed> parse, Func<TParsed, string> describeParsed)
    {
        var timestamp = DownloadedAtUtc();
        if (timestamp is null)
        {
            return;
        }

        if (_parsedFrom == timestamp && _parsed is not null)
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
                _parsed = parse(gzip);
            }

            _parsedFrom = timestamp;

            // Guarded because describeParsed() walks the parsed structure to build its summary;
            // there is no reason to pay for that when Information is not being written.
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(
                    "Indexed IMDb {Dataset}: {Summary} in {Elapsed}",
                    _description,
                    describeParsed(_parsed),
                    (_clock.UtcNow - started).ToString("g", CultureInfo.InvariantCulture));
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            // A truncated or corrupt download must not wedge the plugin: drop it so the next call
            // treats the file as missing and downloads again.
            _logger.LogWarning(ex, "The cached IMDb {Dataset} could not be read; discarding it", _description);
            _parsed = null;
            _parsedFrom = null;
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
