namespace Jellyfin.Plugin.ExternalRatings.Core;

/// <summary>
/// How often the local copy of IMDb's ratings dataset is re-downloaded.
/// </summary>
/// <remarks>
/// IMDb regenerates the file once a day, so anything below <see cref="Daily"/> would only add
/// traffic. <see cref="Never"/> means never *re-download*: a missing file is still fetched on first
/// use, because the resolver has nothing to answer with otherwise.
/// </remarks>
internal enum ImdbDatasetRefreshInterval
{
    /// <summary>Re-download once a day, matching how often IMDb regenerates the file.</summary>
    Daily,

    /// <summary>Re-download once a week.</summary>
    Weekly,

    /// <summary>Re-download once every 30 days.</summary>
    Monthly,

    /// <summary>Keep the copy that is there; only fetch when none exists.</summary>
    Never
}
