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
5. **Listener (c):** not applicable yet — the realtime listener is a later step; just note that the
   own-write carried reason `None`.

## Outcome → decision
- **Plan A holds** (value persists **and** no NFO write): keep `WriteUpdateReason = ItemUpdateType.None`.
  Record the result here.
- **Plan A fails** (no persistence, or an NFO is written): switch to **Plan B** — set
  `WriteUpdateReason = ItemUpdateType.MetadataEdit` in `JellyfinItemWriter.cs`, rebuild, and rely on
  the step-6 NFO warning on the config page (which already fires for NFO-saver libraries). Re-run
  Step B to confirm persistence under Plan B.

## Notes
- Free tier is **1000 requests/day**; each item = 1 request (batch, ~200× cheaper, is step 8). Even a
  dry-run pass calls the API per item, so test on a **small** library. The circuit breaker opens after
  repeated errors/429s and stops the run as a backstop.
- Restore is not built yet (step 10); to undo a test write, edit the rating back manually.
