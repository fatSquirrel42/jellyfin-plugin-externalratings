# Season/Episode support — phase 0 diagnosis

> **Status: implemented.** Every decision below is in the code. `ImdbDatasetResolver` covers
> Movie/Series/Episode by IMDb id, and scores a Season as the mean over *IMDb's* episodes for it
> — identified from the local episodes' ids, never from the season number; levels are opt-in via
> `PluginConfiguration.EnabledLevels`. Two passages were **revised on 2026-09-08** after the
> requirement changed from "the season you have" to "the season": §2's table marker and the two
> §9 sections on the episode map. The revisions say so where they stand. The provider is **not** configurable — `Core/RatingRouter`
> picks it per item from (source × level × available ids), so §5's "which id do I have" question
> is answered at runtime rather than by the user. This document is kept as the evidence for *why*,
> not as a plan.

Measured 2026-09-07 against the local test instance (Jellyfin 10.11.11) and the IMDb
non-commercial datasets. Reproduce with `scripts/diagnose-levels.ps1`.

The question this phase answers: **how would an episode be matched to an IMDb score, and what
does enabling the Season/Episode levels actually cost?** The implementation plan depends on these
numbers, so they are recorded here rather than left in a session transcript.

## 1. mdblist cannot supply episode scores any more

`tests/.../Golden/show_found.json` (live capture 2026-07-18) contains a full
`seasons[].episodes[].rating` matrix, and a previous design round planned to navigate it. **That
path is closed.** Per the official OpenAPI schema (`https://api.mdblist.com/schema/`) the matrix
now sits behind `?append_to_response=episode_ratings` and is gated:

> `episode_ratings` (shows only): for Supporters, the full per-episode/season critic rating matrix
> […] For non-Supporters, an aggregate-only summary with an empty `seasons` list […] the
> per-episode breakdown is a Supporter perk and is never sent to non-Supporters.

Verified live with the project key (`is_supporter: false`, `plan: "Free"`): the response is
`{"seasons":[], "total_rated_episodes":62, "overall_average":89}`. The **batch** POST does not
accept `episode_ratings` at all (only `keyword`, `review`), so even a Supporter account would pay
one request per series with no batching — against a 1000/day limit.

Do not resurrect the payload-navigation design without re-checking this gate.

## 2. The IMDb datasets carry exactly the same data, free

Two files from `https://datasets.imdbws.com/`, refreshed daily:

| file | size (gz) | rows | content |
| --- | --- | --- | --- |
| `title.ratings.tsv.gz` | 8.6 MB | 1,711,454 rated titles | `tconst`, `averageRating`, `numVotes` |
| `title.episode.tsv.gz` | 54.6 MB | 9,871,460 | `tconst`, `parentTconst`, `seasonNumber`, `episodeNumber` — **used for season scores only, see §9** |

**Cross-validation against mdblist:** Breaking Bad has 62 episode rows in `title.episode`, all 62
scored in `title.ratings`, mean **8.9371** → 89 on the 0–100 scale. mdblist's gated aggregate
reported exactly `total_rated_episodes: 62, overall_average: 89`. The dataset reproduces the
Supporter-only data precisely.

Licence is "personal and non-commercial use": download at runtime on the user's server, never
bundle or redistribute.

## 3. Two matching routes

- **Route A** — the episode's own IMDb id → `title.ratings`. Unambiguous, no numbering risk.
- **Route B** — (series `tconst`, `ParentIndexNumber`, `IndexNumber`) → `title.episode` → episode
  `tconst` → `title.ratings`. Covers episodes that carry no id of their own.

Neither prior-art plugin uses `title.episode.tsv.gz`: `verybadsoldier/jellyfin-plugin-imdbratings`
and `Druidblack/Jellyfin.Plugin.MDBList_Ratings` both load only `title.ratings` and therefore
require the episode to already have an IMDb id (Druidblack otherwise falls back to OMDb — separate
key, 1000/day, with a persistent cooldown store). Route B is what closes that gap.

### Decision: route B is not built

Route B is **rejected** for this setup. Futurama (`tt0149460`) is the proof:

| | S1–S5 | S6 | S7 | S8–S10 | total |
| --- | --- | --- | --- | --- | --- |
| IMDb | 9 / 20 / 15 / 12 / 16 | 16 | 13 | 13 each | 14 seasons |
| TheTVDB | identical | **26** | **26** | 10 each | 11 seasons + specials |

