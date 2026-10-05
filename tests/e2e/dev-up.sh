#!/usr/bin/env bash
# A local playground: Jellyfin 12.1 with the server mod, two tentacles, a movie and
# a TV episode, placement Active, and one playback session from a logged-in user,
# so the dashboard page has nodes, jobs and attribution to show. Left running.
#   tests/e2e/dev-up.sh [version]      → http://localhost:18096 (user dev / dev)
#   tests/e2e/dev-up.sh down
# The server and mandalore get /dev/dri/renderD128 when this machine has one; scarif is a
# detect-only CPU tentacle (TENTACLE_MAX_JOBS=0).
# Needs scripts/build.sh mod first. Not for CI (publishes a port).
set -euo pipefail
p=tentacle-dev
srv=$p-server
net=$p
vols=($p-media $p-transcodes $p-temp $p-subs $p-attachments)
down() {
    docker rm -f "$srv" $p-mandalore $p-tatooine $p-scarif >/dev/null 2>&1 || true
    docker network rm "$net" >/dev/null 2>&1 || true
    docker volume rm "${vols[@]}" >/dev/null 2>&1 || true
}
if [[ "${1:-}" == down ]]; then down; exit 0; fi
version="${1:-$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$(dirname "$0")/../../Directory.Build.props")}"
base="linuxserver/jellyfin:version-12.1ubu2604"
plugin=3a65d525-990c-4f73-89e9-a0d1500a53d2
fail() { echo "FAIL: $*" >&2; exit 1; }
down

for role in server worker; do
    printf 'FROM %s\nCOPY --from=tentacle:%s-%s / /\nRUN mkdir -p /config/data/data /config/cache && chown -R 1000:1000 /config\n' "$base" "$role" "$version" | docker build -q -t "tentacle-dev:${role}" - >/dev/null
done
docker network create "$net" >/dev/null
for v in "${vols[@]}"; do docker volume create "$v" >/dev/null; done
for v in "${vols[@]}"; do docker run --rm -v "$v:/v" alpine chown 1000:1000 /v; done
mounts=(-v $p-media:/data/media -v $p-transcodes:/config/cache/transcodes -v $p-temp:/config/cache/temp
        -v $p-subs:/config/data/data/subtitles -v $p-attachments:/config/data/data/attachments)
env=(-e PUID=1000 -e PGID=1000 -e TZ=Etc/UTC -e TMPDIR=/config/cache/temp -e TENTACLE_TOKEN=dev-token)

srvgpu=(); [[ -c /dev/dri/renderD128 ]] && srvgpu=(--device /dev/dri/renderD128)
docker run -d --name "$srv" --hostname jellyfin --network "$net" "${srvgpu[@]}" -p 127.0.0.1:18096:8096 "${mounts[@]}" "${env[@]}" tentacle-dev:server >/dev/null
curl() { docker exec "$srv" curl "$@"; }
for _ in $(seq 120); do [[ "$(curl -s http://localhost:8096/health || true)" == Healthy ]] && break; sleep 1; done
echo "== media"
docker exec -u abc "$srv" sh -c 'set -e
    ff=/usr/lib/jellyfin-ffmpeg/ffmpeg
    mkdir -p "/data/media/movies/Night Harbor (2024)" "/data/media/tv/Signal Lost/Season 01"
    $ff -loglevel error -y -f lavfi -i testsrc2=s=1280x720:r=24:d=600 -f lavfi -i sine=f=440:d=600 \
        -c:v libx264 -preset ultrafast -crf 30 -g 48 -c:a aac "/data/media/movies/Night Harbor (2024)/Night Harbor (2024).mkv"
    $ff -loglevel error -y -f lavfi -i testsrc=s=1280x720:r=24:d=600 -f lavfi -i sine=f=220:d=600 \
        -c:v libx264 -preset ultrafast -crf 30 -g 48 -c:a aac "/data/media/tv/Signal Lost/Season 01/Signal Lost - S01E03 - The Quiet Relay.mkv"'

