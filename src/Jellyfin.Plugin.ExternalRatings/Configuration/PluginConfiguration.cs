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
/// property (it uses the getter and calls Add), double-counting any ctor defaults such as
/// <c>ProcessedLevels</c> — a defect the ConfigRoundtripTests catch. Arrays still meet §10.1's real
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

    /// <summary>
    /// Gets or sets the processed item levels as strings (mapped to the internal
    /// <c>ItemLevel</c> enum). Defaults to Movie and Series.
    /// </summary>
    public string[] ProcessedLevels { get; set; } = new[] { "Movie", "Series" };
#pragma warning restore CA1819

    /// <summary>
    /// Gets or sets the key of the active resolver.
    /// </summary>
    public string ActiveResolverKey { get; set; } = "mdblist";

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
    public string NoMatchBehavior { get; set; } = "LeaveExisting";

    /// <summary>
    /// Gets or sets the behavior for unsupported levels (<c>Skip</c> or <c>ClearField</c>).
    /// </summary>
    public string UnsupportedLevelBehavior { get; set; } = "Skip";

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
