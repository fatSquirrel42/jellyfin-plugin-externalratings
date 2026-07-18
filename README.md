<h1 align="center">Jellyfin External Ratings Plugin</h1>

A Jellyfin server plugin that resolves the community score of an external source — initially
**MyAnimeList**, via the [mdblist](https://mdblist.com) API — and writes it into Jellyfin's standard
`CommunityRating` field. Targets **Jellyfin 10.11.11** (`net9.0`).

## What it does

- Looks up each configured item (Movies/Series) by its Tmdb/Imdb/Tvdb id through mdblist and reads
  the native MyAnimeList `value` (0–10 scale).
- Writes the score into `CommunityRating` (Plan A: `ItemUpdateType.None`).
- Caches results (positive + negative), batches lookups, and stays within a daily request budget,
  with a shared circuit breaker that pauses on repeated errors / HTTP 429.
- Runs on demand (scheduled task) and automatically after library scans; a realtime listener is
  planned.

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

1. **mdblist API key** (free tier = 1000 requests/day).
2. At least one **enabled library**.
3. Keep/adjust **processed levels** (defaults: Movie, Series).
4. **Dry run is ON by default** — nothing is written until you turn it off. Leave it on for a first,
   read-only pass; turn it off to actually write `CommunityRating`.

Trigger a run from **Dashboard → Scheduled Tasks → "Enrich External Ratings"** (on demand). It also
runs automatically after each library scan while **Run after library scan** is enabled. The admin-only
status endpoint `GET /Plugins/ExternalRatings/Status` reports the last run's counters and config flags.

## Verification & write-reason check

The write path uses `ItemUpdateType.None`; the manual verification that the score persists without
touching NFO files (the Plan A vs. Plan B decision) is documented in
[docs/V2-verification.md](docs/V2-verification.md).

## Development

- Build: `dotnet build` (clean under `TreatWarningsAsErrors`).
- Tests: `dotnet test --filter "Category!=Live"` (offline). Live resolver tests need
  `MDBLIST_API_KEY` set and run with `--filter "Category=Live"` (consumes quota).
