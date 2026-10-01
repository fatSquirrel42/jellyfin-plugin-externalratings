#!/usr/bin/env bash
# Asserts the plugin version is identical across the three places it is hand-maintained
# (Directory.Build.props, build.yaml, meta.json), that targetAbi and framework agree between
# build.yaml, meta.json and the csproj, and that a version already published in manifest.json was
# published with the same targetAbi. Guards the release footgun where the changelog automation bumps
# build.yaml only (see RELEASING.md). Requires: grep -P, jq.
#
# The model: the source tree describes the NEXT release, manifest.json describes the PUBLISHED ones.
# The release workflow closes the gap inside one job (it runs `jprm repo add` before this script), so
# a released state has all four agreeing. Between a bump landing on main and that release going out
# the manifest legitimately lags -- that is the "pending release" branch below, and it is the only
# way a targetAbi change (10.11.11.0 -> 12.0.0.0) can be reviewed on a PR without turning main red.
set -euo pipefail
cd "$(dirname "$0")/.."

csproj=src/Jellyfin.Plugin.ExternalRatings/Jellyfin.Plugin.ExternalRatings.csproj
meta=src/Jellyfin.Plugin.ExternalRatings/meta.json

fail() { echo "version-consistency: FAIL — $1" >&2; exit 1; }

props_version=$(grep -oP '(?<=<Version>)[^<]+' Directory.Build.props | head -1)
props_assembly=$(grep -oP '(?<=<AssemblyVersion>)[^<]+' Directory.Build.props | head -1)
props_file=$(grep -oP '(?<=<FileVersion>)[^<]+' Directory.Build.props | head -1)
build_version=$(grep -oP '^version:\s*"\K[^"]+' build.yaml | head -1)
meta_version=$(jq -r '.version' "$meta")
# `jprm repo add` PREPENDS the new release, so the newest entry is versions[0], not versions[-1].
# With a single-entry manifest both indexes agreed, which hid this until the second release.
manifest_newest=$(jq -r '.[0].versions[0].version' manifest.json)

echo "version   props=$props_version build=$build_version meta=$meta_version manifest-newest=$manifest_newest"
[ "$props_version" = "$build_version" ]  || fail "Directory.Build.props ($props_version) != build.yaml ($build_version)"
[ "$props_version" = "$meta_version" ]   || fail "Directory.Build.props ($props_version) != meta.json ($meta_version)"
[ "$props_version" = "$props_assembly" ] || fail "Directory.Build.props Version ($props_version) != AssemblyVersion ($props_assembly)"
[ "$props_version" = "$props_file" ]     || fail "Directory.Build.props Version ($props_version) != FileVersion ($props_file)"

# The manifest may lag the source tree (bumped but not yet released), but it must never lead it: an
# entry newer than the source tree means a release was published from a commit that is not this one.
newest_of_both=$(printf '%s\n%s\n' "$props_version" "$manifest_newest" | sort -V | tail -1)
[ "$newest_of_both" = "$props_version" ] \
    || fail "manifest.json latest ($manifest_newest) is newer than the source tree ($props_version)"

build_abi=$(grep -oP '^targetAbi:\s*"\K[^"]+' build.yaml | head -1)
meta_abi=$(jq -r '.targetAbi' "$meta")
build_fw=$(grep -oP '^framework:\s*"\K[^"]+' build.yaml | head -1)
meta_fw=$(jq -r '.framework' "$meta")
csproj_fw=$(grep -oP '(?<=<TargetFramework>)[^<]+' "$csproj" | head -1)
pkg=$(grep -oP 'Jellyfin\.Controller"\s+Version="\K[^"]+' "$csproj" | head -1)

echo "targetAbi build=$build_abi meta=$meta_abi (controller=$pkg) | framework build=$build_fw meta=$meta_fw csproj=$csproj_fw"
[ "$build_abi" = "$meta_abi" ] || fail "build.yaml targetAbi ($build_abi) != meta.json ($meta_abi)"
[ "$build_fw" = "$meta_fw" ]   || fail "build.yaml framework ($build_fw) != meta.json ($meta_fw)"
# The zip jprm builds contains the DLL for whatever the csproj targets, while the server gates on the
# framework jprm writes into the packaged meta.json. A mismatch ships a DLL the host will not load.
[ "$build_fw" = "$csproj_fw" ] || fail "build.yaml framework ($build_fw) != csproj TargetFramework ($csproj_fw)"

# Look the source tree's version up by value rather than trusting index 0: while a bump is pending
# there is no entry for it at all, and that is a legitimate state (see the header).
published=$(jq -r --arg v "$props_version" '[.[0].versions[] | select(.version == $v)] | length' manifest.json)
if [ "$published" -eq 0 ]; then
    echo "version-consistency: NOTE — $props_version is not in manifest.json yet (pending release); skipping the published-targetAbi check."
elif [ "$published" -gt 1 ]; then
    fail "manifest.json has $published entries for version $props_version (must be unique)"
else
    manifest_abi=$(jq -r --arg v "$props_version" '.[0].versions[] | select(.version == $v) | .targetAbi' manifest.json)
    [ "$build_abi" = "$manifest_abi" ] \
        || fail "build.yaml targetAbi ($build_abi) != the published manifest entry for $props_version ($manifest_abi)"
fi

# Not a hard failure: keeping targetAbi == the built-against controller is the safe default, but a
# maintainer may deliberately lower the floor (e.g. 12.0.0.0 -> a later 12.0.x) to let older servers
# install, after confirming every host API used exists there. Surface the divergence rather than block it.
if [ "$build_abi" != "$pkg.0" ]; then
    echo "version-consistency: NOTE — targetAbi ($build_abi) differs from Jellyfin.Controller $pkg (=$pkg.0); intentional only if the lower floor was verified against the host APIs used."
fi

echo "version-consistency: OK ($props_version, abi $build_abi, $build_fw)"