They agree up to S5 and diverge from S6 on. Concretely: **TheTVDB S7E5 is "Zapp Dingbat"
(aired 2012-07-11); IMDb S7E5 is "The Duh-Vinci Code" (aired 2010-07-15, rated 7.3).** Route B
would look up `(tt0149460, 7, 5)` and write 7.3 onto "Zapp Dingbat" — no error, no warning, just a
wrong number. Where IMDb has *fewer* episodes than TheTVDB in a season, route B instead finds
nothing and `ClearField` nulls the rating: annoying, but at least honest. The silent-wrong-value
case is the disqualifying one.

Route A does not have this problem *because it does not guess*. See §5 on where the id comes from.

If route B is ever revisited, gate it on a per-series structural check — compare IMDb's episode
count per season against Jellyfin's and refuse route B where they differ. That would have switched
Futurama off automatically from S6.

### Measured on the test library (17 episodes, 2 series)

| metric | result |
| --- | --- |
| episodes with their own IMDb id | 17 / 17 |
| …whose id is wrongly the *series* id | 0 |
| …scored in `title.ratings` | 17 / 17 |
| route B inputs present (series id + S + E) | 17 / 17 |
| route B mapped to an episode `tconst` | 17 / 17 |
| **routes A and B resolved to the same `tconst`** | **17 / 17** |

Both series are anthologies (Star Wars: Visions S3, The Boys Presents: Diabolical S1), the case
where numbering divergence was most expected. No divergence found — but n=17, so this is a
mechanism check, not a statistic.

### Measured on a 39-series control set (6,292 episodes, no library needed)

- **99.0 %** of episodes have a rating (6,227 / 6,292).
- The 1 % gap is almost entirely **unaired** episodes: the only seasons below 90 % coverage are
  The Simpsons S38–S40, South Park S29–S30, Rick and Morty S10–S12 and True Detective S5 — all at
  0 %, all announced-but-not-aired.
- **96.1 %** of seasons (268 / 279) have 100 % episode coverage; 3.2 % are the unaired ones.

This is what makes averaging over IMDb's season list sound: for a finished season the list is
effectively fully rated, so the mean over it is the mean over the whole season. The 1 % that is
unrated is announced-but-unaired, which is the airing-season question — **parked deliberately**,
not answered here.

> Two coverage gates were tried and both removed on 2026-09-08. First a configurable minimum-
> coverage threshold, which could not do its job: `CollectSeasonMembers` dropped episodes without
> an IMDb id *before* counting, so the denominator was "episodes already matched" — a season with
> 2 of 12 matched reported 100 % coverage and got a two-episode average, exactly the case the
> threshold was documented to prevent. It was replaced by an all-or-nothing rule over the
> episodes on disk, which went the same way a day later once the season became IMDb's season:
> with that denominator the rule would fire on every season IMDb knows more episodes of than the
> user has. There is no gate now.

**IMDb's own season average is the unweighted mean**, and it is reproducible from the dataset.
IMDb displays "8.6 Average from 43K episode ratings" for `tt7078180` season 1; computed from
`title.ratings` that season's 13 episodes give an arithmetic mean of **8.6000** and a vote total of
**42,905**. The vote-weighted mean would be 8.8560, so the displayed figure is unambiguously the
simple mean of the episode averages, with the "43K" being the sum of votes. Match that formula if
season support is ever built.

## 4. IMDb has no season 0, and specials carry no numbers

Two structural facts, both verified against the raw file:

- **`seasonNumber == 0` does not exist**: zero rows in all 9.87 M. IMDb seasons start at 1.
- **2,068,360 rows (20.95 %) have `\N` for season/episode.** These are specials and unnumbered
  entries. They have their own `tconst` and often a rating, but they are **unreachable via route
  B**.

With route B dropped this stops being a coverage problem and becomes a naming one: IMDb *does*
rate specials, they simply have no `(series, season, episode)` coordinate. Route A reaches them
through the id like any other episode — Futurama's four movies sit in TheTVDB season 0 and in IMDb
season 5, and the id bridges that without either side agreeing on a number. Only specials that
TheTVDB has not cross-referenced fail to resolve, and `ClearField` then nulls them.

IMDb also has **no season entity at all**, so a season score must be aggregated from its episodes.

## 5. Where the IMDb id comes from, and what else is available

The target setup uses **TMDb for movies and TheTVDB only for series** — yet episodes still carry
IMDb ids. `jellyfin-plugin-tvdb`, `TvdbEpisodeProvider.cs:249`:

```csharp
var imdbID = episode.RemoteIds?.FirstOrDefault(x => x.SourceName == "IMDB")?.Id;
item.SetProviderIdIfHasValue(MetadataProvider.Imdb, imdbID);
```

