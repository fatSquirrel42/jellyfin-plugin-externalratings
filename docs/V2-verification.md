# V2 verification — write-reason (`ItemUpdateType.None`, Plan A)

**Question (spec §11 V2):** when the plugin writes `CommunityRating` with **`ItemUpdateType.None`**,
does the change (a) persist correctly, and (b) avoid producing/updating NFO files in media folders,
and (c) not re-trigger the realtime listener? This can only be answered on a **live Jellyfin 10.11
instance** — it is a manual verification. Steps 3–7 are built so the outcome flips a single constant.

Where the write reason lives: `src/Jellyfin.Plugin.ExternalRatings/Infrastructure/JellyfinItemWriter.cs`
→ `private const ItemUpdateType WriteUpdateReason = ItemUpdateType.None;` (the one place to change for
Plan B).

## Prerequisites
- A test Jellyfin **10.11.x** server you can restart and whose server log you can read.
- One small library (a few Movies and/or a Series) with at least one **anime** item that has a Tmdb
  or Imdb id and a real MyAnimeList score (e.g. *Spirited Away*, *Cowboy Bebop*).
- The plugin installed (build the plugin project, drop the DLL into the server's
  `plugins/ExternalRatings/` folder, restart). A free **mdblist API key**.

## Configure
1. Dashboard → Plugins → **External Ratings**: paste the mdblist API key, enable the small library,
   keep **Dry run = on**, keep levels Movie + Series. Save.
2. Note whether the config page shows the **NFO warning** (it will if that library has the NFO saver
   active — relevant to step (b) below).

## Step A — Dry-run E2E (no writes)
1. Dashboard → Scheduled Tasks → run **"Enrich External Ratings"**.
2. In the server log, confirm exactly one summary line per run at Info level:
   `External Ratings run <id> done: processed=… updated=… … dryRunSkipped=… errors=…`.
   - Expect `updated=0` and `dryRunSkipped ≥ 1` (writes suppressed by dry run), `errors=0` for items
     with a valid key/ids. This proves enumeration + resolve + the pipeline run.
3. Dashboard → the plugin's status endpoint (`GET /Plugins/ExternalRatings/Status`, admin-only)
   should now show the `lastRun` counters and `circuitOpen=false`.

## Step B — Real write on one item (the actual V2 check)
1. Narrow the enabled library to just the folder containing **one** known anime item (to limit
   blast radius), then set **Dry run = off**. Save.
2. Run the task again. In the log, expect `updated=1` (or more).
3. **Persistence (a):** open that item in the UI (or query it) → its **Community Rating** should now
   equal the MyAnimeList score (0–10). Restart the server and re-check — the value must survive
   (confirms `None` persisted to the DB).
4. **NFO (b):** inspect the item's media folder. With Plan A there should be **no new/modified
   `.nfo` file** (compare file timestamps before/after). This is the core V2 question.
5. **Listener (c):** the realtime listener now exists (§15 step 9). Watch the server log around the
   write: even if our `None` write raises an `ItemUpdated`, there must be **exactly one** enrichment and
   **no repeating chain**. The listener is robust by design — it ignores non-metadata reasons (Plan A)
   **and** ignores ids the plugin wrote within the self-write window (Plan B) — so a loop should not
   occur regardless of the write-reason outcome.

## Step C — Realtime listener (§15 step 9, V3/V4)
1. Keep **Dry run = off**, **Enable realtime listener = on**, the small library enabled.
2. Manually change the item's Community Rating in the UI (or clear it), then trigger a **metadata
   refresh** on just that item (Refresh metadata → "Search for missing metadata" / replace).
3. Expect the listener to re-enrich **only that item**: one `realtime enrichment … : Updated` line,
   debounced (a burst of refresh events collapses into a single run). No scan is triggered.
4. Negative checks: with the listener **off**, the refresh must **not** enrich; during a full library
   scan, the listener defers to the post-scan pass (no duplicate work).

## Outcome → decision
- **Plan A holds** (value persists **and** no NFO write): keep `WriteUpdateReason = ItemUpdateType.None`.
  Record the result here.
- **Plan A fails** (no persistence, or an NFO is written): switch to **Plan B** — set
  `WriteUpdateReason = ItemUpdateType.MetadataEdit` in `JellyfinItemWriter.cs`, rebuild, and rely on
  the step-6 NFO warning on the config page (which already fires for NFO-saver libraries). Re-run
  Step B to confirm persistence under Plan B.

