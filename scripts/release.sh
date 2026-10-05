#!/usr/bin/env bash
# Creates (or updates) the GitHub release for a version tag and attaches the
# build's files. Run by CI on vX.Y.Z tags, after the mod images are pushed.
#   scripts/release.sh X.Y.Z
# Env:
#   GH_TOKEN       a token allowed to write releases (the Actions job token works)
#   GH_REPO        owner/name (default crisidev/tentacle)
#   DIGESTS_FILE   optional: "role image@sha256:..." lines from the image push
#   DRY_RUN=1      print what would be sent, touch nothing
# Re-running for the same tag replaces assets of the same name: safe to retry.
set -euo pipefail
version="${1:?usage: $0 X.Y.Z}"
tag="v${version}"
root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"
dry="${DRY_RUN:-0}"
export GH_REPO="${GH_REPO:-crisidev/tentacle}"
out="dist/release"

# The files: names carry the version so downloads do not collide.
rm -rf "$out"
mkdir -p "$out"
[[ -f "dist/tentacle_${version}.zip" && -x dist/cli/tentacle && -x dist/cli-arm64/tentacle ]] || { echo "release: build first (scripts/build.sh plugin cli)" >&2; exit 1; }
cp "dist/tentacle_${version}.zip" "$out/tentacle-plugin_${version}.zip"
cp dist/cli/tentacle "$out/tentacle_${version}_linux-amd64"
cp dist/cli-arm64/tentacle "$out/tentacle_${version}_linux-arm64"
cp deploy/prometheus/tentacle.rules.yaml "$out/tentacle_${version}.rules.yaml"
cp deploy/grafana/tentacle.json "$out/tentacle_${version}.grafana.json"
(cd "$out" && sha256sum -- * > SHA256SUMS)

# Release notes: this version's section of CHANGELOG.md and the images.
changelog="$(awk -v v="## ${version}" '
    $0 == v { inside = 1; next }
    inside && /^## / { exit }
    inside { print }' CHANGELOG.md | sed -e '/./,$!d')"
[[ -n "$changelog" ]] || changelog="No entry for ${version} in CHANGELOG.md."
images="$(if [[ -s "${DIGESTS_FILE:-}" ]]; then
    while read -r role ref; do printf -- '- %s: `%s`\n' "$role" "$ref"; done < "$DIGESTS_FILE"
else
    printf -- '- server: `crisidev/tentacle:server-%s`\n- worker: `crisidev/tentacle:worker-%s`\n' "$version" "$version"
fi)"
notes="$(cat <<EOF
${changelog}

### LinuxServer mods (Docker Hub, amd64 and arm64)

Pin the digest in \`DOCKER_MODS\`: a tag can be overwritten, a digest cannot.

${images}

### Files

- \`tentacle-plugin_${version}.zip\`: the Jellyfin plugin, for installs without the server mod (the mod already contains it)
- \`tentacle_${version}_linux-amd64\`, \`tentacle_${version}_linux-arm64\`: the shim and agent binary (NativeAOT, glibc)
- \`tentacle_${version}.rules.yaml\`, \`tentacle_${version}.grafana.json\`: Prometheus alerts and Grafana board
- \`SHA256SUMS\`
EOF
)"
pre=()
[[ "$version" == *-* ]] && pre=(--prerelease)

if [[ "$dry" == 1 ]]; then
    echo "DRY RUN: would create or update ${GH_REPO} release ${tag}${pre:+ (prerelease)}:"
    echo "$notes"
    echo "assets:"; ls -l "$out"
    exit 0
fi

# Create, or update the one a previous run made for this tag.
if gh release view "$tag" >/dev/null 2>&1; then
    gh release edit "$tag" --title "Tentacle ${version}" --notes "$notes" "${pre[@]}" >/dev/null
    echo "release: updated ${tag}"
else
    gh release create "$tag" --verify-tag --title "Tentacle ${version}" --notes "$notes" "${pre[@]}" >/dev/null
    echo "release: created ${tag}"
fi
gh release upload "$tag" "$out"/* --clobber
echo "release: $(gh release view "$tag" --json url -q .url)"
