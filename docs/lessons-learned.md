# Lessons learned — where spec/theory diverged from the live host

Notes from bringing §15 step 9 (realtime listener) onto a real Jellyfin 10.11.11 instance. Each item is
something the offline design + unit tests could **not** have caught, only a live smoke test did.

## 1. Library filtering: `TopParentIds` was wrong, `AncestorIds` is right
The full-pass query filtered enabled libraries with `InternalItemsQuery.TopParentIds = EnabledLibraries`.
The config stores the ids from `getVirtualFolders().ItemId` — i.e. the **CollectionFolder** id. But an
item's `TopParentId` is the **underlying physical folder**, not that CollectionFolder, so the query
matched **zero items** (enrichment silently did nothing). The CollectionFolder is an *ancestor* of the
item (`AncestorIds` table), so the correct filter is `AncestorIds`.
- Why it slipped through: unit tests fake `ILibraryManager.GetItemList`, so the query object was never
  actually executed against a real library graph. **Any query-shape assumption needs a live check.**
- Fix: `RatingEnrichmentService.BuildLibraryQuery` uses `AncestorIds` (+ regression test).

## 2. Write reason & the NFO: Plan A "worked" but was the wrong default here
Spec §6.6 defaulted to writing `CommunityRating` with `ItemUpdateType.None` (Plan A) to avoid touching
media-folder NFOs. Live: the `None` write **did** persist to the DB and **did not** update the NFO — as
designed — but that left the DB (MAL 8.8) and the NFO saver's `<rating>` (TMDb 8.304) **divergent**, which
is confusing/undesirable when the library has the NFO saver enabled. The "hook" to keep them consistent is
simply the write reason: writing with `MetadataEdit` runs the host's per-library metadata savers exactly
like a manual UI edit (and does nothing where no saver is enabled). Switched to Plan B (`MetadataEdit`).
- Lesson: "does it persist / does it avoid an NFO write" was the wrong question; **"do the DB and the NFO
  stay consistent"** is what the user actually cares about. The theoretical Plan-A default optimized for
  the wrong thing on an NFO-saver library.

## 3. Our own `None` write DOES raise an `ItemUpdated` echo
The spec left open whether a `None`-reason write re-triggers the listener. Live: it **does** raise
`ItemUpdated` (mapped to reason `Other`). So the explicit `SelfWriteTracker` was genuinely necessary, not
merely belt-and-suspenders — and under Plan B (`MetadataEdit` echo) it is the *primary* loop guard, since
the reason filter no longer blocks the echo. Building it "robust to both plans" up front paid off.

## 4. Reacting to `MetadataEdit` overwrites the user
The theoretical gate treated every metadata-bearing reason as a trigger. Live, that meant a **manual**
rating edit (`MetadataEdit`) was instantly overwritten by the plugin — bad UX. The listener must react
only to **automatic** reasons (`MetadataDownload`/`MetadataImport`) + adds, never manual edits.
- Residual gap: the periodic full pass ignored this and still re-applied the
  external rating over a manual edit. **Fixed in §15 step 10**: locked items (`IsLocked`) are now skipped
  by the full pass and the listener alike, so locking is the durable, Jellyfin-native way to protect a
  hand-set rating. Live-confirmed: a locked item makes the full pass report `processed=0`.

## 5. Idempotency is the real long-term loop backstop
Beyond the self-write window, the pipeline writing nothing when `|current - target| <= epsilon`
(`SkippedNoChange`) is what guarantees convergence: any late echo / re-import of our own value produces no
new write, hence no new event. Time-boxed guards handle the short term; idempotency handles the rest.

## 6. Observability: the useful listener logs were `Debug`
Gate decisions and per-item realtime outcomes log at `Debug`, but Jellyfin defaults to `Information`, so
the whole realtime path was invisible until a `config/logging.json` override raised
`Jellyfin.Plugin.ExternalRatings` to `Debug`. Verifying event-driven behavior needs the logs that prove
*why* something was (not) done — make sure they are actually visible during a smoke test.

