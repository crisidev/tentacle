#!/usr/bin/env bash
# M1 acceptance: real Jellyfin 12.1 (server mod) + one tentacle (worker mod) sharing
# the media, transcode and temp volumes at the same paths, software encoding.
#   - an HLS transcode runs on the tentacle, with progress relayed to Jellyfin's log
#   - seeking replaces the remote ffmpeg; stopping playback ends it
#   - killing the tentacle mid-stream: the job is lost, playback resumes locally
#   - M3: the tentacle_* metrics in Jellyfin's /metrics match what happened
#   tests/e2e/m1-e2e.sh [version]   (needs scripts/build.sh mod first)
set -euo pipefail
version="${1:-$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$(dirname "$0")/../../Directory.Build.props")}"
base="linuxserver/jellyfin:version-12.1ubu2604"
net=tentacle-e2e
srv=tentacle-e2e-server
wrk=tentacle-e2e-worker
token=e2e-$(date +%s)
vols=(tentacle-e2e-media tentacle-e2e-transcodes tentacle-e2e-temp)
plugin=3a65d525-990c-4f73-89e9-a0d1500a53d2
fail() { echo "FAIL: $*" >&2; docker logs "$srv" 2>&1 | grep -i tentacle | tail -20 >&2 || true; docker logs "$wrk" 2>&1 | tail -20 >&2 || true; exit 1; }
cleanup() {
    docker rm -f "$srv" "$wrk" >/dev/null 2>&1 || true
    docker network rm "$net" >/dev/null 2>&1 || true
    docker volume rm "${vols[@]}" >/dev/null 2>&1 || true
}
[[ "${KEEP:-0}" == 1 ]] || trap cleanup EXIT
cleanup

for role in server worker; do
    printf 'FROM %s\nCOPY --from=tentacle:%s-%s / /\nRUN mkdir -p /config/data/data /config/cache && chown -R 1000:1000 /config\n' "$base" "$role" "$version" | docker build -q -t "tentacle-dev:${role}" - >/dev/null
done
docker network create "$net" >/dev/null
for v in "${vols[@]}"; do docker volume create "$v" >/dev/null; done
# Shared volumes owned by abc (1000), like the NFS export.
docker run --rm -v tentacle-e2e-media:/a -v tentacle-e2e-transcodes:/b -v tentacle-e2e-temp:/c alpine chown 1000:1000 /a /b /c

mounts=(-v tentacle-e2e-media:/data/media -v tentacle-e2e-transcodes:/config/cache/transcodes -v tentacle-e2e-temp:/config/cache/temp)
env=(-e PUID=1000 -e PGID=1000 -e TZ=Etc/UTC -e TMPDIR=/config/cache/temp -e TENTACLE_TOKEN="$token")

echo "== start server"
docker run -d --name "$srv" --network "$net" "${mounts[@]}" "${env[@]}" tentacle-dev:server >/dev/null
curl() { docker exec "$srv" curl "$@"; }
for _ in $(seq 120); do [[ "$(curl -s http://localhost:8096/health || true)" == Healthy ]] && break; sleep 1; done
[[ "$(curl -s http://localhost:8096/health)" == Healthy ]] || fail "server not healthy"

echo "== test media (4 min 720p h264/aac)"
docker exec -u abc "$srv" sh -c 'mkdir -p "/data/media/movies/Tentacle Test (2026)" && /usr/lib/jellyfin-ffmpeg/ffmpeg -loglevel error -y \
    -f lavfi -i testsrc2=s=1280x720:r=24:d=240 -f lavfi -i sine=f=440:d=240 \
    -c:v libx264 -preset ultrafast -crf 30 -g 48 -c:a aac "/data/media/movies/Tentacle Test (2026)/Tentacle Test (2026).mkv"'

