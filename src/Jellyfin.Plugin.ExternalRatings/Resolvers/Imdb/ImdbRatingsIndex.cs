using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Jellyfin.Plugin.ExternalRatings.Resolvers.Imdb;

/// <summary>
/// An in-memory lookup over IMDb's <c>title.ratings</c> dataset: numeric tconst to average rating.
/// </summary>
/// <remarks>
/// <para>
/// Pure and I/O-free apart from the stream it is handed, so the parse is unit-testable;
/// <see cref="ImdbRatingsDataset"/> owns downloading and refreshing the file.
/// </para>
/// <para>
/// A tconst is stored as the number behind the <c>tt</c> prefix in two parallel sorted arrays
/// rather than a dictionary of strings. At ~1.7 M rated titles that is roughly 20 MB resident with
/// no per-entry object overhead, and a lookup is a binary search. Votes are deliberately not kept:
/// nothing reads them, and IMDb's own season average is unweighted
/// (see <c>docs/level-support-diagnosis.md</c> §3).
/// </para>
/// </remarks>
internal sealed class ImdbRatingsIndex
{
    private const int InitialCapacity = 1 << 21;

    private readonly long[] _ids;
    private readonly float[] _ratings;

    private ImdbRatingsIndex(long[] ids, float[] ratings)
    {
        _ids = ids;
        _ratings = ratings;
    }

    /// <summary>Gets an index containing nothing, used before the first successful load.</summary>
    public static ImdbRatingsIndex Empty { get; } = new(Array.Empty<long>(), Array.Empty<float>());

    /// <summary>Gets the number of rated titles.</summary>
    public int Count => _ids.Length;

    /// <summary>
    /// Parses a decompressed <c>title.ratings.tsv</c> stream (<c>tconst, averageRating, numVotes</c>).
    /// Rows that are malformed, unrated, or not titles are skipped rather than failing the load.
    /// </summary>
    /// <param name="tsv">The decompressed TSV stream.</param>
    /// <returns>The index.</returns>
    public static ImdbRatingsIndex Load(Stream tsv)
    {
        var ids = new long[InitialCapacity];
        var ratings = new float[InitialCapacity];
        var count = 0;

        using var reader = new StreamReader(tsv);
        _ = reader.ReadLine(); // header

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (!TryParseLine(line, out var id, out var rating))
            {
                continue;
            }

            if (count == ids.Length)
            {
                Array.Resize(ref ids, ids.Length * 2);
                Array.Resize(ref ratings, ratings.Length * 2);
            }

            ids[count] = id;
            ratings[count] = rating;
            count++;
        }

        Array.Resize(ref ids, count);
        Array.Resize(ref ratings, count);

        // Sort ids ascending, carrying the ratings along, so lookups can binary-search.
        Array.Sort(ids, ratings);

        // Collapse duplicate ids in place, keeping the first of each run: BinarySearch has no
        // defined behaviour across duplicates, so the index must hold each id exactly once.
        var unique = 0;
        for (var read = 0; read < count; read++)
        {
            if (read > 0 && ids[read] == ids[read - 1])
            {
                continue;
            }

            ids[unique] = ids[read];
            ratings[unique] = ratings[read];
            unique++;
        }

        if (unique != count)
        {
            Array.Resize(ref ids, unique);
            Array.Resize(ref ratings, unique);
        }

        return new ImdbRatingsIndex(ids, ratings);
    }

    /// <summary>Looks up the average rating for an IMDb id.</summary>
    /// <param name="imdbId">The IMDb id (e.g. <c>tt0903747</c>); leading/trailing space and casing are tolerated.</param>
    /// <param name="rating">The average rating on IMDb's 0–10 scale, or <c>0</c> when not found.</param>
    /// <returns><see langword="true"/> when the id is present.</returns>
    public bool TryGetRating(string? imdbId, out float rating)
    {
        rating = 0f;

        var id = ParseTconst(imdbId);
        return id is not null && TryGetRating(id.Value, out rating);
    }

    /// <summary>Looks up the average rating for an already-parsed numeric tconst.</summary>
    /// <param name="id">The numeric id, as <see cref="ImdbEpisodeMap"/> hands them out.</param>
    /// <param name="rating">The average rating on IMDb's 0–10 scale, or <c>0</c> when not found.</param>
    /// <returns><see langword="true"/> when the id is present.</returns>
    public bool TryGetRating(long id, out float rating)
    {
        rating = 0f;

        var index = Array.BinarySearch(_ids, id);
        if (index < 0)
        {
            return false;
        }

        rating = _ratings[index];
        return true;
    }

    /// <summary>Converts <c>tt0903747</c> to <c>903747</c>, or <see langword="null"/> if it is not a tconst.</summary>
    /// <param name="imdbId">The candidate id.</param>
    /// <returns>The numeric id, or <see langword="null"/>.</returns>
    public static long? ParseTconst(string? imdbId)
    {
        if (string.IsNullOrWhiteSpace(imdbId))
        {
            return null;
        }

        var span = imdbId.AsSpan().Trim();
        if (span.Length <= 2 || !span.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return long.TryParse(span[2..], NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static bool TryParseLine(string line, out long id, out float rating)
    {
        id = 0;
        rating = 0f;

        var firstTab = line.IndexOf('\t', StringComparison.Ordinal);
        if (firstTab <= 0)
        {
            return false;
        }

        var parsed = ParseTconst(line[..firstTab]);
        if (parsed is null)
        {
            return false;
        }

        var secondTab = line.IndexOf('\t', firstTab + 1);
        var ratingSpan = secondTab > firstTab
            ? line.AsSpan(firstTab + 1, secondTab - firstTab - 1)
            : line.AsSpan(firstTab + 1);

        if (!float.TryParse(ratingSpan.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }

        // IMDb never publishes 0 or a negative average; such a row means "no rating".
        if (value <= 0f)
        {
            return false;
        }

        id = parsed.Value;
        rating = value;
        return true;
    }
}
