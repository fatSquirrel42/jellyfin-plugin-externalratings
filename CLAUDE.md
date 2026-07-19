# CLAUDE.md

Guidance for Claude Code (and other AI agents) working in this repository. This file captures the
things you **cannot** infer from the code and that will otherwise break your first build, test, or
commit. For install/first-run see [README.md](README.md); for design rationale see [`docs/`](docs).

## Overview

Jellyfin **10.11.x** server plugin (`net9.0`) that resolves an external community score — initially
**MyAnimeList** via the [mdblist](https://mdblist.com) API — and writes it into Jellyfin's native
`CommunityRating`. Plugin GUID `54015a93-7e43-4406-adcf-a15cc251dff1`. Single source project
(`src/Jellyfin.Plugin.ExternalRatings`) + test project (`tests/…Tests`), both `net9.0`.

## Build & test commands

Requires the **.NET 9 SDK**.

- Build: `dotnet build` (or `-c Release`). **Must stay clean** — the main project has
  `TreatWarningsAsErrors=true`, `AnalysisMode=AllEnabledByDefault`, and the `jellyfin.ruleset`.
  A warning is a failed build.
- Tests (offline, default): `dotnet test --filter "Category!=Live"`.
- Live resolver tests: `dotnet test --filter "Category=Live"` — needs env var `MDBLIST_API_KEY` and
  **consumes API quota** (free tier = 1000/day). A bare `dotnet test` stays green because the live
  test *skips* when the key is unset (`[Trait("Category","Live")]` + `[SkippableFact]`).
  - To pass the key into one command without restarting the agent (env changes don't propagate to
    already-running shells), do it in a single call:
    `$env:MDBLIST_API_KEY = [Environment]::GetEnvironmentVariable('MDBLIST_API_KEY','User'); dotnet test …`
- Local install into a Jellyfin instance: `scripts/install-local.ps1`. VS Code tasks: `build`,
  `build-and-install`.

## Build & analyzer constraints (read before writing code)

The strict ruleset makes several non-obvious things mandatory:

- **Domain types are `internal`** + `InternalsVisibleTo("…Tests")` (this is what keeps CS1591
  doc-comment errors off; SA1600 is disabled but CS1591 fires on `public` members). Test fakes must
  also be `internal`. A `public` test method may **not** take an internal-typed parameter → do
  **not** use `[Theory]`/`[InlineData]` over an internal enum (e.g. `ItemLevel`); use separate
  `[Fact]`s.
- **Public-shell / internal-domain boundary (CS0051):** host entry points (`IScheduledTask`,
  `IPluginServiceRegistrator`, controllers, tasks) must be `public`, but no `public`/`protected`
  signature may expose an internal type. The pattern: **`RatingEnrichmentService` is the only public
  bridge** — a DI-singleton composition root that takes public host services and builds the internal
  graph itself. A public class holding *private* fields of internal type is fine.
- **StyleCop: one public type per file** (SA1402), **filename = first type** (SA1649). Split every
  enum/record/interface into its own file.
- **CA rules escalated to errors** in `jellyfin.ruleset` — the ones that bite:
  - CA1305 — pass `CultureInfo.InvariantCulture` on formatting.
  - CA2016 — always forward the `CancellationToken`.
  - CA2254 / CA1727 — constant log message templates, named placeholders, no string interpolation
    in log calls (SerilogAnalyzer also enforces this).
  - CA1859 — a `private` helper returning `IReadOnly…` that only ever returns a concrete
    `List`/`Dictionary` must declare the concrete type.
  - CA1001 — a type owning a `SemaphoreSlim` field must implement `IDisposable`
    (see `FileRatingCache`, `BackupStore`).
- **Config must be XmlSerializer-safe:** use **arrays, not `List<T>`** (XmlSerializer appends to a
  pre-initialized list and double-counts defaults — `ConfigRoundtripTests` guards this), enums as
  strings, durations as whole days, no `Dictionary`/`TimeSpan`. CA1819 is suppressed on the config
  DTO.
- **Test project needs runtime Jellyfin assemblies.** The main project references
  `Jellyfin.Controller`/`Jellyfin.Model` with `ExcludeAssets=runtime` (the host supplies them), so
  tests touching a Jellyfin type add a plain `Jellyfin.Model 10.11.11` reference and
  `FrameworkReference Microsoft.AspNetCore.App`. `Microsoft.Extensions.Logging.Abstractions` is
  pinned to **`9.0.11`** (lower trips NU1605).
- **Style** (`.editorconfig`): LF line endings, 4-space indent, `_`/`s_` field prefixes, Allman
  braces, `system` usings first.

## Architecture map (folders = namespaces)

| Location | Role |
| --- | --- |
| `Plugin.cs`, `PluginServiceRegistrator.cs`, `RatingEnrichmentService.cs` (root) | Entry point; DI wiring; **the public facade / composition root** (owns the shared `CircuitBreaker`, caches, `HttpClient`). |
| `Configuration/` | `PluginConfiguration` (arrays!), `ResolverSetting`, `LibrarySourceSetting`, embedded `configPage.html`. |
| `Core/` (+ `Core/Abstractions/`) | Host-independent domain: `RatingPipeline` (the §6 decision table), `EnrichmentRunner`, `RatingPrefetcher`, `PluginConfigurationMapper`, `InputIdSelector`, `CircuitBreaker`, `ItemLevel`, `IClock`; seam interfaces (`IItemWriter`, `IRatingCache`, `IBackupStore`). |
| `Resolvers/` (+ `Mdblist/`) | `IRatingResolver`/`IBatchRatingResolver`, `MdblistResolver`, `RatingSourceInfo` + wire DTOs. |
| `Persistence/` | `FileRatingCache`, `BackupStore`, `DailyRequestCounter`, `ICacheFileStore`. |
| `Infrastructure/` | `JellyfinItemWriter` (implements `IItemWriter`), `BudgetTrackingHandler`. |
| `Api/` | `StatusController` (`[Authorize(RequiresElevation)]`) + DTOs. |
| `Tasks/` | `EnrichRatingsScheduledTask`, `EnrichRatingsPostScanTask`, `ItemChangedListener` (realtime hosted service). |

## Key patterns

- **Resolver capability drives the UI.** `IRatingResolver.SupportedInputProviders` (presence of a
  level key = "this level is supported") and `SupportedRatingSources` flow through
  `RatingEnrichmentService.GetSupportedLevels/GetSupportedSources` → `StatusController` →
  `configPage.html`, which builds the level/source dropdowns from the endpoint instead of hardcoding
  them. Add a source/level by extending the resolver, not the UI.
- **Humble Object:** tasks, controllers, and the item writer are thin shells over testable core
  logic behind `Core/Abstractions`. Fakes live in `tests/…/Fakes/`.
- **Live config reads via `Func<>` accessors + `IClock`/`SystemClock`** (no captured snapshots), so
  toggles like `DryRun` (**default `true`** — nothing is written until turned off) apply without a
  restart.
- **`EnrichRatingsPostScanTask` is deliberately NOT registered in DI** — the host discovers
  `ILibraryPostScanTask` by assembly scanning. Don't "fix" this by adding it to
  `PluginServiceRegistrator`.
- Library enumeration uses `InternalItemsQuery` with **`AncestorIds`** (not `TopParentIds` — a
  documented gotcha).

## Version bump lockstep (CI trap)

The version lives in **four** files that must match exactly: `Directory.Build.props`, root
`build.yaml`, `src/…/meta.json`, and `manifest.json`. The `version-consistency.yaml` CI job runs
`scripts/check-versions.sh` and fails on any mismatch; `targetAbi` must also agree across
build.yaml/meta.json/manifest.json. Keep `build.yaml` ASCII-only. Full release process:
[RELEASING.md](RELEASING.md).

## Further reading

- [`docs/lessons-learned.md`](docs/lessons-learned.md), [`docs/mdblist-v1-notes.md`](docs/mdblist-v1-notes.md),
  [`docs/V2-verification.md`](docs/V2-verification.md).
- [`docs/jellyfin-12-compat.md`](docs/jellyfin-12-compat.md) — the one open roadmap item: a separate
  `net10.0` build for Jellyfin 12.
