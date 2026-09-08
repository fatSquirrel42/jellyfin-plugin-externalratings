using System;
using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.ExternalRatings.Configuration;

/// <summary>
/// Plugin configuration (spec §10.1). Only XML-serializable types are used: Jellyfin persists this
/// through <see cref="System.Xml.Serialization.XmlSerializer"/>, which rejects
/// <see cref="Dictionary{TKey, TValue}"/> and <see cref="TimeSpan"/> (M6). Enum-valued settings are
/// stored as strings and mapped to the internal domain enums by the configuration mapper; durations
/// are stored as whole days.
/// </summary>
/// <remarks>
/// Collections are arrays rather than <see cref="List{T}"/>: <see cref="System.Xml.Serialization.XmlSerializer"/>
/// replaces an array on deserialize, but <em>appends</em> to a pre-initialized <see cref="List{T}"/>
/// property (it uses the getter and calls Add), double-counting any non-empty ctor defaults — a
/// defect the ConfigRoundtripTests catch. Arrays still meet §10.1's real
/// requirement (XML-serializable, non-<see cref="Dictionary{TKey, TValue}"/> collections; M6).
/// </remarks>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the ids of the libraries the plugin is enabled for.
    /// </summary>
    // CA1819: config DTOs persisted by XmlSerializer expose array properties by design (see remarks).
#pragma warning disable CA1819
    public Guid[] EnabledLibraries { get; set; } = Array.Empty<Guid>();

    /// <summary>
    /// Gets or sets the per-library rating-source overrides. Libraries without an entry use
    /// <see cref="RatingSource"/>. Keyed by CollectionFolder id (as in <see cref="EnabledLibraries"/>).
    /// </summary>
    public LibrarySourceSetting[] LibrarySources { get; set; } = Array.Empty<LibrarySourceSetting>();

    /// <summary>
    /// Gets or sets the resolver settings (for example the mdblist API key), keyed by string.
    /// </summary>
    public ResolverSetting[] ResolverSettings { get; set; } = Array.Empty<ResolverSetting>();
#pragma warning restore CA1819

#pragma warning disable CA1819 // Properties should not return arrays - XmlSerializer needs a settable array here.
    /// <summary>
    /// Gets or sets the item levels to process, by name (<c>Movie</c>, <c>Series</c>, <c>Season</c>,
    /// <c>Episode</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Season and Episode are off by default. This is a volume control, not a safety one: a TV
    /// library has one to two orders of magnitude more episodes than series, and an existing
    /// install must not start writing thousands of items because it was upgraded. An empty array
    /// means "process nothing" and is deliberately distinct from the default.
    /// </para>
    /// <para>
    /// The choice is global while rating sources are per-library, so a level enabled here is
    /// enumerated everywhere. An item whose source has no score at its level is then *cleared*
    /// under the shipped <see cref="UnsupportedLevelBehavior"/> default, not skipped — the field
    /// shows the configured source or nothing.
    /// </para>
    /// </remarks>
    public string[] EnabledLevels { get; set; } = { "Movie", "Series" };
#pragma warning restore CA1819

    /// <summary>
    /// Gets or sets how often the local copy of IMDb's <c>title.ratings</c> dataset is
    /// re-downloaded: <c>Daily</c>, <c>Weekly</c>, <c>Monthly</c> or <c>Never</c>. IMDb
    /// regenerates the file once a day, so nothing below daily would help. <c>Never</c> keeps the
    /// copy that is there and still fetches one when none exists.
    /// </summary>
    public string ImdbDatasetRefresh { get; set; } = "Daily";

    /// <summary>
    /// Gets or sets the default external rating source used for libraries without a
    /// <see cref="LibrarySources"/> override (for example <c>myanimelist</c> or <c>imdb</c>). The
    /// default <c>none</c> leaves community ratings untouched unless a library selects its own source.
    /// </summary>
    public string RatingSource { get; set; } = "none";

    /// <summary>
    /// Gets or sets the positive-cache time-to-live in days.
    /// </summary>
    public int CacheTtlDays { get; set; } = 7;

    /// <summary>
    /// Gets or sets the negative-cache (no-match) time-to-live in days.
    /// </summary>
    public int NegativeCacheTtlDays { get; set; } = 1;

    /// <summary>
    /// Gets or sets the behavior for authoritative no-matches
    /// (<c>LeaveExisting</c> or <c>ClearField</c>).
    /// </summary>
    public string NoMatchBehavior { get; set; } = "ClearField";

    /// <summary>
    /// Gets or sets the behavior for unsupported levels (<c>LeaveExisting</c> or <c>ClearField</c>).
    /// </summary>
    public string UnsupportedLevelBehavior { get; set; } = "ClearField";

    /// <summary>
    /// Gets or sets the daily HTTP request limit.
    /// </summary>
    public int DailyRequestLimit { get; set; } = 1000;

    /// <summary>
    /// Gets or sets a value indicating whether the plugin runs after each library scan.
    /// </summary>
    public bool RunAfterLibraryScan { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the realtime item listener is enabled.
    /// </summary>
    public bool EnableRealtimeListener { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether writes are suppressed (dry run; default on per §4).
    /// </summary>
    public bool DryRun { get; set; } = true;
}
