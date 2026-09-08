using System;
using System.Globalization;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ExternalRatings.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ExternalRatings.Resolvers.Imdb;

/// <summary>
/// Owns the on-disk copy of IMDb's <c>title.ratings.tsv.gz</c> and the
/// <see cref="ImdbRatingsIndex"/> parsed from it.
/// </summary>
/// <remarks>
/// The file is ~8.6 MB compressed and IMDb regenerates it daily. Downloading, stamping, staleness
/// and corrupt-archive recovery all live in <see cref="ImdbDatasetFile{TParsed}"/>, shared with the
/// episode map; this class only says which file and how to parse it.
/// </remarks>
internal sealed class ImdbRatingsDataset : IDisposable
{
    /// <summary>The official dataset location.</summary>
    public const string DatasetUrl = "https://datasets.imdbws.com/title.ratings.tsv.gz";

    private const string FileName = "imdb-title.ratings.tsv.gz";

    private readonly ImdbDatasetFile<ImdbRatingsIndex> _file;
    private bool _disposed;

    /// <summary>Initializes a new instance of the <see cref="ImdbRatingsDataset"/> class.</summary>
    /// <param name="httpClient">An HTTP client that must not be the mdblist one (this is not on that quota).</param>
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
        _file = new ImdbDatasetFile<ImdbRatingsIndex>(
            httpClient, DatasetUrl, "title.ratings", directory, FileName, refreshInterval, clock, logger);
    }

    /// <summary>Gets when the currently loaded file was downloaded, or <see langword="null"/>.</summary>
    public DateTimeOffset? LoadedFromUtc => _file.LoadedFromUtc;

    /// <summary>
    /// Returns the index, downloading and/or parsing first if the cached copy is missing or stale.
    /// Returns an empty index rather than throwing when there is nothing usable at all.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The index.</returns>
    public async Task<ImdbRatingsIndex> GetIndexAsync(CancellationToken cancellationToken)
        => await _file.GetAsync(
            ImdbRatingsIndex.Load,
            static index => string.Create(CultureInfo.InvariantCulture, $"{index.Count} rated titles"),
            forceReparse: null,
            cancellationToken).ConfigureAwait(false)
            ?? ImdbRatingsIndex.Empty;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _file.Dispose();
    }
}
