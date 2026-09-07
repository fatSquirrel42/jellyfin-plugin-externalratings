using System;
using System.Collections.Generic;
using Jellyfin.Plugin.ExternalRatings.Configuration;

namespace Jellyfin.Plugin.ExternalRatings.Core;

/// <summary>
/// Maps the public, string-valued <see cref="PluginConfiguration"/> onto the internal domain types
/// (spec §10.1). Keeping this translation in one tested place lets the domain enums stay internal
/// while the config surface remains XML-serializable, and centralizes the invalid-value fallbacks.
/// </summary>
internal static class PluginConfigurationMapper
{
    /// <summary>
    /// The sentinel source value meaning "do not set or replace the community rating". When resolved as
    /// the effective source, the item is skipped entirely (no fetch, no write).
    /// </summary>
    public const string NoSource = "none";

    /// <summary>The canonical level order, broad to narrow.</summary>
    private static readonly ItemLevel[] CanonicalLevels =
    {
        ItemLevel.Movie, ItemLevel.Series, ItemLevel.Season, ItemLevel.Episode
    };

    /// <summary>Builds <see cref="PipelineOptions"/> from the configuration.</summary>
    /// <param name="config">The plugin configuration.</param>
    /// <returns>The pipeline options; unknown enum strings fall back to their spec defaults.</returns>
    public static PipelineOptions ToPipelineOptions(PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);

        return new PipelineOptions
        {
            DryRun = config.DryRun,
            NoMatchBehavior = ParseEnum(config.NoMatchBehavior, NoMatchBehavior.LeaveExisting),
            UnsupportedLevelBehavior = ParseEnum(config.UnsupportedLevelBehavior, UnsupportedLevelBehavior.LeaveExisting),
            CacheTtl = TimeSpan.FromDays(config.CacheTtlDays),
            NegativeCacheTtl = TimeSpan.FromDays(config.NegativeCacheTtlDays)
        };
    }

    /// <summary>
    /// Parses configured level names into <see cref="ItemLevel"/> values, in canonical order.
    /// Unknown or blank names are ignored rather than failing the run, and duplicates collapse.
    /// </summary>
    /// <param name="levelNames">The configured level names.</param>
    /// <returns>The parsed levels, deduplicated and ordered Movie, Series, Season, Episode.</returns>
    public static IReadOnlyList<ItemLevel> ParseLevels(IEnumerable<string>? levelNames)
    {
        if (levelNames is null)
        {
            return Array.Empty<ItemLevel>();
        }

        var wanted = new HashSet<ItemLevel>();
        foreach (var name in levelNames)
        {
            if (!string.IsNullOrWhiteSpace(name) && Enum.TryParse<ItemLevel>(name.Trim(), ignoreCase: true, out var level))
            {
                wanted.Add(level);
            }
        }

        // Canonical order, so the enumeration order never depends on how the config was written.
        var ordered = new List<ItemLevel>(wanted.Count);
        foreach (var level in CanonicalLevels)
        {
            if (wanted.Contains(level))
            {
                ordered.Add(level);
            }
        }

        return ordered;
    }

    /// <summary>Whether the resolved source means enrichment should be skipped.</summary>
    /// <param name="source">A source returned by <see cref="ResolveSource"/>.</param>
    /// <returns><see langword="true"/> for the <see cref="NoSource"/> sentinel or a blank value.</returns>
    public static bool IsNoSource(string source)
        => string.IsNullOrWhiteSpace(source) || string.Equals(source, NoSource, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves the external rating source for an item, given the ids of the libraries
    /// (CollectionFolders) it belongs to.
    /// </summary>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="collectionFolderIds">The item's CollectionFolder ids (see <c>ILibraryManager.GetCollectionFolders</c>).</param>
    /// <returns>
    /// The source of the first matching <see cref="PluginConfiguration.LibrarySources"/> override; else
    /// <see cref="PluginConfiguration.RatingSource"/>; falling back to <see cref="NoSource"/> (leave
    /// ratings untouched) when unset.
    /// </returns>
    public static string ResolveSource(PluginConfiguration config, IEnumerable<Guid> collectionFolderIds)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(collectionFolderIds);

        var fallback = string.IsNullOrWhiteSpace(config.RatingSource) ? NoSource : config.RatingSource.Trim();

        // Fast path: no overrides configured, so every library uses the default.
        if (config.LibrarySources.Length == 0)
        {
            return fallback;
        }

        foreach (var folderId in collectionFolderIds)
        {
            foreach (var mapping in config.LibrarySources)
            {
                if (mapping.LibraryId == folderId && !string.IsNullOrWhiteSpace(mapping.Source))
                {
                    return mapping.Source.Trim();
                }
            }
        }

        return fallback;
    }

    /// <summary>Looks up a resolver setting by key.</summary>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="key">The setting key (for example <c>mdblist.apiKey</c>).</param>
    /// <returns>The value, or <see langword="null"/> if the key is not present.</returns>
    public static string? GetResolverSetting(PluginConfiguration config, string key)
    {
        ArgumentNullException.ThrowIfNull(config);

        foreach (var setting in config.ResolverSettings)
        {
            if (string.Equals(setting.Key, key, StringComparison.Ordinal))
            {
                return setting.Value;
            }
        }

        return null;
    }

    private static TEnum ParseEnum<TEnum>(string value, TEnum fallback)
        where TEnum : struct, Enum
        => Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed) ? parsed : fallback;
}
