#!/usr/bin/env bash
# Hardware acceptance on a machine with an Intel GPU (not in CI): Jellyfin set to
# QSV then VAAPI. A tentacle with the render node detects an Intel GPU (QSV, with
# measured encoders and decoders), passes the hardware self-test and gets the
# hardware transcodes; one without it is a CPU tentacle: no test, no hardware jobs,
# still Ready. A detect-only agent with two render nodes (the second one a copy at
# renderD129, which sysfs does not know) registers two tentacles, host/renderD128
# and host/renderD129, and never gets a job. Switching the encoding settings
# re-verifies every tentacle.
#   tests/e2e/hw-e2e.sh [version] [render node]   (needs scripts/build.sh mod first)
set -euo pipefail
version="${1:-$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$(dirname "$0")/../../Directory.Build.props")}"
dev="${2:-/dev/dri/renderD128}"
[[ -c "$dev" ]] || { echo "SKIP: no $dev"; exit 0; }
base="linuxserver/jellyfin:version-12.1ubu2604"
p=tentacle-hw
srv=$p-server
token=hw-$(date +%s)
vols=(media transcodes temp)
fail() { echo "FAIL: $*" >&2; docker logs "$srv" 2>&1 | grep -iE 'tentacle|ERR' | tail -20 >&2 || true; exit 1; }
cleanup() {
    docker rm -f "$srv" $p-gpu $p-cpu $p-probe >/dev/null 2>&1 || true
    docker network rm $p >/dev/null 2>&1 || true
    for v in "${vols[@]}"; do docker volume rm "$p-$v" >/dev/null 2>&1 || true; done
}
[[ "${KEEP:-0}" == 1 ]] || trap cleanup EXIT
cleanup

for role in server worker; do
    printf 'FROM %s\nCOPY --from=tentacle:%s-%s / /\nRUN mkdir -p /config/data/data /config/cache && chown -R 1000:1000 /config\n' "$base" "$role" "$version" |
        docker build -q -t "tentacle-dev:${role}" - >/dev/null
done
docker network create $p >/dev/null
for v in "${vols[@]}"; do docker volume create "$p-$v" >/dev/null; done
docker run --rm -v $p-media:/a -v $p-transcodes:/b -v $p-temp:/c alpine chown 1000:1000 /a /b /c
mounts=(-v $p-media:/data/media -v $p-transcodes:/config/cache/transcodes -v $p-temp:/config/cache/temp)
env=(-e PUID=1000 -e PGID=1000 -e TZ=Etc/UTC -e TMPDIR=/config/cache/temp -e TENTACLE_TOKEN="$token")

echo "== server with $dev"
docker run -d --name "$srv" --network $p --device "$dev" "${mounts[@]}" "${env[@]}" tentacle-dev:server >/dev/null
curl() { docker exec "$srv" curl "$@"; }
for _ in $(seq 120); do [[ "$(curl -s http://localhost:8096/health || true)" == Healthy ]] && break; sleep 1; done
[[ "$(curl -s http://localhost:8096/health)" == Healthy ]] || fail "server not healthy"
docker exec -u abc "$srv" sh -c 'mkdir -p "/data/media/movies/Hw Test (2026)" && /usr/lib/jellyfin-ffmpeg/ffmpeg -loglevel error -y \
    -f lavfi -i testsrc2=s=1920x1080:r=24:d=180 -f lavfi -i sine=d=180 -c:v libx264 -preset ultrafast -crf 28 -g 48 -c:a aac \
    "/data/media/movies/Hw Test (2026)/Hw Test (2026).mkv"'

api=http://localhost:8096
auth='MediaBrowser Client="tentacle-hw", Device="hw", DeviceId="tentacle-hw", Version="1"'
curl -sf -X POST "$api/Startup/Configuration" -H 'Content-Type: application/json' -d '{"UICulture":"en-US","MetadataCountryCode":"US","PreferredMetadataLanguage":"en"}' >/dev/null
curl -sf "$api/Startup/User" >/dev/null
curl -sf -X POST "$api/Startup/User" -H 'Content-Type: application/json' -d '{"Name":"hw","Password":"hw"}' >/dev/null
curl -sf -X POST "$api/Startup/Complete" >/dev/null
at="$(curl -sf -X POST "$api/Users/AuthenticateByName" -H "Authorization: $auth" -H 'Content-Type: application/json' -d '{"Username":"hw","Pw":"hw"}' | jq -r .AccessToken)"
h="Authorization: ${auth}, Token=\"${at}\""
get() { curl -sf "$api/$1" -H "$h"; }
post() { curl -sf -X POST "$api/$1" -H "$h" -H 'Content-Type: application/json' -d "${2:-{\}}"; }
post "Library/VirtualFolders?name=Movies&collectionType=movies&refreshLibrary=true&paths=%2Fdata%2Fmedia%2Fmovies" \
    '{"LibraryOptions":{"EnableRealtimeMonitor":false,"EnableTrickplayImageExtraction":false,"EnableChapterImageExtraction":false}}' >/dev/null