api=http://localhost:8096
auth='MediaBrowser Client="tentacle-e2e", Device="e2e", DeviceId="tentacle-e2e", Version="1"'
curl -sf -X POST "$api/Startup/Configuration" -H 'Content-Type: application/json' -d '{"UICulture":"en-US","MetadataCountryCode":"US","PreferredMetadataLanguage":"en"}' >/dev/null
curl -sf "$api/Startup/User" >/dev/null
curl -sf -X POST "$api/Startup/User" -H 'Content-Type: application/json' -d '{"Name":"e2e","Password":"e2e"}' >/dev/null
curl -sf -X POST "$api/Startup/Complete" >/dev/null
at="$(curl -sf -X POST "$api/Users/AuthenticateByName" -H "Authorization: $auth" -H 'Content-Type: application/json' -d '{"Username":"e2e","Pw":"e2e"}' | jq -r .AccessToken)"
h="Authorization: ${auth}, Token=\"${at}\""
get() { curl -sf "$api/$1" -H "$h"; }

echo "== enable Jellyfin's /metrics (read at startup: restart)"
sys="$(get System/Configuration | jq -c '.EnableMetrics=true')"
curl -sf -X POST "$api/System/Configuration" -H "$h" -H 'Content-Type: application/json' -d "$sys" >/dev/null
docker restart "$srv" >/dev/null
for _ in $(seq 120); do [[ "$(curl -s http://localhost:8096/health || true)" == Healthy ]] && break; sleep 1; done
[[ "$(curl -s http://localhost:8096/health)" == Healthy ]] || fail "server not healthy after restart"
metric() { curl -sf "$api/metrics" | grep -v '^#' | grep -F "$1" | awk '{print $NF}' | head -1; }
[[ "$(metric 'tentacle_broker_up ')" == 1 ]] || fail "tentacle_broker_up not 1 in Jellyfin's /metrics"

curl -sf -X POST "$api/Library/VirtualFolders?name=Movies&collectionType=movies&refreshLibrary=true&paths=%2Fdata%2Fmedia%2Fmovies" -H "$h" \
    -H 'Content-Type: application/json' -d '{"LibraryOptions":{"EnableRealtimeMonitor":false,"EnableTrickplayImageExtraction":false,"EnableChapterImageExtraction":false}}' >/dev/null
docker exec "$srv" test -s "/data/media/movies/Tentacle Test (2026)/Tentacle Test (2026).mkv" || fail "test media missing"
# The scan started by creating the library occasionally misses the file; rescan
# every 20 s until the movie shows up.
item=""
for i in $(seq 90); do
    item="$(get 'Items?Recursive=true&IncludeItemTypes=Movie' | jq -r '.Items[0].Id // empty')"
    [[ -n "$item" ]] && break
    (( i % 20 == 0 )) && curl -sf -X POST "$api/Library/Refresh" -H "$h" >/dev/null
    sleep 1
done
[[ -n "$item" ]] || { docker exec "$srv" ls -laR /data/media >&2; fail "library scan found no movie"; }
echo "item ${item}"

echo "== placement Active, tentacle registers"
cfg="$(get "Plugins/$plugin/Configuration" | jq -c '.Placement="Active"')"
curl -sf -X POST "$api/Plugins/$plugin/Configuration" -H "$h" -H 'Content-Type: application/json' -d "$cfg" >/dev/null
get Tentacle/Status | jq -c '{Placement,StartError,SharedRoots,Tls,TlsFingerprint}'
fp="$(get Tentacle/Status | jq -r '.TlsFingerprint // empty')"
[[ -n "$fp" ]] || fail "agent port does not speak TLS by default"

docker run -d --name "$wrk" --network "$net" "${mounts[@]}" "${env[@]}" \
    -e TENTACLE_BROKER_URL="wss://$srv:8097" -e TENTACLE_ROOTS=/data/media:/config/cache/transcodes:/config/cache/temp:/config/data/data/subtitles:/config/data/data/attachments -e TENTACLE_BROKER_FINGERPRINT="$fp" -e TENTACLE_NODE_NAME=worker1 tentacle-dev:worker >/dev/null
