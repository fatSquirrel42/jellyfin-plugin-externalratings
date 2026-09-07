# Season/Episode support — phase 0 diagnosis

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
| `title.episode.tsv.gz` | 54.6 MB | 9,871,460 | `tconst`, `parentTconst`, `seasonNumber`, `episodeNumber` |

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

So a season average needs a **minimum-coverage threshold**, and its job is to suppress unaired
seasons rather than to paper over patchy data.

## 4. IMDb has no season 0, and 21 % of episode rows are unnumbered

Two structural facts, both verified against the raw file:

- **`seasonNumber == 0` does not exist**: zero rows in all 9.87 M. IMDb seasons start at 1.
- **2,068,360 rows (20.95 %) have `\N` for season/episode.** These are specials and unnumbered
  entries. They have their own `tconst` and often a rating, but they are **unreachable via route
  B**.

Consequence: Jellyfin's Season 0 specials can only ever be matched by route A. In the curated
control set the unnumbered share is just 1 % (60 / 6,292); 21 % is the realistic worst case across
all of IMDb.

IMDb also has **no season entity at all**, so a season score must be aggregated from its episodes.

## 5. Library-shape findings

From the test instance, all of which generalise:

- **Seasons carry only a `Tvdb` id** — no Imdb, no Tmdb (2 / 2). An IMDb resolver must walk the
  parent chain to the Series for both Season and Episode; the item's own ids are not enough.
  `ExtractProviderIds` reads only the item itself today.
- No multi-episode files (`IndexNumberEnd`) and no Season 0 in this library, so those policies
  remain untested here.
- `BuildLibraryQuery` does not constrain `IsVirtualItem`. This library has no virtual items so the
  count is 0, but with Episode enabled a library with missing-episode entries would enumerate them.

## 6. `ClearField` is far cheaper than lesson 9 assumed

See `lessons-learned.md` lesson 9 for the measurement table. Summary: a cleared rating is **not**
refilled by an ordinary refresh or by the `Scan Media Library` task — only by an explicit
*full* metadata refresh. The "slow perpetual loop" was a code-reading inference and does not hold
for the recurring paths.

At episode scale this is the difference between a one-off clear and a per-run treadmill, and it
removes the main objection to keeping `ClearField` as the default for the new levels.

## 7. Wholphin never displays a Season rating — determined from its source

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

## 8. What this means for the implementation

1. Build an **IMDb dataset resolver**; do not build on mdblist for these levels.
2. Prefer **route A**, fall back to **route B**. Validate route A: the id must differ from the
   series id and must appear as a child row in `title.episode`.
3. **Episodes first.** Wholphin does not render a Season rating at all (§7), so season
   aggregation is optional scope. If built: aggregate from episode data with a minimum-coverage
   threshold, since IMDb has no season score to read.
4. Plumb the **parent chain** and the season/episode numbers through `RatingWorkItem`,
   `RatingRequest` and `RatingCacheKey` — the cache key has no S/E field today, so every episode
   of a series would collide.
5. Keep `ClearField` as the default; gate the new levels behind config opt-in for volume, not for
   safety.
