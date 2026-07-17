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
    private static readonly IReadOnlyList<ItemLevel> DefaultLevels = new[] { ItemLevel.Movie, ItemLevel.Series };

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
            UnsupportedLevelBehavior = ParseEnum(config.UnsupportedLevelBehavior, UnsupportedLevelBehavior.Skip),
            CacheTtl = TimeSpan.FromDays(config.CacheTtlDays),
            NegativeCacheTtl = TimeSpan.FromDays(config.NegativeCacheTtlDays)
        };
    }

    /// <summary>Parses the configured processed levels into the internal enum.</summary>
    /// <param name="config">The plugin configuration.</param>
    /// <returns>
    /// The distinct, valid levels in configured order; falls back to Movie and Series when the
    /// configuration is empty or contains no recognized level.
    /// </returns>
    public static IReadOnlyList<ItemLevel> ParseLevels(PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var result = new List<ItemLevel>();
        foreach (var raw in config.ProcessedLevels)
        {
            if (Enum.TryParse<ItemLevel>(raw, ignoreCase: true, out var level) && !result.Contains(level))
            {
                result.Add(level);
            }
        }

        return result.Count == 0 ? DefaultLevels : result;
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