## 7. `targetAbi` is a *minimum*, not a compatibility guarantee (Jellyfin 12.0)
Jellyfin 12.0 jumps to net10.0 + `Jellyfin.Controller` 12.0, binary-incompatible with our net9.0 / 10.11
build. But `targetAbi` only gates on `serverVersion >= targetAbi`, so `10.11.11.0` *passes* on a 12.0
server — the catalog installs it, then it fails to load at runtime. Lesson: never bump `targetAbi` to
"support" a newer major; a new major needs its own build (framework + host-assembly refs) on a separate
(unstable) channel. Details + the deferral decision: `docs/jellyfin-12-compat.md`.

## 8. Other plugins act on our `ItemUpdated` — but the cost is a wake-up, not a re-analysis
Intro Skipper subscribes to `ItemAdded`/`ItemUpdated`, filters out only `ImageUpdate`, and enqueues
`Episode → SeasonId` / `Movie → movie.Id`. The obvious guess — that every rating write costs a segment
analysis — is **wrong**, and measuring it mattered: `BaseItemAnalyzerTask` returns early when
`HasUncachedAnalysisWork` finds no episode in state `NotAnalyzed`, so a warm item is never re-decoded.
Measured on the test instance (2026-09-06, 19 items, warm cache): two rating writes produced two
enqueues, one automatic pass 60s later, four ffmpeg capability probes (~124ms) and an enumeration of
*every* library (~3ms here; `GetMediaItems()` runs before the season filter) — and zero analyzer runs.
The 60s debounce also collapses N writes in one window into a single pass. So the defensible claim is
"a metadata write wakes the task", never "it re-analyzes".
We still cannot suppress the event: `ItemUpdated` fires regardless of `ItemUpdateType` (lesson 3),
`ImageUpdate` would be a semantic lie, and `IItemRepository.SaveItems` would skip the metadata savers
*and* the WebSocket refresh while reimplementing host internals that change in Jellyfin 12.
What *is* ours: never write when nothing changed (lesson 5), and never process an item the full pass
would not. The realtime path must apply the same `SupportedLevels()` filter as `BuildWorkItems` — an
A/B test against the unfixed build cleared an episode's rating outright (8 → null, outcome `Cleared`),
because without it `UnsupportedLevelBehavior=ClearField` treats every Episode as an unsupported level.

## 9. A cleared rating is refilled only by a *full* refresh — not by scans — and `0` is not portable
The merge is `if (replaceData || !target.CommunityRating.HasValue)`, so a remote fetch does refill an
empty field. The earlier claim derived from that line — "even on a normal, non-replace refresh … a slow
perpetual loop" — was code-reading, never measured, and **measuring it refutes the part that mattered.**
Measured on the test instance (2026-09-07, `EnableInternetProviders=true`, TheMovieDb+TheTVDB as Episode
fetchers, one episode that TMDb rates 6.833):

| trigger | cleared → refilled? |
| --- | --- |
| `metadataRefreshMode=Default`, `replaceAllMetadata=false` | **no** |
| `Scan Media Library` scheduled task | **no** |
| `metadataRefreshMode=FullRefresh`, `replaceAllMetadata=false` | **yes** (6.833 returns) |
| any of the above with `EnableInternetProviders=false` | no |

So the recurring paths — the library scan and ordinary refreshes — leave a cleared rating cleared. Only a
*full* metadata refresh refills it: a deliberate "refresh metadata / search for missing metadata", or the
library's `AutomaticRefreshIntervalDays > 0` (untested here — it cannot be exercised without waiting out
the interval, but it drives a full remote refresh, so assume it refills). The test instance has that
interval at `0` and internet providers off, so the loop cannot occur there at all.

This matters most at episode scale, where `ClearField` touches orders of magnitude more items than at
movie scale: the clear is a one-off, not a per-run treadmill.

The sentinel question is unchanged. Writing `0` would also stop a refill (a `0` *has* a value), but `0`
is not portable: jellyfin-web hides it only because JS treats `0` as falsy (`if (item.CommunityRating)`),
while Wholphin uses Kotlin null-checks — `communityRating?.let { ... }` runs for `0f` and renders "0.0 ★".
Jellyfin's NFO saver writes `<rating>0</rating>` for the same reason (`HasValue`). Only `null` reads as
"unknown" everywhere. Decision: keep `null`.
