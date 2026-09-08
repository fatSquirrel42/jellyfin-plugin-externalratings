using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ExternalRatings.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ExternalRatings.Resolvers.Imdb;

/// <summary>
/// Owns the on-disk copy of IMDb's <c>title.episode.tsv.gz</c> and the <see cref="ImdbEpisodeMap"/>
/// parsed from it, filtered to the series that are actually being processed.
/// </summary>
/// <remarks>
/// <para>
/// This is the file that lets a season score cover IMDb's season rather than only the episodes on
/// disk. It is ~54.6 MB compressed against the ratings file's 8.6 — the reason the refresh interval
/// is worth turning down on a metered connection.
/// </para>
/// <para>
/// The map is filtered to a set of series, so it is rebuilt when asked about a series it was not
/// built for. That happens on the first pass and then rarely: when a new series appears, or when a
/// realtime season event arrives for one the last pass did not see.
/// </para>
/// </remarks>
internal sealed class ImdbEpisodeDataset : IDisposable
{
    /// <summary>The official dataset location.</summary>
    public const string DatasetUrl = "https://datasets.imdbws.com/title.episode.tsv.gz";

    private const string FileName = "imdb-title.episode.tsv.gz";

    private readonly ImdbDatasetFile<ImdbEpisodeMap> _file;
    private readonly object _wantedGate = new();

    private HashSet<long> _wanted = new();
    private bool _disposed;

    /// <summary>Initializes a new instance of the <see cref="ImdbEpisodeDataset"/> class.</summary>
    /// <param name="httpClient">An HTTP client that must not be the mdblist one (this is not on that quota).</param>
    /// <param name="directory">The directory to cache the dataset in.</param>
    /// <param name="refreshInterval">Accessor for how long a cached copy stays fresh.</param>
    /// <param name="clock">The clock.</param>
    /// <param name="logger">The logger.</param>
    public ImdbEpisodeDataset(
        HttpClient httpClient,
        string directory,
        Func<TimeSpan> refreshInterval,
        IClock clock,
        ILogger logger)
    {
        _file = new ImdbDatasetFile<ImdbEpisodeMap>(
            httpClient, DatasetUrl, "title.episode", directory, FileName, refreshInterval, clock, logger);
    }

    /// <summary>
    /// Widens the map to cover these series, rebuilding it if it was filtered more narrowly.
    /// </summary>
    /// <remarks>
    /// Called by the host, which is the only side that knows an episode's series. The resolver
    /// cannot work it out: it holds episode ids, and finding their series is what the map is for.
    /// </remarks>
    /// <param name="parents">The numeric ids of the series that must be covered.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes once the map covers them.</returns>
    public async Task EnsureCoversAsync(IReadOnlySet<long> parents, CancellationToken cancellationToken)
    {
        // Accumulate rather than replace: a realtime event asking about one series must not shrink
        // the map the last full pass built.
        lock (_wantedGate)
        {
            foreach (var parent in parents)
            {
                _wanted.Add(parent);
            }
        }

        await GetMapAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the map, built for whatever series have been asked for so far. Returns an empty map
    /// rather than throwing when the file is unavailable.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The map.</returns>
    public async Task<ImdbEpisodeMap> GetMapAsync(CancellationToken cancellationToken)
    {
        HashSet<long> wanted;
        lock (_wantedGate)
        {
            wanted = new HashSet<long>(_wanted);
        }

        return await _file.GetAsync(
            stream => ImdbEpisodeMap.Load(stream, wanted),
            static map => string.Create(CultureInfo.InvariantCulture, $"{map.EpisodeCount} episodes in {map.SeasonCount} seasons"),
            map => !map.CoversAll(wanted),
            cancellationToken).ConfigureAwait(false)
            ?? ImdbEpisodeMap.Empty;
    }

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