item=""
for i in $(seq 90); do
    item="$(get 'Items?Recursive=true&IncludeItemTypes=Movie' | jq -r '.Items[0].Id // empty')"
    [[ -n "$item" ]] && break
    (( i % 20 == 0 )) && post Library/Refresh >/dev/null
    sleep 1
done
[[ -n "$item" ]] || fail "library scan found no movie"
plugin=3a65d525-990c-4f73-89e9-a0d1500a53d2
post "Plugins/$plugin/Configuration" "$(get "Plugins/$plugin/Configuration" | jq -c '.Placement="Active"')" >/dev/null

encoding() {
    local cfg
    cfg="$(get System/Configuration/encoding | jq -c --arg t "$1" --arg d "$dev" \
        '.HardwareAccelerationType=$t | .VaapiDevice=$d | .QsvDevice=$d | .EnableHardwareEncoding=true | .HardwareDecodingCodecs=["h264","hevc"] | .EnableTonemapping=false | .EnableVppTonemapping=false')"
    post System/Configuration/encoding "$cfg" >/dev/null
}

echo "== encoding: qsv; tentacles gpu (with $dev) and cpu (without)"
fp="$(get Tentacle/Status | jq -r '.TlsFingerprint')"
encoding qsv
docker run -d --name $p-gpu --network $p --device "$dev" "${mounts[@]}" "${env[@]}" -e TENTACLE_BROKER_URL="wss://$srv:8097" -e TENTACLE_ROOTS=/data/media:/config/cache/transcodes:/config/cache/temp:/config/data/data/subtitles:/config/data/data/attachments -e TENTACLE_BROKER_FINGERPRINT="$fp" -e TENTACLE_NODE_NAME=gpu tentacle-dev:worker >/dev/null
docker run -d --name $p-cpu --network $p "${mounts[@]}" "${env[@]}" -e TENTACLE_BROKER_URL="wss://$srv:8097" -e TENTACLE_ROOTS=/data/media:/config/cache/transcodes:/config/cache/temp:/config/data/data/subtitles:/config/data/data/attachments -e TENTACLE_BROKER_FINGERPRINT="$fp" -e TENTACLE_NODE_NAME=cpu tentacle-dev:worker >/dev/null
docker run -d --name $p-probe --network $p --device "$dev" --device "$dev:/dev/dri/renderD129" "${mounts[@]}" "${env[@]}" -e TENTACLE_BROKER_URL="wss://$srv:8097" \
    -e TENTACLE_ROOTS=/data/media:/config/cache/transcodes:/config/cache/temp:/config/data/data/subtitles:/config/data/data/attachments -e TENTACLE_BROKER_FINGERPRINT="$fp" -e TENTACLE_NODE_NAME=probe -e TENTACLE_MAX_JOBS=0 tentacle-dev:worker >/dev/null

check() { get Tentacle/Nodes | jq -c --arg n "$1" --arg c "$2" '.[] | select(.Name==$n) | .Checks[] | select(.Name==$c) | {Ok,Detail}'; }
wait_hw() {
    for _ in $(seq 90); do
        [[ "$(check gpu "hardware:$1" | jq -r .Ok)" == true && "$(check cpu "hardware:$1" | jq -r .Ok)" == false ]] && return 0
        sleep 1
    done
    get Tentacle/Nodes | jq -c '.[] | {Name,State,HardwareOk,Devices,Checks:[.Checks[] | select(.Name|startswith("hardware"))]}'
    fail "hardware:$1 checks not as expected"
}
transcode() {
    local session=$1 main seg size
    main="$(curl -sf "$api/Videos/$item/main.m3u8?MediaSourceId=$item&VideoCodec=h264&AudioCodec=aac&VideoBitrate=4000000&MaxWidth=1280&SegmentContainer=ts&PlaySessionId=$session&DeviceId=hw-$session&ApiKey=$at")"
    seg="$(grep -v '^#' <<<"$main" | head -1)"
    size="$(curl -sf -m 60 -o /dev/null -w '%{size_download}' "$api/Videos/$item/$seg")"
    (( size > 10000 )) || fail "segment too small ($size)"
    get 'Tentacle/Jobs?limit=5' | jq -c '[.[] | select(.Kind=="Transcode")][0]'
}

