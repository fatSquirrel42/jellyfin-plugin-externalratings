namespace Jellyfin.Plugin.ExternalRatings.Resolvers;

/// <summary>
/// Describes one external rating source a resolver can return, for the configuration UI. This is the
/// target-source counterpart to <see cref="IRatingResolver.SupportedInputProviders"/>: the resolver is
/// the single authority for which sources are selectable, so the config page renders its dropdowns from
/// this list instead of hardcoding them.
/// </summary>
/// <param name="Key">The source key exactly as it appears in the resolver's response (e.g. <c>myanimelist</c>).</param>
/// <param name="DisplayName">The human-readable name shown in the UI (e.g. <c>MyAnimeList</c>).</param>
/// <param name="Scale">The source's native value range (e.g. <c>0–10</c>), used for the conversion hint.</param>
/// <param name="Rescaled">Whether the native scale differs from Jellyfin's 0–10 and is therefore converted.</param>
public sealed record RatingSourceInfo(string Key, string DisplayName, string Scale, bool Rescaled);
