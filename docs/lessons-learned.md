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
- Residual gap (see `open-questions.md`): the periodic full pass ignores this and still re-applies the
  external rating over a manual edit. Proper fix (later): honor Jellyfin's `IsLocked`.

## 5. Idempotency is the real long-term loop backstop
Beyond the self-write window, the pipeline writing nothing when `|current - target| <= epsilon`
(`SkippedNoChange`) is what guarantees convergence: any late echo / re-import of our own value produces no
new write, hence no new event. Time-boxed guards handle the short term; idempotency handles the rest.

## 6. Observability: the useful listener logs were `Debug`
Gate decisions and per-item realtime outcomes log at `Debug`, but Jellyfin defaults to `Information`, so
the whole realtime path was invisible until a `config/logging.json` override raised
`Jellyfin.Plugin.ExternalRatings` to `Debug`. Verifying event-driven behavior needs the logs that prove
*why* something was (not) done — make sure they are actually visible during a smoke test.