wait_hw qsv
for _ in $(seq 60); do [[ "$(get Tentacle/Nodes | jq '[.[] | select(.State=="DetectOnly")] | length')" == 2 ]] && break; sleep 1; done
nodes="$(get Tentacle/Nodes)"
jq -c '.[] | {Name,State,HardwareOk,Arch,Gpu:(.Gpu | if . then {Vendor,Api,PciId,Driver,Model,Capabilities,Detail} else null end)}' <<<"$nodes"
node() { jq -c --arg n "$1" '.[] | select(.Name==$n)' <<<"$nodes"; }
[[ "$(node gpu | jq -r '.Gpu.Vendor + "/" + .Gpu.Api')" == intel/qsv ]] || fail "gpu tentacle: no Intel QSV GPU detected"
node gpu | jq -e '.Gpu.Capabilities | index("encode:h264") and index("decode:h264")' >/dev/null || fail "gpu tentacle: H.264 encode/decode not measured"
# This test shares only some roots (Degraded for that): no other required check may fail.
failed() { node "$1" | jq '[.Checks[] | select(.Required and (.Ok | not) and (.Name | startswith("root:") | not))] | length'; }
[[ "$(failed gpu)" == 0 && "$(node gpu | jq -r .HardwareOk)" == true ]] || fail "gpu tentacle: a required check failed"
[[ "$(node cpu | jq -r '.Gpu')" == null && "$(failed cpu)" == 0 ]] || fail "cpu tentacle should be a CPU tentacle with no failed required check"
node cpu | jq -e '.Checks[] | select(.Name=="hardware:qsv") | (.Ok == false and .Required == false and (.Detail | test("no GPU")))' >/dev/null || fail "cpu tentacle: hardware check should be skipped, optional"
[[ "$(node probe/renderD128 | jq -r '.State + " " + .Gpu.Vendor + " " + (.MaxJobs|tostring)')" == "DetectOnly intel 0" ]] || fail "probe/renderD128 not a detect-only Intel tentacle"
[[ "$(node probe/renderD129 | jq -r '.State + " " + .Gpu.Api')" == "DetectOnly none" ]] || fail "probe/renderD129 not a detect-only tentacle without API"
echo "gpu: $(check gpu hardware:qsv)"
echo "cpu: $(check cpu hardware:qsv | cut -c1-200)"
j="$(transcode q1)"; echo "$j" | jq -c '{Node,Reason,Outcome}'
[[ "$(jq -r .Node <<<"$j")" == gpu ]] || fail "QSV transcode not on the gpu tentacle"
get 'Tentacle/Jobs?limit=50' | jq -e 'all(.[]; (.Node | startswith("probe")) | not)' >/dev/null || fail "a detect-only tentacle got a job"
jq -r .Command <<<"$j" | grep -q 'h264_qsv' || fail "transcode does not use h264_qsv"
docker exec $p-gpu sh -c 'pgrep -af "jellyfin-ffmpeg/ffmpeg.*h264_qsv"' >/dev/null || fail "no h264_qsv ffmpeg on the gpu tentacle"
curl -sf -X DELETE "$api/Videos/ActiveEncodings?deviceId=hw-q1&playSessionId=q1" -H "$h"

echo "== encoding: vaapi (settings change re-verifies)"
encoding vaapi
wait_hw vaapi
echo "gpu: $(check gpu hardware:vaapi)"
j="$(transcode v1)"; echo "$j" | jq -c '{Node,Reason,Outcome}'
[[ "$(jq -r .Node <<<"$j")" == gpu ]] || fail "VAAPI transcode not on the gpu tentacle"
jq -r .Command <<<"$j" | grep -q 'h264_vaapi' || fail "transcode does not use h264_vaapi"
curl -sf -X DELETE "$api/Videos/ActiveEncodings?deviceId=hw-v1&playSessionId=v1" -H "$h"

echo "PASS: hardware e2e"