TheTVDB maintains its own cross-references to other databases and the plugin copies the IMDB one
straight onto the item (same for Series at `TvdbSeriesProvider.cs:168`). This is an **explicit
per-episode link curated at TheTVDB**, not a positional guess — which is exactly why route A is
immune to the numbering divergence that kills route B. `SetProviderIdIfHasValue` also means: no
cross-reference at TheTVDB, no id in Jellyfin, and then the episode simply does not resolve.

TheTVDB's API supports five remote-id sources (`TvdbClientManager.cs:359`: IMDB, TMDB, Zap2It,
TV Maze, EIDR) but the plugin copies only a subset, and it differs by item type:

| item | ids copied |
| --- | --- |
| **Episode** | **IMDB only** |
| Series / Movie / Person | IMDB, Zap2It, TheMovieDB.com |

Confirmed against the live instance: episodes carry exactly `{Imdb, Tvdb}`, series carry
`{Imdb, Tmdb, Tvdb, TvdbCollection, TvdbSlug}`. So at episode level IMDb is the *only* free
cross-reference — a TMDb-based episode resolver would have no id to work with even though
TheTVDB knows one.

**Open follow-up:** other services that publish episode-level cross-references (Trakt and TMDb's
`/tv/{id}/season/{n}/episode/{m}/external_ids` both do, TVmaze partially, plus Wikidata/EIDR).
Worth surveying only if route-A coverage turns out to be thin in practice.

## 6. Other library-shape findings

- **Seasons carry only a `Tvdb` id** — no Imdb, no Tmdb (2 / 2). An IMDb resolver must walk the
  parent chain to the Series for both Season and Episode; the item's own ids are not enough.
  `ExtractProviderIds` reads only the item itself today.
- No multi-episode files (`IndexNumberEnd`) and no Season 0 in this library, so those policies
  remain untested here.
- `BuildLibraryQuery` does not constrain `IsVirtualItem`. This library has no virtual items so the
  count is 0, but with Episode enabled a library with missing-episode entries would enumerate them.

## 7. `ClearField` is far cheaper than lesson 9 assumed

See `lessons-learned.md` lesson 9 for the measurement table. Summary: a cleared rating is **not**
refilled by an ordinary refresh or by the `Scan Media Library` task — only by an explicit
*full* metadata refresh. The "slow perpetual loop" was a code-reading inference and does not hold
for the recurring paths.

At episode scale this is the difference between a one-off clear and a per-run treadmill, and it
removes the main objection to keeping `ClearField` as the default for the new levels.

## 8. Wholphin never displays a Season rating — determined from its source

Answered by reading `damontecres/Wholphin` (Android TV, the target client per
`lessons-learned.md`) at 2026-09-05 rather than by eye:

- **A Season has no detail page.** `BaseItem.destination()` maps `BaseItemKind.SEASON`
  unconditionally to `Destination.SeriesOverview(...)` — the comment says "Redirect episodes &
  seasons to their series if possible". There is no `ui/detail/season/` package.
- **The series page always shows the *series'* rating.** `SeriesDetails.kt` renders
  `series.ui.quickDetails`; selecting a season does not swap in season-level details.
- **No card renders a rating.** `SeasonCard.kt` draws title and subtitle only, and `SimpleStarRating`
  is used nowhere outside `Rating.kt` itself and a filter control.

Episodes are the opposite — they do display it, in two places:

- `EpisodeDetailsHeader.kt` → `QuickDetails(ep.ui.quickDetails, …)` on the episode page.
- `FocusedEpisodeHeader.kt` → the focused episode's details on the series page.

`QuickDetailsData` builds `communityRating` type-agnostically (`data.communityRating?.let { … }`,
formatted `%.1f` plus a star glyph), gated by the `COMMUNITY_RATING` display toggle, whose default
is all toggles on (`AppPreference.DisplayTogglesPref.defaultValue = DisplayToggle.entries`). Note
the toggle is user-facing: if it has been switched off, no rating shows anywhere.

**Consequence: writing Season ratings buys this user nothing.** The value would sit in the database
unread. Season support is therefore optional — worth building only for other clients (jellyfin-web
shows a season rating) or not at all.

## 9. What this means for the implementation

1. Build an **IMDb dataset resolver**; do not build on mdblist for these levels.
2. **Route A only.** Route B is rejected (§3). Validate route A: the id must differ from the series
   id and must appear as a child row in `title.episode`. An episode with no IMDb id does not
   resolve, and `ClearField` then nulls whatever was there.
3. **Episodes and Seasons.** IMDb has no season entity, so a season score is aggregated — but over
   *IMDb's* season, not the episodes on disk (see below). Note this value is invisible in
   Wholphin (§8) and shows only in jellyfin-web; it is filled anyway because IMDb's own page
   shows it.