api=http://localhost:8096
auth='MediaBrowser Client="Jellyfin Web", Device="Firefox", DeviceId="dev-firefox", Version="10.11.0"'
curl -sf -X POST "$api/Startup/Configuration" -H 'Content-Type: application/json' -d '{"UICulture":"en-US","MetadataCountryCode":"US","PreferredMetadataLanguage":"en"}' >/dev/null
curl -sf "$api/Startup/User" >/dev/null
curl -sf -X POST "$api/Startup/User" -H 'Content-Type: application/json' -d '{"Name":"dev","Password":"dev"}' >/dev/null
curl -sf -X POST "$api/Startup/Complete" >/dev/null
at="$(curl -sf -X POST "$api/Users/AuthenticateByName" -H "Authorization: $auth" -H 'Content-Type: application/json' -d '{"Username":"dev","Pw":"dev"}' | jq -r .AccessToken)"
h="Authorization: ${auth}, Token=\"${at}\""
get() { curl -sf "$api/$1" -H "$h"; }
for lib in "Movies movies movies" "Shows tvshows tv"; do
    set -- $lib
    curl -sf -X POST "$api/Library/VirtualFolders?name=$1&collectionType=$2&refreshLibrary=true&paths=%2Fdata%2Fmedia%2F$3" -H "$h" \
        -H 'Content-Type: application/json' -d '{"LibraryOptions":{"EnableRealtimeMonitor":false,"EnableTrickplayImageExtraction":false,"EnableChapterImageExtraction":false}}' >/dev/null
done
for i in $(seq 90); do
    [[ "$(get 'Items?Recursive=true&IncludeItemTypes=Movie,Episode' | jq '.Items | length')" == 2 ]] && break
    (( i % 20 == 0 )) && curl -sf -X POST "$api/Library/Refresh" -H "$h" >/dev/null
    sleep 1
done
cfg="$(get "Plugins/$plugin/Configuration" | jq -c '.Placement="Active"')"
curl -sf -X POST "$api/Plugins/$plugin/Configuration" -H "$h" -H 'Content-Type: application/json' -d "$cfg" >/dev/null
fp="$(get Tentacle/Status | jq -r .TlsFingerprint)"

echo "== tentacles"
gpu=()
[[ -c /dev/dri/renderD128 ]] && gpu=(--device /dev/dri/renderD128)
for n in mandalore tatooine scarif; do
    extra=()
    [[ $n == mandalore ]] && extra=("${gpu[@]}")
    [[ $n == scarif ]] && extra=(-e TENTACLE_MAX_JOBS=0)
    docker run -d --name "$p-$n" --network "$net" --hostname "$n" "${mounts[@]}" "${env[@]}" "${extra[@]}" -e TENTACLE_BROKER_URL="wss://$srv:8097" \
        -e TENTACLE_ROOTS=/data/media:/config/cache/transcodes:/config/cache/temp:/config/data/data/subtitles:/config/data/data/attachments -e TENTACLE_BROKER_FINGERPRINT="$fp" -e TENTACLE_NODE_NAME="$n" tentacle-dev:worker >/dev/null
done
for _ in $(seq 90); do [[ "$(get Tentacle/Nodes | jq '[.[] | select(.State=="Ready" or .State=="Degraded")] | length')" == 2 ]] && break; sleep 1; done

echo "== playback: dev watches the episode (session device dev-firefox)"
episode="$(get 'Items?Recursive=true&IncludeItemTypes=Episode' | jq -r '.Items[0].Id')"
q="MediaSourceId=${episode}&VideoCodec=h264&AudioCodec=aac&VideoBitrate=2000000&AudioBitrate=128000&MaxWidth=1280&SegmentContainer=ts&PlaySessionId=dev-play-1&DeviceId=dev-firefox&ApiKey=${at}"
seg="$(curl -sf "$api/Videos/$episode/main.m3u8?$q" | grep -v '^#' | sed -n 1p)"
curl -sf -o /dev/null "$api/Videos/$episode/$seg"
sleep 3
get 'Tentacle/Jobs?limit=5' | jq -c '.[] | {Kind, Node, User, Client, Item, Outcome}'
echo "ready: http://localhost:18096 (dev / dev) → Dashboard → Tentacle;  tests/e2e/dev-up.sh down"