# Registered, then verified (the shares it proves decide which jobs it gets).
for _ in $(seq 60); do [[ "$(get Tentacle/Nodes | jq -r '.[0].State // empty')" =~ ^(Ready|Degraded)$ ]] && break; sleep 1; done
get Tentacle/Nodes | jq -c '.[] | {Name,State,FfmpegVersion,MaxJobs,VerifiedRoots}'
[[ "$(get Tentacle/Nodes | jq -r '.[0].State // empty')" =~ ^(Ready|Degraded)$ ]] || fail "tentacle did not register and verify"
docker exec "$wrk" /usr/local/bin/tentacle/tentacle agent-status | tee /dev/stderr | grep 'tls=pinned' >/dev/null || fail "agent not on pinned TLS"

echo "== jobs are confined to TENTACLE_ROOTS"
[[ "$(get Tentacle/Nodes | jq -r '.[0].Sandbox')" == "Landlock ABI "* ]] || fail "the tentacle does not report its jobs as confined"
movie="/data/media/movies/Tentacle Test (2026)/Tentacle Test (2026).mkv"
docker exec -u abc "$wrk" sh -c 'echo secret > /config/outside.txt'
confined() { docker exec -u abc "$wrk" /usr/local/bin/tentacle/tentacle sandbox --root /data/media --root /config/cache/transcodes -- /usr/lib/jellyfin-ffmpeg/ffmpeg -v error "$@"; }
confined -t 1 -i "$movie" -f null - || fail "a confined ffmpeg cannot read a root"
if confined -f data -i /config/outside.txt -f null - 2>/dev/null; then fail "a confined ffmpeg read a file outside its roots"; fi
if confined -f lavfi -i nullsrc=d=1 -f mpegts -y /config/written.ts 2>/dev/null; then fail "a confined ffmpeg wrote outside its roots"; fi
docker exec "$wrk" test ! -s /config/written.ts || fail "a confined ffmpeg wrote outside its roots"

# Opens an HLS session and fetches segment $2; prints the segment size.
media_source="$item"
hls_query() { echo "MediaSourceId=${media_source}&VideoCodec=h264&AudioCodec=aac&VideoBitrate=2000000&AudioBitrate=128000&MaxWidth=1280&SegmentContainer=ts&PlaySessionId=$1&DeviceId=e2e-$1&ApiKey=${at}"; }
segment() {
    local session=$1 n=$2 main seg
    main="$(curl -sf "$api/Videos/$item/main.m3u8?$(hls_query "$session")")"
    seg="$(grep -v '^#' <<<"$main" | sed -n "$((n + 1))p")"
    [[ -n "$seg" ]] || fail "no segment $n in playlist"
    curl -sf -m 60 -o /dev/null -w '%{size_download}' "$api/Videos/$item/$seg"
}
remote_ffmpeg() { docker exec "$wrk" pgrep -f /usr/lib/jellyfin-ffmpeg/ffmpeg 2>/dev/null | tr '\n' ' ' || true; }
local_ffmpeg() { docker exec "$srv" pgrep -f '^/usr/lib/jellyfin-ffmpeg/ffmpeg .*-f hls' 2>/dev/null | tr '\n' ' ' || true; }
last_job() { get 'Tentacle/Jobs?limit=20' | jq -c '[.[] | select(.Kind=="Transcode")][0] | {Id,Node,Reason,Outcome,ExitCode}'; }

echo "== 1. HLS transcode on the tentacle"
size="$(segment s1 0)"
before="$(remote_ffmpeg)"
echo "segment 0: ${size} bytes; job $(last_job)"
(( size > 10000 )) || fail "segment too small"
[[ "$(last_job | jq -r .Node)" == worker1 ]] || fail "transcode did not run on the tentacle"
[[ -n "$before" ]] || fail "no ffmpeg on the tentacle"
item_name="$(get 'Tentacle/Jobs?limit=20' | jq -r '[.[] | select(.Kind=="Transcode")][0].Item // empty')"
echo "attributed to: ${item_name}"
[[ "$item_name" == "Tentacle Test (2026)"* ]] || fail "transcode not matched to its library item"
[[ -z "$(local_ffmpeg)" ]] || fail "a real ffmpeg runs on the server too"

