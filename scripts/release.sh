#!/usr/bin/env bash
# Creates (or updates) the GitHub release for a version tag and attaches the
# build's files. Run by CI on vX.Y.Z tags, after the worker mod image is pushed.
# The release carries manifest.json, the Jellyfin plugin repository: every stable
# release so far, so Jellyfin can install this one and offer older ones.
# Users add https://github.com/<repo>/releases/latest/download/manifest.json.
#   scripts/release.sh X.Y.Z
# Env:
#   GH_TOKEN       a token allowed to write releases (the Actions job token works)
#   GH_REPO        owner/name (default crisidev/tentacle)
#   DIGESTS_FILE   optional: "role image@sha256:..." lines from the image push
#   PREVIOUS_MANIFEST  optional: the manifest.json to add this version to
#                  (default: the one in the latest release, if any)
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
# One plugin zip per Jellyfin line (scripts/build.sh plugin_builds).
lines=(12 10.11)
for line in "${lines[@]}"; do
    [[ -f "dist/tentacle_${version}_jellyfin-${line}.zip" ]] || { echo "release: build first (scripts/build.sh cli plugin)" >&2; exit 1; }
    cp "dist/tentacle_${version}_jellyfin-${line}.zip" "$out/tentacle-plugin_${version}_jellyfin-${line}.zip"
done
[[ -x dist/cli/tentacle && -x dist/cli-arm64/tentacle ]] || { echo "release: build first (scripts/build.sh cli plugin)" >&2; exit 1; }
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
    printf -- '- worker: `crisidev/tentacle:worker-%s`\n' "$version"
fi)"

# The plugin repository: this version on top of the previous stable ones.
previous="${PREVIOUS_MANIFEST:-}"
if [[ -z "$previous" ]]; then
    previous="$(mktemp)"
    if [[ "$dry" == 1 ]] || ! gh release download --pattern manifest.json --output "$previous" --clobber 2>/dev/null; then
        echo '[]' > "$previous"
    fi
fi
# This release's entries, one per Jellyfin line: version and targetAbi from the
# zip's meta.json (the 4th version part tells the lines apart).
entries="$(for line in "${lines[@]}"; do
    zip_name="tentacle-plugin_${version}_jellyfin-${line}.zip"
    unzip -p "$out/$zip_name" meta.json | jq -c --arg changelog "$changelog" \
        --arg url "https://github.com/${GH_REPO}/releases/download/${tag}/${zip_name}" \
        --arg checksum "$(md5sum "$out/$zip_name" | cut -d' ' -f1)" \
        '{version, changelog: $changelog, targetAbi, sourceUrl: $url, checksum: $checksum, timestamp}'
done | jq -s -c .)"
jq --arg guid "$(sed -n 's/^guid: "\(.*\)"/\1/p' build.yaml)" --argjson new "$entries" '
    ($new | map(.version)) as $versions
    | [{
        guid: $guid,
        name: "Tentacle",
        overview: "Remote transcoding: run Jellyfin'"'"'s ffmpeg jobs on other machines",
        description: "Sends the ffmpeg jobs Jellyfin starts (playback transcodes, extraction, trickplay, audio analysis) to worker machines, or runs them on the server, whichever is better. Workers run the crisidev/tentacle:worker LinuxServer mod. Replaces rffmpeg.",
        owner: "crisidev",
        category: "General",
        imageUrl: "https://raw.githubusercontent.com/crisidev/tentacle/main/img/dashboard.png",
        versions: ($new + ([.[] | select(.guid == $guid) | .versions[]] | map(select(.version as $v | $versions | index($v) | not))))
      }]' "$previous" > "$out/manifest.json"

notes="$(cat <<EOF
${changelog}

### Jellyfin plugin (the server)

Add the repository \`https://github.com/${GH_REPO}/releases/latest/download/manifest.json\`
in Dashboard → Plugins → Repositories, then install **Tentacle** from the catalog.

### LinuxServer mod (the tentacles; Docker Hub, amd64 and arm64)

Pin the digest in \`DOCKER_MODS\`: a tag can be overwritten, a digest cannot.

${images}

### Files

- \`tentacle-plugin_${version}_jellyfin-12.zip\`, \`tentacle-plugin_${version}_jellyfin-10.11.zip\`: the Jellyfin plugin for Jellyfin 12.1 and later and for 10.11, with the shim for amd64 and arm64 (what the repository installs)
- \`manifest.json\`: the Jellyfin plugin repository
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
