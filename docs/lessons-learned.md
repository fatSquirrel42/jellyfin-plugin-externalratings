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

## 8. Other plugins act on our `ItemUpdated` — and we cannot suppress it
Intro Skipper subscribes to `ItemAdded`/`ItemUpdated`, filters out only `ImageUpdate`, and re-queues
`Episode → SeasonId` / `Movie → movie.Id` for media-segment analysis. Every rating write we make therefore
costs a segment analysis for that item, and a first full pass looks like a library-wide re-analysis. We
cannot avoid it: `ItemUpdated` fires regardless of `ItemUpdateType` (lesson 3), `ImageUpdate` would be a
semantic lie, and writing through `IItemRepository.SaveItems` to dodge the event skips the metadata savers
*and* the WebSocket refresh that tells clients to update — while reimplementing host internals that change
in Jellyfin 12. Our write is correct; the over-broad filter is Intro Skipper's. What *is* ours: never write
when nothing changed (lesson 5), and never process an item the full pass would not — the realtime path must
apply the same `SupportedLevels()` filter as `BuildWorkItems`, or `UnsupportedLevelBehavior=ClearField`
wipes Episode ratings and cascades a whole season re-analysis.

## 9. A cleared rating gets refilled, and `0` is not a portable "no rating"
`ClearField` writes `null`, but Jellyfin repopulates empty fields on the next remote metadata fetch —
`if (replaceData || !target.CommunityRating.HasValue)` — even on a normal, non-replace refresh. So
clear → refill → clear is a slow perpetual loop, paced by the library's "automatically refresh metadata
from the internet" setting. Writing `0` instead breaks it (a `0` *has* a value, so nothing refills it), but
`0` is not portable: jellyfin-web hides it only because JS treats `0` as falsy (`if (item.CommunityRating)`),
while Wholphin uses Kotlin null-checks — `communityRating?.let { ... }` runs for `0f` and renders "0.0 ★".
Jellyfin's NFO saver writes `<rating>0</rating>` for the same reason (`HasValue`). Only `null` reads as
"unknown" everywhere; every sentinel leaks into some client. Decision: keep `null` and accept the loop.