4. **Dropping route B removes most of the plumbing.** With route A the input id is the episode's
   own `tconst`, so `RatingCacheKey(resolver, source, "Imdb", "tt16364366", Episode)` is already
   unique — no season/episode fields, no `CurrentSchemaVersion` bump, and no parent-chain walk in
   `ExtractProviderIds`. All of that is only needed if Season support is built, where every season
   of a series would otherwise share the series id.
5. Keep `ClearField` as the default; gate the new levels behind config opt-in for volume, not for
   safety.

### Revised: `title.episode.tsv.gz` is used, for seasons only

This section first concluded the file was unnecessary. That was wrong, and the reason is recorded
here rather than deleted, because the two arguments are worth keeping apart:

- **Validation — still does not need the file.** The realistic failure — an episode carrying its
  *series'* id — is caught by comparing against the parent series' id, no dataset needed
  (measured: 0 / 17 here). The broader "is this `tconst` an episode?" test buys little, because
  the id was curated per episode at TheTVDB rather than guessed. A special whose id points at the
  *film* (Futurama's movies) resolves to that film's rating, which is the right answer anyway.
- **Season averaging — does need it.** The requirement changed: the season is the season, with all
  of its episodes, independent of what is on disk. Nothing local can supply that episode list.
  Jellyfin knows nothing about the missing ones in this setup — the missing-episode fetcher is off
  and `DisplayMissingEpisodes` is unset, so there are no virtual placeholders to count. The list
  can therefore only come from IMDb.

So `title.episode.tsv.gz` is downloaded, and the refresh interval governs 63 MB rather than 8.6.
Route B stays rejected — having the file does not make positional matching safe (§3).

### Season averaging: identify by episode identity, average over IMDb's season

IMDb publishes no season row; the value its site shows is computed, and it is the plain unweighted
mean of the episode averages (§3, verified against `tt7078180` S1 = 8.6000 exactly — the
vote-weighted mean would be 8.8560). That is the figure to reproduce.

The hard part is knowing *which* IMDb season a Jellyfin season is, **without** translating the
season number — that would be exactly the positional guess route B was rejected for. The rule:

```
members = the IMDb ids of this Jellyfin season's episodes (only the ones that have one)
seasons = { episodeMap.SeasonOf(id) | id ∈ members }        → (parentTconst, seasonNumber)

seasons.Count != 1  → NoMatch, logged differently for each cause:
                       0 = no member resolvable, >1 = this season has no single IMDb counterpart
otherwise:
   children = episodeMap.EpisodesOf(parent, season)         ← IMDb's complete season
   values   = the children that have a `title.ratings` row
   result   = unweighted mean, one decimal, away from zero
```

The identity comes from the episodes themselves, never from their position. And it self-checks: on
Futurama-style divergence the members land in several IMDb seasons and that is *detected* rather
than guessed at — the per-series structural check §3 already suggested.

Two consequences that follow and are intended:

- **A partially downloaded season still gets IMDb's figure.** With one episode of eight on disk
  the score is the eight-episode mean, because the season is the season. This is the point of the
  design, and it reverses an earlier decision in this document to average only what the library
  holds.
- **Specials can never identify themselves.** IMDb has zero season-0 rows across all 9.87 M (§4),
  so a specials season resolves to nothing and is cleared under `ClearField`.

There is no completeness gate any more. It existed only because the denominator used to be "what is
on disk"; with IMDb's season as the denominator it would fire on every season IMDb knows more
episodes of than the user has. Unrated episodes simply do not appear in the mean — which is what
IMDb's own figure does too. **Airing seasons are deliberately out of scope here** and parked for
later.

Two implementation notes:

- Compute it from the resolver's own lookups, never from `CommunityRating` values already written —
  otherwise the result depends on write order and on `DryRun`.
- **An unavailable episode map is an `Error`, never a `NoMatch`** — sibling of the existing empty-
  index check. Without that, one failed 54.6 MB download would clear every season score in the
  library under `ClearField`.

Verified against the real data before building: Diabolical S1 — all 8 episodes on
`(tt16350094, S1)`, IMDb lists exactly 8, all rated, mean 6.7250 → **6.7**. Visions S3 — all 9 on
`(tt13622982, S3)`, IMDb lists 9, all rated → **6.6**. Both are complete locally, so they are a
clean regression probe: the values must not move.

Caveat on the display side: per §8 Wholphin renders no Season rating, so this value is write-only
for the primary client. It is filled anyway, because IMDb's own page shows it; jellyfin-web does
display it.
