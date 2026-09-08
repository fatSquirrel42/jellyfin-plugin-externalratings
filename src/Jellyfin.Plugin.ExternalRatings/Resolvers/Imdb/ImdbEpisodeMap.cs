using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Jellyfin.Plugin.ExternalRatings.Resolvers.Imdb;

/// <summary>
/// An in-memory view of IMDb's <c>title.episode</c> dataset: which season an episode belongs to,
/// and which episodes make up a season.
/// </summary>
/// <remarks>
/// <para>
/// This is what lets a season score cover *IMDb's* season rather than only the episodes the library
/// happens to hold. The season is identified from the episodes themselves — their ids map to a
/// (series, season) pair — never by translating a season number, which diverges between TheTVDB and
/// IMDb (see <c>docs/level-support-diagnosis.md</c> §3).
/// </para>
/// <para>
/// Loading is filtered to a set of parent series. The full file is ~9.87 M rows and would cost
/// roughly 178 MB resident; restricted to one library's series it is a few tens of thousands of
/// rows, so a dictionary is both clearer and cheap. The streaming parse still reads every row —
/// the filter governs what is kept, not what is read.
/// </para>
/// <para>
/// Rows whose season or episode number is <c>\N</c> are dropped: 20.95 % of the real file is
/// specials and unnumbered entries, and keeping them would invent a phantom season.
/// </para>
/// </remarks>
internal sealed class ImdbEpisodeMap
{
    private readonly Dictionary<(long Parent, int Season), long[]> _seasons;
    private readonly Dictionary<long, (long Parent, int Season)> _episodes;
    private readonly HashSet<long> _parents;

    private ImdbEpisodeMap(
        Dictionary<(long Parent, int Season), long[]> seasons,
        Dictionary<long, (long Parent, int Season)> episodes,
        HashSet<long> parents)
    {
        _seasons = seasons;
        _episodes = episodes;
        _parents = parents;
    }

    /// <summary>Gets a map containing nothing, used before the first successful load.</summary>
    public static ImdbEpisodeMap Empty { get; } = new(new(), new(), new());

    /// <summary>Gets the number of episodes held.</summary>
    public int EpisodeCount => _episodes.Count;

    /// <summary>Gets the number of seasons held.</summary>
    public int SeasonCount => _seasons.Count;

    /// <summary>
    /// Parses a decompressed <c>title.episode.tsv</c> stream, keeping only the rows belonging to
    /// <paramref name="parents"/>.
    /// </summary>
    /// <param name="tsv">The decompressed TSV stream.</param>
    /// <param name="parents">The numeric ids of the series to keep.</param>
    /// <returns>The map.</returns>
    public static ImdbEpisodeMap Load(Stream tsv, IReadOnlySet<long> parents)
    {
        var pending = new Dictionary<(long Parent, int Season), List<(int Episode, long Child)>>();
        var episodes = new Dictionary<long, (long Parent, int Season)>();

        using var reader = new StreamReader(tsv);
        _ = reader.ReadLine(); // header

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (!TryParseLine(line, parents, out var child, out var parent, out var season, out var episode))
            {
                continue;
            }

            if (!episodes.TryAdd(child, (parent, season)))
            {
                // A duplicate row; the first wins, matching the ratings index's dedupe.
                continue;
            }

            var key = (parent, season);
            if (!pending.TryGetValue(key, out var list))
            {
                list = new List<(int, long)>();
                pending[key] = list;
            }

            list.Add((episode, child));
        }

        var seasons = new Dictionary<(long Parent, int Season), long[]>(pending.Count);
        foreach (var pair in pending)
        {
            var ordered = pair.Value;
            ordered.Sort(static (a, b) => a.Episode.CompareTo(b.Episode));

            var children = new long[ordered.Count];
            for (var i = 0; i < ordered.Count; i++)
            {
                children[i] = ordered[i].Child;
            }

            seasons[pair.Key] = children;
        }

        return new ImdbEpisodeMap(seasons, episodes, new HashSet<long>(parents));
    }

    /// <summary>Whether this map was built with a series included.</summary>
    /// <param name="parent">The numeric series id.</param>
    /// <returns><see langword="true"/> when the series was in the load filter.</returns>
    public bool Covers(long parent) => _parents.Contains(parent);

    /// <summary>Whether this map covers every one of the given series.</summary>
    /// <param name="parents">The numeric series ids.</param>
    /// <returns><see langword="true"/> when all of them were in the load filter.</returns>
    public bool CoversAll(IReadOnlySet<long> parents)
    {
        foreach (var parent in parents)
        {
            if (!_parents.Contains(parent))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Finds the season an episode belongs to.</summary>
    /// <param name="imdbId">The episode's IMDb id.</param>
    /// <param name="season">The owning series and season number.</param>
    /// <returns><see langword="true"/> when the episode is known.</returns>
    public bool TryGetSeasonOf(string? imdbId, out (long Parent, int Season) season)
    {
        season = default;

        var id = ImdbRatingsIndex.ParseTconst(imdbId);
        if (id is null)
        {
            return false;
        }

        return _episodes.TryGetValue(id.Value, out season);
    }

    /// <summary>Returns every episode IMDb lists for a season, ordered by episode number.</summary>
    /// <param name="parent">The numeric series id.</param>
    /// <param name="season">The season number.</param>
    /// <returns>The episodes' numeric ids, empty when the season is unknown.</returns>
    public IReadOnlyList<long> GetEpisodesOf(long parent, int season)
        => _seasons.TryGetValue((parent, season), out var children) ? children : Array.Empty<long>();

    private static bool TryParseLine(
        string line,
        IReadOnlySet<long> parents,
        out long child,
        out long parent,
        out int season,
        out int episode)
    {
        child = 0;
        parent = 0;
        season = 0;
        episode = 0;

        var t1 = line.IndexOf('\t', StringComparison.Ordinal);
        if (t1 <= 0)
        {
            return false;
        }

        var t2 = line.IndexOf('\t', t1 + 1);
        if (t2 <= t1)
        {
            return false;
        }

        var t3 = line.IndexOf('\t', t2 + 1);
        if (t3 <= t2)
        {
            return false;
        }

        // Check the parent first: it rejects ~99 % of rows for a typical library, so the remaining
        // parses only run on rows that will be kept.
        var parsedParent = ImdbRatingsIndex.ParseTconst(line[(t1 + 1)..t2]);
        if (parsedParent is null || !parents.Contains(parsedParent.Value))
        {
            return false;
        }

        var parsedChild = ImdbRatingsIndex.ParseTconst(line[..t1]);
        if (parsedChild is null)
        {
            return false;
        }

        // "\N" for either number means an unnumbered entry — a special, or an episode IMDb has not
        // placed yet. It has no (season, episode) coordinate and must not become one.
        if (!int.TryParse(line.AsSpan(t2 + 1, t3 - t2 - 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedSeason)
            || !int.TryParse(line.AsSpan(t3 + 1).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedEpisode))
        {
            return false;
        }

        child = parsedChild.Value;
        parent = parsedParent.Value;
        season = parsedSeason;
        episode = parsedEpisode;
        return true;
    }
}
