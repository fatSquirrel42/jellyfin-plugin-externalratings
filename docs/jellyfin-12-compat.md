# Jellyfin 12.0 compatibility — the cut (2026-09-08)

**Short version:** Jellyfin 12.0 shipped on 2026-09-08 and runs on **.NET 10**. From `1.0.0.0` the
plugin targets `net10.0` and Jellyfin **12.0+** only. `0.2.0.0` is the last release of the 10.11.x
line and stays in `manifest.json` for servers still on 10.11.

## Why the two lines are separate binaries

- **Runtime:** 12.0 moves to **.NET 10** (`net10.0`); the 10.11 line built against **`net9.0`**.
- **Host assemblies:** 12.0 ships `Jellyfin.Controller` / `Jellyfin.Model` **`12.0.0`** (net10.0);
  the 10.11 line referenced the **`10.11.11`** packages.

A single binary cannot load on both servers — the framework and the referenced host assemblies
differ. This is not a versioning choice we made; it is what "retarget and rebuild" in the 12.0
release notes means.

**The port itself was almost free.** Every host API the plugin uses is unchanged in 12.0 —
`ILibraryManager` (`GetItemById`, `GetItemList(InternalItemsQuery)`, `GetCollectionFolders`,
`GetVirtualFolders`, `UpdateItemAsync`, `ItemAdded`/`ItemUpdated`, `IsScanRunning`),
`ILibraryPostScanTask`, `IPluginServiceRegistrator`, `BasePlugin<T>(IApplicationPaths, IXmlSerializer)`,
`IHasWebPages`, `TaskTriggerInfo`, `Policies.RequiresElevation`, `Jellyfin.Data.Enums.BaseItemKind`.
The 12.0 breaking changes (`ISearchEngine`→`ISearchManager`, `IUserManager.Users`→`GetUsers()`,
`IItemRepository`→`IItemPersistenceService`, removal of the `/emby/` and `/mediabrowser/` routes)
touch nothing we call. The build produced zero analyzer errors on the .NET 10 rule wave.

The one real behaviour change found: **`HttpClient` no longer throws before dispatching to the
handler when the token is already cancelled.** With a real `SocketsHttpHandler` the transport still
observes the token, so production behaviour is unchanged — but `MdblistResolver`'s promise that
caller cancellation propagates (rather than becoming an `Error` result) silently depended on that
pre-check. It now calls `ThrowIfCancellationRequested()` itself.

## The `targetAbi` trap — why old entries stay in the manifest

`targetAbi` is a **minimum** server version, not an exact match: the server accepts a plugin version
when `serverVersion >= targetAbi`. Two consequences, in opposite directions:

- **A 10.11.x server never sees `1.0.0.0`** (`12.0.0.0 > 10.11.11`). It is filtered out of the
  catalog, so those users keep being offered `0.2.0.0` and cannot install a build that would fail to
  load. This is the direction that matters, and it works.
- **A 12.0 server still sees `0.2.0.0`** (`10.11.11.0 <= 12.0`) and could install it by hand from the
  version dropdown, where it would fail to load. There is no `maxAbi` in the manifest schema to
  express the upper bound. Reported as
  [jellyfin/jellyfin#11331](https://github.com/jellyfin/jellyfin/issues/11331) and triaged
  **Not A Bug**. It never happens by itself: the catalog resolves to the highest compatible version,
  which on a 12.0 server is always the newest `1.x`.

We mitigate the second case with the **changelog text**, not with mechanism — the first line of every
release names the server line it is for. That is what the whole ecosystem does.

## Why one manifest and one repository URL

The official Jellyfin plugins cut over the same way on release day (2026-09-08 02:38 UTC): a single
`<TargetFramework>net10.0</TargetFramework>`, `targetAbi: "12.0.0.0"`, the next version number, and
the *same* manifest with the old low-`targetAbi` entries left in place. In
`repo.jellyfin.org/files/plugin/manifest.json` the webhook plugin's `22.0.0.0` (`targetAbi 12.0.0.0`)
sits directly above its `21.0.0.0` (`targetAbi 10.11.8.0`); across the whole file 34 entries at
`12.0.0.0` coexist with 38 at `10.11.0.0` and 54 at `10.8.0.0`. None of them keeps a maintenance
branch or a second manifest URL.

So: no `support/10.11` branch, no `manifest-unstable.json`, no version bands. The tag `v0.2.0.0`
preserves the 10.11 state if a hotfix there ever becomes necessary; until then the 10.11 line is
simply finished.

Version `1.0.0.0` rather than `0.3.0.0` is deliberate: the compatibility break is the largest change
the plugin has had, and the major bump makes that visible in the catalog and the changelog.

## Consequences to keep in mind

- **`scripts/check-versions.sh` had to be relaxed.** It compared the source tree against
  `manifest[0].versions[0]` by index, so the moment `targetAbi` moved to `12.0.0.0` while the newest
  published entry was still `0.2.0.0`/`10.11.11.0`, `main` went red — and would have stayed red until
  the release ran. It now treats the source tree as the *next* release and the manifest as the
  *published* ones: it looks the source version up by value and reports `pending release` when it is
  not published yet. It also gained a `framework` check across `build.yaml`, `meta.json` and the
  csproj, which is exactly the class of mistake this cut could have made.
- **The local test instance must be a second server.** The 10.11.11 portable instance cannot load
  this build. Run Jellyfin 12 from its own data directory and pass it to
  `scripts/install-local.ps1 -JellyfinDataDir`.
- **The `.NET 10 SDK` is now required to build.** The 10.11 line built on the .NET 9 SDK.

## Sources

- Jellyfin 12.0 release: <https://github.com/jellyfin/jellyfin/releases/tag/v12.0>
- `Jellyfin.Controller` 12.0.0 (net10.0): <https://www.nuget.org/packages/Jellyfin.Controller/12.0.0>
- targetAbi = minimum-version semantics: <https://forum.jellyfin.org/t-question-about-targetabi>
- Catalog does not filter out lower `targetAbi` on a newer server:
  <https://github.com/jellyfin/jellyfin/issues/11331>
