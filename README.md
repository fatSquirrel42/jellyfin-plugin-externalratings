<h1 align="center">Jellyfin External Ratings Plugin</h1>

A Jellyfin server plugin that resolves an external community score and writes it into Jellyfin's
standard `CommunityRating` field. Targets **Jellyfin 10.11.x** (`net9.0`). (Jellyfin 12.0 needs a
separate net10.0 build — see [docs/jellyfin-12-compat.md](docs/jellyfin-12-compat.md).)

You pick a **rating source**; the plugin works out how to reach it. There is no backend to choose.

| Source | Levels | Needs |
| --- | --- | --- |
| **IMDb** | Movies, Series, Seasons, Episodes | Nothing — local copies of IMDb's own dataset files (~9 MB of ratings, plus ~55 MB of episode listings for season scores), refreshed daily |
| MyAnimeList, Metacritic, Trakt, TMDb, Rotten Tomatoes, Letterboxd | Movies, Series | An mdblist API key; 1000 requests/day on the free tier |

Only IMDb reaches **seasons and episodes**. mdblist's per-episode data became a paid perk and is
unavailable on its batch endpoint either way — the evidence is in
[docs/level-support-diagnosis.md](docs/level-support-diagnosis.md).

For IMDb the local file is used wherever the item has an IMDb id. Where it does not but has a TMDb
id, the score is fetched through mdblist instead — so an API key widens IMDb coverage without being
required for it.

## What it does

- Looks up each configured item by its provider id and writes the chosen source's score, converted
  to Jellyfin's 0–10 scale.
- **Episodes** match on the episode's own IMDb id — the one TheTVDB's own cross-reference supplies,
  even in a TVDB-only setup. Never by (series, season, episode) position: TheTVDB and IMDb disagree
  on numbering often enough that positional matching writes the wrong episode's score silently.
- **Seasons** have no IMDb entry at all, so their score is the unweighted mean of the episodes the
  library holds — the same figure IMDb's own site shows.
- Writes the score into `CommunityRating` using `ItemUpdateType.MetadataEdit`, so a per-library NFO
  saver (if enabled) rewrites the `<rating>` and the DB and NFO stay consistent.
- Caches results (positive + negative), batches lookups, and stays within a daily request budget,
  with a shared circuit breaker that pauses on repeated errors / HTTP 429.
- Three triggers: an on-demand scheduled task, an automatic pass after each library scan, and a
  realtime item listener that enriches items as they change (debounced, self-write-guarded, and it
  ignores manual edits so it never fights the user).
- **Respects locked items:** an item with metadata locked (`IsLocked`) is never overwritten, so
  locking is the durable way to protect a hand-set rating.
- **Admin actions** on the config page: *Run enrichment now*, *Restore all original ratings* (resets
  every plugin-changed rating to the value it had before, from the backup), and *Clear rating cache*.

## Install from the plugin repository

In Jellyfin: **Dashboard → Plugins → Repositories → +** and add this manifest URL:

```
https://raw.githubusercontent.com/fatSquirrel42/jellyfin-plugin-externalratings/main/manifest.json
```

Then **Catalog → Metadata → External Ratings → Install** and restart Jellyfin. Releasing a new version is
documented in [RELEASING.md](RELEASING.md).

## Build & install for local testing

Requires the .NET 9 SDK.

```pwsh
# Build and copy the plugin into your local Jellyfin instance, then restart Jellyfin:
./scripts/install-local.ps1

# If your instance's data directory isn't %LOCALAPPDATA%\jellyfin, point at it:
./scripts/install-local.ps1 -JellyfinDataDir 'D:\JellyfinData'
```

This builds `src/Jellyfin.Plugin.ExternalRatings` and copies the plugin DLL + `meta.json` into
`<JellyfinDataDir>/plugins/External Ratings/`. In VS Code, the **build-and-install** task does the
same. (Packaged releases are produced by CI via the shared Jellyfin JPRM workflows; the script is for
local dev/test.)

## First run

After restart, open **Dashboard → Plugins → External Ratings** and set:

1. A **rating source**, and at least one **enabled library**. Only IMDb works without an mdblist
   API key.
2. The **item levels** to process. Movies and Series are on by default; Seasons and Episodes are
   off. Enabling episodes can multiply the number of processed items by a hundred in a TV library,
   so turn them on deliberately — and note that in a library whose source has no score at a level,
   those items are *cleared* rather than left alone.
3. **Dry run is ON by default** — nothing is written until you turn it off. Leave it on for a first,
   read-only pass; turn it off to actually write `CommunityRating`.

Trigger a run from **Dashboard → Scheduled Tasks → "Enrich External Ratings"** (on demand). It also
runs automatically after each library scan while **Run after library scan** is enabled. The admin-only
status endpoint `GET /Plugins/ExternalRatings/Status` reports the last run's counters and config flags.

## Verification & write-reason check

The write path uses `ItemUpdateType.MetadataEdit` (Plan B), so the change flows through the host's
per-library metadata savers and keeps the DB and any NFO `<rating>` consistent. The live verification
behind that decision — and the step-10 checks for locked-item protection, restore, and clear-cache — is
documented in [docs/V2-verification.md](docs/V2-verification.md).

## Development

- Build: `dotnet build` (clean under `TreatWarningsAsErrors`).
- Tests: `dotnet test --filter "Category!=Live"` (offline). Live resolver tests need
  `MDBLIST_API_KEY` set and run with `--filter "Category=Live"` (consumes quota).
