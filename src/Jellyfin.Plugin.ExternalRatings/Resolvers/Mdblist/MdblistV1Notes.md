# mdblist V1 verification results (§11)

Measured live against `https://api.mdblist.com` on 2026-07-18 with a Free-tier key
(`fatsquirrel42`). Golden files under `tests/.../Golden/` are the real (scrubbed) responses from
this run. These constants settle the §11 "V1" open questions.

## Endpoints
- **Single:** `GET /{provider}/{type}/{id}?apikey=KEY`.
- **Batch:** `POST /{provider}/{type}?apikey=KEY`, body `{"ids":["…"]}`, ≤ 200 ids. Response is a
  JSON **array** of full media objects. Match a response element back to a requested id via
  `ids[provider]` (a number for tmdb/tvdb/trakt, a string for imdb).
- Provider segments: `imdb, tmdb, trakt, tvdb, mal, mdblist`. Types: `movie, show, any`.

## Settled questions
- **Batch quota cost = 1 request per POST** (measured: `X-RateLimit-Remaining` dropped by exactly 1
  over one 2-id batch). Batch is cheap → it stays the primary path for scans; **Plan B not needed**.
- **Free-tier daily limit = 1000** (`/user` → `api_requests: 1000`, `rate_limit: 1000`).
- **No-match shape:** an unknown id returns **HTTP 404** with body `{"error":"Item not found"}` (NOT
  200-with-empty). A known item lacking a MAL score returns **200** with a `myanimelist` rating whose
  `value` is `null` (e.g. Jaws). The resolver maps **both** to `NoMatch`.
- **Header auth: none.** Only `apikey` in the query string (OAuth `Authorization: Bearer` is the only
  header alternative). The key therefore rides in the query and is masked in logs/exceptions (H9).
- **`tvdb` + `movie` is valid** and resolves the id **as a TVDB id** — `tvdb/movie/578` returned
  *Traffik* (whose `ids.tvdb == 578`), not the tmdb-578 movie. The provider segment governs id
  interpretation, so the resolver must always pair the segment with the matching provider's id.
- **MAL scale = 0–10** (Spirited Away `8.7`, Cowboy Bebop `8.7`), read from the native `value` field,
  never the normalized `score` (87). Other sources' `value` scales vary (letterboxd 0–5 or 0–10
  inconsistently, rogerebert 0–4, critic sources 0–100) — irrelevant since only `myanimelist` is read.

## Headers
- Media responses carry `X-RateLimit-Remaining` (and `X-RateLimit-Limit`/`-Reset`); 429 adds
  `Retry-After`. `rate_limit_reset` is a Unix timestamp at midnight UTC.
- The `/user` response did **not** include `X-RateLimit-*` headers in this capture — its counts live
  in the body (`api_requests`, `api_requests_count`, `rate_limit`, `rate_limit_remaining`,
  `rate_limit_reset`). So the §7.3 cold-start budget read must parse the `/user` body, not headers.
