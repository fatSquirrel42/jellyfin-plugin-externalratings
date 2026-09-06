# Releasing

This plugin is distributed as a **GitHub Release** plus a repo-hosted **`manifest.json`** that users add
as a Jellyfin plugin repository. Releases are cut manually with [`jprm`](https://pypi.org/project/jprm/)
(the Jellyfin Plugin Repository Manager). One artifact targets Jellyfin **10.11.x** (net9.0); Jellyfin
12.0 needs a separate build — see [docs/jellyfin-12-compat.md](docs/jellyfin-12-compat.md).

## Prerequisites
- .NET 9 SDK.
- `jprm` installed: `pip install --user jprm`.

## Steps

### Automated (preferred)

1. Update `changelog:` in `build.yaml` on `main` through a normal PR. A changelog-only change moves no
   version, so the *Version Consistency* check stays green.
2. **Actions -> 🚀 Release -> Run workflow**, entering the new four-part version (e.g. `0.1.2.0`).

The workflow bumps `Directory.Build.props`, `build.yaml` and `meta.json`, runs the offline tests, builds
the package with `jprm`, writes the new `manifest.json` entry, pushes the bump to `main`, publishes the
GitHub Release with the asset, and finally re-downloads that asset to confirm its md5 equals the manifest
checksum.

Why the workflow builds *and* publishes instead of uploading a locally built zip: the artifact's md5 goes
into `manifest.json`, and .NET builds are not bit-reproducible (jprm also stamps a timestamp into the
`meta.json` inside the zip). Any split where one side builds and the other publishes leaves `checksum`
disagreeing with the published asset.

It also pushes the bump *before* publishing the release, because `manifest.json` advertises the asset URL
and an entry whose asset does not exist yet breaks `Catalog -> Install` for everyone. Doing both inside
one job shrinks that window to seconds.

### Manual fallback

Needs `jprm` (`pip install --user jprm`) and the GitHub CLI (`gh auth login`).

1. **Bump the version** — it lives in **four** places and they must all match (the fourth,
   `manifest.json`, is written for you in step 5). All four are bumped **manually** — the `changelog.yaml`
   automation only drafts release notes and touches no version file. CI enforces agreement via
   `scripts/check-versions.sh` (the *Version Consistency* workflow), so a mismatch fails the build:
   - `Directory.Build.props` — `Version` / `AssemblyVersion` / `FileVersion`
   - `build.yaml` — `version:` (and update `changelog:`)
   - `src/Jellyfin.Plugin.ExternalRatings/meta.json` — `version` (the dev-install manifest)
   - `manifest.json` — the new `versions[]` entry (added in step 5)

   Keep everything ASCII in `build.yaml` `description`/`changelog` — non-ASCII (e.g. an em dash) can be
   mangled into mojibake in the generated `meta.json`/catalog listing.

2. **Verify**: `dotnet test --filter "Category!=Live"` (all green) and `dotnet build -c Release` (clean).

3. **Build the package** (from the repo root; reads `build.yaml`):
   ```
   jprm plugin build . --output ./artifacts --version <X.Y.Z.0>
   ```
   Produces `artifacts/external-ratings_<X.Y.Z.0>.zip` (contains the DLL + generated `meta.json`), a
   `.md5sum`, and a sidecar `.meta.json`.

4. **Create the GitHub Release** tagged `v<X.Y.Z.0>` and upload the `.zip` as a release asset. The asset
   download URL will be:
   ```
   https://github.com/fatSquirrel42/jellyfin-plugin-externalratings/releases/download/v<X.Y.Z.0>/external-ratings_<X.Y.Z.0>.zip
   ```

5. **Update `manifest.json`** (append the new version, `sourceUrl` = the asset URL above, `checksum` = the
   md5):
   ```
   jprm repo add \
     --plugin-url "<asset-download-url>" \
     ./manifest.json \
     ./artifacts/external-ratings_<X.Y.Z.0>.zip
   ```
   Commit the updated `manifest.json` to `main`.

6. **Sanity-check** the release: in a test server, add the repository URL, confirm the new version shows
   in the catalog, install it, restart, and check the log for
   `Loaded plugin: "External Ratings" "<X.Y.Z.0>"` with no load errors.

## Repository URL for users
```
https://raw.githubusercontent.com/fatSquirrel42/jellyfin-plugin-externalratings/main/manifest.json
```

## Notes
- `artifacts/` is a scratch output dir — do not commit it (add to `.gitignore` if it lands in the repo).
- The `.github/workflows/changelog.yaml` workflow runs `release-drafter` to maintain a **release-notes
  draft only** (driven by `.github/release-drafter.yml`); it does not bump any version file, and does not
  build or publish the package. Steps 1–5 above are the actual bump and publish.
- **The GitHub Release is not optional.** `manifest.json` on `main` advertises a `sourceUrl` pointing at
  the release asset; if the release/asset is missing, `Catalog → Install` fails with a download error for
  everyone. After committing `manifest.json`, verify `sourceUrl` returns HTTP 200 and its `md5sum` equals
  the manifest `checksum`.
- `targetAbi` (currently `10.11.11.0`) is the **minimum** Jellyfin version allowed to install the plugin.
  It is kept equal to the `Jellyfin.Controller` package the plugin compiles against — the safe default,
  and `scripts/check-versions.sh` prints a note if they diverge. Lowering it (e.g. `10.11.0.0`) would let
  older 10.11.x servers install too, but only after confirming every host API the plugin uses exists on
  that floor.