echo "== 2. seek far ahead: Jellyfin restarts the transcode, the old remote ffmpeg goes"
size="$(segment s1 70)"
(( size > 10000 )) || fail "seek segment too small"
sleep 2
after="$(remote_ffmpeg)"
echo "remote ffmpeg before: ${before}after: ${after}"
for pid in $before; do [[ " $after " != *" $pid "* ]] || fail "old remote ffmpeg $pid survived the seek"; done

docker exec "$srv" sh -c 'grep -l "frame=" /config/log/FFmpeg.Transcode-*.log' >/dev/null || fail "no ffmpeg progress relayed into Jellyfin's log"
echo "progress relayed: $(docker exec "$srv" sh -c 'grep -ho "time=[0-9:.]*" /config/log/FFmpeg.Transcode-*.log | tail -1')"

echo "== 3. stop playback: the remote ffmpeg ends (q, then kill after 5 s)"
curl -sf -X DELETE "$api/Videos/ActiveEncodings?deviceId=e2e-s1&playSessionId=s1" -H "$h"
for _ in $(seq 10); do [[ -z "$(remote_ffmpeg)" ]] && break; sleep 1; done
[[ -z "$(remote_ffmpeg)" ]] || fail "remote ffmpeg still running after stop"
echo "stopped; job $(last_job)"

echo "== 4. kill the tentacle mid-stream: job lost, playback continues locally"
segment s2 0 >/dev/null
[[ "$(last_job | jq -r .Node)" == worker1 ]] || fail "second session not on the tentacle"
docker kill "$wrk" >/dev/null
for _ in $(seq 20); do [[ "$(get 'Tentacle/Jobs?limit=20' | jq -r '[.[] | select(.Kind=="Transcode")][0].Outcome')" != running ]] && break; sleep 1; done
echo "after kill: job $(last_job)"
[[ "$(last_job | jq -r .Outcome)" == lost ]] || fail "job not reported lost"
for _ in $(seq 20); do [[ "$(get Tentacle/Nodes | jq length)" == 0 ]] && break; sleep 1; done
size="$(segment s2 6)"
echo "segment 6 after the loss: ${size} bytes; job $(last_job)"
(( size > 10000 )) || fail "playback did not resume"
[[ "$(last_job | jq -r .Node)" == local ]] || fail "resumed job not local"

echo "== 5. metrics"
curl -sf "$api/metrics" | grep '^tentacle_' | grep -v '_bucket{' | sort
expect() { local v; v="$(metric "$1")"; [[ -n "$v" ]] && awk -v v="$v" -v min="$2" 'BEGIN { exit !(v >= min) }' || fail "metric $1 = '${v}', expected >= $2"; }
expect 'tentacle_jobs_total{node="worker1",kind="Transcode",outcome="ok"}' 2
expect 'tentacle_jobs_total{node="worker1",kind="Transcode",outcome="lost"}' 1
expect 'tentacle_placements_total{kind="Transcode",node="worker1",reason="least-loaded"}' 3
expect 'tentacle_placements_total{kind="Transcode",node="local",reason="no-tentacle"}' 1
expect 'tentacle_job_duration_seconds_count{node="worker1",kind="Transcode"}' 3
expect 'tentacle_jobs_running{node="local",kind="Transcode"}' 1
expect 'tentacle_node_state{node="worker1",state="Disconnected"}' 1
expect 'tentacle_placement_mode{mode="Active"}' 1
[[ "$(metric 'tentacle_node_state{node="worker1",state="Ready"}')" == 0 ]] || fail "worker1 still Ready in metrics"
docker logs "$srv" 2>&1 | grep -E 'tentacle_job job=.*node="?worker1"? .*outcome="?lost' >/dev/null || fail "no logfmt tentacle_job line for the lost job"
docker logs "$srv" 2>&1 | grep 'tentacle_job job=' | tail -3

echo "PASS: M1 e2e"
