#!/usr/bin/env bash
# Asserts the plugin version is identical across the four places it is hand-maintained, that the
# targetAbi agrees across build.yaml / meta.json / manifest.json, and that targetAbi matches the
# Jellyfin.Controller package the plugin is built against. Guards the release footgun where the
# changelog automation bumps build.yaml only (see RELEASING.md). Requires: grep -P, jq.
set -euo pipefail
cd "$(dirname "$0")/.."

fail() { echo "version-consistency: FAIL — $1" >&2; exit 1; }

props_version=$(grep -oP '(?<=<Version>)[^<]+' Directory.Build.props | head -1)
build_version=$(grep -oP '^version:\s*"\K[^"]+' build.yaml | head -1)
meta_version=$(jq -r '.version' src/Jellyfin.Plugin.ExternalRatings/meta.json)
# `jprm repo add` PREPENDS the new release, so the newest entry is versions[0], not versions[-1].
# With a single-entry manifest both indexes agreed, which hid this until the second release.
manifest_version=$(jq -r '.[0].versions[0].version' manifest.json)

echo "version   props=$props_version build=$build_version meta=$meta_version manifest=$manifest_version"
[ "$props_version" = "$build_version" ]    || fail "Directory.Build.props ($props_version) != build.yaml ($build_version)"
[ "$props_version" = "$meta_version" ]     || fail "Directory.Build.props ($props_version) != meta.json ($meta_version)"
[ "$props_version" = "$manifest_version" ] || fail "Directory.Build.props ($props_version) != manifest.json latest ($manifest_version)"

build_abi=$(grep -oP '^targetAbi:\s*"\K[^"]+' build.yaml | head -1)
meta_abi=$(jq -r '.targetAbi' src/Jellyfin.Plugin.ExternalRatings/meta.json)
manifest_abi=$(jq -r '.[0].versions[0].targetAbi' manifest.json)
pkg=$(grep -oP 'Jellyfin\.Controller"\s+Version="\K[^"]+' src/Jellyfin.Plugin.ExternalRatings/Jellyfin.Plugin.ExternalRatings.csproj | head -1)

echo "targetAbi build=$build_abi meta=$meta_abi manifest=$manifest_abi (controller=$pkg)"
[ "$build_abi" = "$meta_abi" ]     || fail "build.yaml targetAbi ($build_abi) != meta.json ($meta_abi)"
[ "$build_abi" = "$manifest_abi" ] || fail "build.yaml targetAbi ($build_abi) != manifest.json latest ($manifest_abi)"

# Not a hard failure: keeping targetAbi == the built-against controller is the safe default, but a
# maintainer may deliberately lower the floor (e.g. 10.11.0.0) to let older 10.11.x servers install,
# after confirming every host API used exists there. Surface the divergence rather than block it.
if [ "$build_abi" != "$pkg.0" ]; then
    echo "version-consistency: NOTE — targetAbi ($build_abi) differs from Jellyfin.Controller $pkg (=$pkg.0); intentional only if the lower floor was verified against the host APIs used."
fi

echo "version-consistency: OK ($props_version, abi $build_abi)"
