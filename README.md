<h1 align="center">Jellyfin External Ratings Plugin</h1>

A Jellyfin server plugin that resolves the community score of an external source — initially
**MyAnimeList**, via the [mdblist](https://mdblist.com) API — and writes it into Jellyfin's standard
`CommunityRating` field. Targets **Jellyfin 10.11.x** (`net9.0`). (Jellyfin 12.0 needs a separate
net10.0 build — see [docs/jellyfin-12-compat.md](docs/jellyfin-12-compat.md).)

## What it does

- Looks up each configured item (Movies/Series) by its Tmdb/Imdb/Tvdb id through mdblist and reads
  the native MyAnimeList `value` (0–10 scale).
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

1. **mdblist API key** (free tier = 1000 requests/day).
2. At least one **enabled library**.
3. Keep/adjust **processed levels** (defaults: Movie, Series).
4. **Dry run is ON by default** — nothing is written until you turn it off. Leave it on for a first,
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
