# Jellyfin 12.0 compatibility — finding (2026-07-18)

**Short version:** the current release (`0.1.0.0`) targets Jellyfin **10.11.x** and will **not** run on
Jellyfin 12.0. Supporting 12.0 requires a **separate build**, deliberately deferred (see below).

## Why a separate build is required

Jellyfin 12.0(-rc) is a major platform bump:

- **Runtime:** 12.0 moves to **.NET 10** (`net10.0`); this plugin builds against **`net9.0`**.
- **Host assemblies:** 12.0 ships `Jellyfin.Controller` / `Jellyfin.Model` **`12.0.0-rc*`** (targeting
  net10.0); this plugin references the **`10.11.11`** packages.

A single binary cannot load on both servers — the framework and the referenced host assemblies differ, so
the 10.11-built DLL is **binary-incompatible** with a 12.0 runtime.

## The `targetAbi` trap — do NOT just bump it

`targetAbi` is a **minimum** server version, not an exact match: the server loads a plugin when
`serverVersion >= targetAbi`. Because `12.0 > 10.11.11`, a plugin with `targetAbi: 10.11.11.0` **passes**
the manifest gate on a 12.0 server — the catalog will happily offer/install it — but the assembly then
**fails to load at runtime** (net9 vs net10, different host assemblies). So raising `targetAbi` alone would
make things *worse* (the server would try to load an incompatible DLL). The 12.0 RC notes themselves tell
users to reinstall plugins from the unstable repository for exactly this reason.

## What a 12.0 build needs (when we do it)

- A second build target: **`net10.0`**, referencing **`Jellyfin.Controller` / `Jellyfin.Model`
  `12.0.0-rc2`** (or the eventual 12.0 stable), with **`targetAbi: 12.0.0.0`**.
- Recompile against the 12.0 Controller assembly and fix any breaking API changes (the RC1 changelog notes
  "remove some deprecated API members" and "remove legacy API route middleware"; audit `ILibraryManager`,
  `IScheduledTask` / `IHostedService`, and `IHasWebPages` usage).
- Distribute it via a **separate `unstable` manifest / repository**, not the stable one.

## Why deferred now

Two prerequisites are missing in the current environment:

- **No .NET 10 SDK** installed (only the .NET 9 SDK) — cannot build `net10.0`.
- **No Jellyfin 12.0-rc1 server** available locally to smoke-test against.

12.0 is also still an RC, not stable. The plan is to add the net10.0 build + unstable channel once the
.NET 10 SDK and a 12.0 server are available (ideally when 12.0 stabilizes). Until then, `0.1.0.0` remains a
10.11.x-only release.

## Sources

- Jellyfin 12.0-rc1 release: <https://github.com/jellyfin/jellyfin/releases/tag/v12.0-rc1>
- `Jellyfin.Controller` 12.0.0-rc2 (net10.0): <https://www.nuget.org/packages/Jellyfin.Controller/12.0.0-rc2>
- targetAbi = minimum-version semantics: <https://forum.jellyfin.org/t-question-about-targetabi>