## Result (2026-07-18) — Plan B chosen
Verified live on the dev instance (Jellyfin 10.11.11) with one anime movie (*Violet Evergarden: Der Film*,
tmdb 533514, MAL 8.8, TMDb 8.304):

- **`None` (Plan A) worked but left the NFO stale.** Run `b9bf9690` (14:11): DB `CommunityRating`
  8.304 → **8.8** (persisted, `DateLastSaved` set), **no NFO write** (`movie.nfo` mtime unchanged), and
  the write echo (`ItemUpdated`, reason mapped to `Other`) was dropped via `SkipRecentSelfWrite` — no
  loop. So Plan A's premise held, but DB and the (NFO-saver-enabled) `<rating>` diverged.
- **Decision: use Plan B — `WriteUpdateReason = ItemUpdateType.MetadataEdit`** (commit `f4721df`), so the
  write flows through the host metadata savers like a manual edit and the NFO stays consistent. Run at
  14:40 confirmed: `movie.nfo` mtime bumped, `<rating>` = **8.8**, DB = **8.8**; the `MetadataEdit` echo
  → `SkipRecentSelfWrite` (no loop); follow-up task `skippedNoChange` (idempotent). The NFO saver is
  per-library opt-in, so libraries without it get no NFO (nothing to diverge).
- **Loop safety under Plan B** rests on the `SelfWriteTracker` (the reason filter no longer blocks the
  `MetadataEdit` echo), with `SkippedNoChange` idempotency as a backstop.
- **Realtime listener** additionally restricted to *automatic* reasons (`MetadataDownload`/
  `MetadataImport`) + adds (commit `151081d`): a manual `MetadataEdit` no longer triggers enrichment, so
  the plugin does not instantly overwrite a hand-set rating. Verified 15:13 (`SkipIneligibleReason`).
  Follow-up now resolved in step 10 (see below): locked items are skipped by the full pass too.
- **Automatic positive path + debounce verified live (15:22):** two provider refreshes ("scan for new
  and updated" + "replace all") on the item coalesced into **one** realtime enrichment → `Updated` back
  to 8.8, `movie.nfo` rewritten to 8.8; the write echo was dropped (`SkipRecentSelfWrite`). Across the
  whole session: 3 realtime enrichments / 5 self-write skips, each tied to a distinct user action — no
  repeating chain, no errors.

## Step 10 verification (2026-07-18) — IsLocked skip + Restore + Clear cache

Verified live on the dev instance (Jellyfin 10.11.11, Release build loaded 16:02) with the same anime
movie (*Violet Evergarden: Der Film*). Baseline before the tests: DB `CommunityRating` **8.8** (plugin's
MAL write), `backup.json` = original **8.304**, `cache.json` = MAL 8.8, item unlocked.

- **IsLocked — full pass skips locked items.** Locked the item in the UI, then ran the enrichment task.
  Log (16:10): the lock's `MetadataEdit` echo hit the listener as `SkipLocked` (new gate branch), and the
  run reported `starting for 0 item(s) … processed=0` — i.e. the live `AncestorIds` query + `BuildWorkItems`
  filtered the locked item out of the work set. DB rating stayed **8.8**. This is the live-only proof that
  offline tests cannot give (query behavior, per lessons-learned #1).
- **Clear cache — cache only.** "Clear rating cache" button → `cache.json` deleted, `backup.json`
  untouched (original 8.304 preserved), so restore remained possible.
- **Restore all — ignores the lock (deliberate admin reset).** "Restore all original ratings" button
  (item still locked) → log (16:11) `RestoreRunner … starting for 1 backed-up item(s) … restore done: 1
  item(s) restored`. DB `CommunityRating` **8.8 → 8.304**, `backup.json` emptied, and `movie.nfo`
  `<rating>` rewritten to **8.304** (Plan B `MetadataEdit` flows through the NFO saver). The restore's own
  write echo was dropped (`SkipLocked` here; the `SelfWriteTracker` is the guard when unlocked) — no
  re-enrich loop.
- **Mid-run 409** is impractical to trigger with a one-item library (a run completes instantly); the
  mutual-exclusion is covered by `ExclusiveOperationGateTests`.

## Notes
- Free tier is **1000 requests/day**; each item = 1 request (batch, ~200× cheaper, is step 8). Even a
  dry-run pass calls the API per item, so test on a **small** library. The circuit breaker opens after
  repeated errors/429s and stops the run as a backstop.
- Restore is available from step 10: the config page "Restore all original ratings" button resets every
  plugin-changed rating to its `backup.json` original and clears the backups.
