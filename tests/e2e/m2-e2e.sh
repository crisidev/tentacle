#!/usr/bin/env bash
# M2 acceptance: verification, per-kind placement, draining.
#   worker1: every share            -> Ready
#   worker2: no temp share          -> Degraded (still used for jobs that do not need temp)
#   worker3: reports ffmpeg 7.1     -> Incompatible (never used)
# Checks: a local-only library stays local (path-ineligible) with a real exit code;
# subtitle extraction, attachment extraction (the cwd variant, for ASS burn-in) and
# trickplay run on tentacles; trickplay only where temp is shared; SIGTERM drains.
#   tests/e2e/m2-e2e.sh [version]   (needs scripts/build.sh mod first; KEEP=1 keeps containers)
set -euo pipefail
version="${1:-$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$(dirname "$0")/../../Directory.Build.props")}"
base="linuxserver/jellyfin:version-12.1ubu2604"
p=tentacle-m2
net=$p
srv=$p-server
token=m2-$(date +%s)
vols=(media local transcodes temp subs attach)
plugin=3a65d525-990c-4f73-89e9-a0d1500a53d2
fail() {
    echo "FAIL: $*" >&2
    docker logs "$srv" 2>&1 | grep -iE 'tentacle|ERR' | tail -25 >&2 || true
    exit 1
}
cleanup() {
    docker rm -f "$srv" $p-worker1 $p-worker2 $p-worker3 >/dev/null 2>&1 || true
    docker network rm "$net" >/dev/null 2>&1 || true
    for v in "${vols[@]}"; do docker volume rm "$p-$v" >/dev/null 2>&1 || true; done
}
[[ "${KEEP:-0}" == 1 ]] || trap cleanup EXIT
cleanup

for role in server worker; do
    printf 'FROM %s\nCOPY --from=tentacle:%s-%s / /\nRUN mkdir -p /config/data/data /config/cache && chown -R 1000:1000 /config\n' "$base" "$role" "$version" | docker build -q -t "tentacle-dev:${role}" - >/dev/null
done
docker network create "$net" >/dev/null
for v in "${vols[@]}"; do docker volume create "$p-$v" >/dev/null; done
docker run --rm $(for v in "${vols[@]}"; do echo "-v $p-$v:/v/$v"; done) alpine chown 1000:1000 $(for v in "${vols[@]}"; do echo "/v/$v"; done)

m_media=(-v $p-media:/data/media)
m_transcodes=(-v $p-transcodes:/config/cache/transcodes)
m_temp=(-v $p-temp:/config/cache/temp)
m_subs=(-v $p-subs:/config/data/data/subtitles -v $p-attach:/config/data/data/attachments)
env=(-e PUID=1000 -e PGID=1000 -e TZ=Etc/UTC -e TMPDIR=/config/cache/temp -e TENTACLE_TOKEN="$token")

echo "== server"
docker run -d --name "$srv" --network "$net" "${m_media[@]}" -v $p-local:/data/localonly "${m_transcodes[@]}" "${m_temp[@]}" "${m_subs[@]}" "${env[@]}" tentacle-dev:server >/dev/null
curl() { docker exec "$srv" curl "$@"; }
for _ in $(seq 120); do [[ "$(curl -s http://localhost:8096/health || true)" == Healthy ]] && break; sleep 1; done
[[ "$(curl -s http://localhost:8096/health)" == Healthy ]] || fail "server not healthy"

echo "== media: h264/aac + SRT + ASS + font attachment; a copy in a server-only library"
docker exec -u abc "$srv" sh -c '
set -e
cd /tmp
printf "1\n00:00:01,000 --> 00:03:00,000\nTentacle subtitle line\n" > s.srt
printf "[Script Info]\nScriptType: v4.00+\n\n[V4+ Styles]\nFormat: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\nStyle: Default,DejaVu Sans,48,&H00FFFFFF,&H000000FF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,2,0,2,10,10,10,1\n\n[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\nDialogue: 0,0:00:01.00,0:03:00.00,Default,,0,0,0,,Tentacle ASS line\n" > s.ass
mkdir -p "/data/media/movies/Tentacle Test (2026)" "/data/localonly/movies/Local Only (2026)"
out="/data/media/movies/Tentacle Test (2026)/Tentacle Test (2026).mkv"
/usr/lib/jellyfin-ffmpeg/ffmpeg -loglevel error -y -f lavfi -i testsrc2=s=1280x720:r=24:d=240 -f lavfi -i sine=f=440:d=240 -i s.srt -i s.ass \
    -map 0 -map 1 -map 2 -map 3 -c:v libx264 -preset ultrafast -crf 30 -g 48 -c:a aac -c:s:0 srt -c:s:1 ass \
    -attach /usr/share/fonts/truetype/dejavu/DejaVuSans.ttf -metadata:s:t mimetype=application/x-truetype-font "$out"
cp "$out" "/data/localonly/movies/Local Only (2026)/Local Only (2026).mkv"'

api=http://localhost:8096
auth='MediaBrowser Client="tentacle-m2", Device="m2", DeviceId="tentacle-m2", Version="1"'
curl -sf -X POST "$api/Startup/Configuration" -H 'Content-Type: application/json' -d '{"UICulture":"en-US","MetadataCountryCode":"US","PreferredMetadataLanguage":"en"}' >/dev/null
curl -sf "$api/Startup/User" >/dev/null
curl -sf -X POST "$api/Startup/User" -H 'Content-Type: application/json' -d '{"Name":"m2","Password":"m2"}' >/dev/null
curl -sf -X POST "$api/Startup/Complete" >/dev/null
at="$(curl -sf -X POST "$api/Users/AuthenticateByName" -H "Authorization: $auth" -H 'Content-Type: application/json' -d '{"Username":"m2","Pw":"m2"}' | jq -r .AccessToken)"
h="Authorization: ${auth}, Token=\"${at}\""
get() { curl -sf "$api/$1" -H "$h"; }
post() { curl -sf -X POST "$api/$1" -H "$h" -H 'Content-Type: application/json' -d "${2:-{\}}"; }

opts='{"LibraryOptions":{"EnableRealtimeMonitor":false,"EnableTrickplayImageExtraction":true,"ExtractTrickplayImagesDuringLibraryScan":false,"EnableChapterImageExtraction":false}}'
post "Library/VirtualFolders?name=Movies&collectionType=movies&refreshLibrary=false&paths=%2Fdata%2Fmedia%2Fmovies" "$opts" >/dev/null
post "Library/VirtualFolders?name=Local&collectionType=movies&refreshLibrary=true&paths=%2Fdata%2Flocalonly%2Fmovies" "$opts" >/dev/null
item=""; local_item=""
for i in $(seq 120); do
    items="$(get 'Items?Recursive=true&IncludeItemTypes=Movie')"
    item="$(jq -r '.Items[] | select(.Name | startswith("Tentacle Test")) | .Id' <<<"$items")"
    local_item="$(jq -r '.Items[] | select(.Name | startswith("Local Only")) | .Id' <<<"$items")"
    [[ -n "$item" && -n "$local_item" ]] && break
    (( i % 20 == 0 )) && post Library/Refresh >/dev/null
    sleep 1
done
[[ -n "$item" && -n "$local_item" ]] || fail "library scan incomplete"
streams="$(get "Items/$item/PlaybackInfo?UserId=$(get Users/Me | jq -r .Id)" | jq -c '[.MediaSources[0].MediaStreams[] | {Index,Type,Codec}]')"
echo "streams: $streams"

cfg="$(get "Plugins/$plugin/Configuration" | jq -c '.Placement="Active" | .BackgroundNice=12')"
post "Plugins/$plugin/Configuration" "$cfg" >/dev/null

echo "== workers"
fp="$(get Tentacle/Status | jq -r '.TlsFingerprint')"
worker() { docker run -d --name "$p-$1" --network "$net" "${env[@]}" -e TENTACLE_BROKER_URL="wss://$srv:8097" -e TENTACLE_ROOTS=/data/media:/config/cache/transcodes:/config/cache/temp:/config/data/data/subtitles:/config/data/data/attachments -e TENTACLE_BROKER_FINGERPRINT="$fp" -e TENTACLE_NODE_NAME="$1" "${@:2}" tentacle-dev:worker >/dev/null; }
worker worker1 "${m_media[@]}" "${m_transcodes[@]}" "${m_temp[@]}" "${m_subs[@]}" -e TENTACLE_DRAIN_SECONDS=6 -e S6_SERVICES_GRACETIME=20000 -e S6_KILL_GRACETIME=20000
worker worker2 "${m_media[@]}" "${m_transcodes[@]}" "${m_subs[@]}"
worker worker3 "${m_media[@]}" "${m_transcodes[@]}" "${m_temp[@]}" "${m_subs[@]}" -e TENTACLE_FAKE_FFMPEG_VERSION="ffmpeg version 7.1.1-Jellyfin Copyright"
state() { get Tentacle/Nodes | jq -r --arg n "$1" '.[] | select(.Name==$n) | .State'; }
for _ in $(seq 60); do
    [[ "$(state worker1)/$(state worker2)/$(state worker3)" == Ready/Degraded/Incompatible ]] && break
    sleep 1
done
get Tentacle/Nodes | jq -c '.[] | {Name,State,failed:[.Checks[] | select(.Ok|not) | .Name]}'
[[ "$(state worker1)" == Ready ]] || fail "worker1 not Ready"
[[ "$(state worker2)" == Degraded ]] || fail "worker2 (no temp share) not Degraded"
[[ "$(state worker3)" == Incompatible ]] || fail "worker3 (ffmpeg 7.1) not Incompatible"

jobs() { get 'Tentacle/Jobs?limit=200'; }
hls() { echo "MediaSourceId=$2&VideoCodec=h264&AudioCodec=aac&VideoBitrate=2000000&AudioBitrate=128000&MaxWidth=1280&SegmentContainer=ts&PlaySessionId=$1&DeviceId=m2-$1&ApiKey=${at}${3:-}"; }
segment() {
    local main seg
    main="$(curl -sf "$api/Videos/$2/main.m3u8?$(hls "$1" "$2" "${4:-}")")"
    seg="$(grep -v '^#' <<<"$main" | sed -n "$(( $3 + 1 ))p")"
    curl -sf -m 90 -o /dev/null -w '%{size_download}' "$api/Videos/$2/$seg"
}
job_for() { jobs | jq -c --arg s "$1" '[.[] | select(.Command | contains($s))][0] | {Kind,Node,Reason,Outcome,ExitCode}'; }

echo "== 1. server-only library: local, path-ineligible, real exit code"
size="$(segment loc "$local_item" 0)"
(( size > 10000 )) || fail "local segment too small"
curl -sf -X DELETE "$api/Videos/ActiveEncodings?deviceId=m2-loc&playSessionId=loc" -H "$h"
sleep 7
j="$(job_for 'Local Only')"; echo "$j"
[[ "$(jq -r .Node <<<"$j")" == local ]] || fail "server-only media ran remotely"
[[ "$(jq -r .Reason <<<"$j")" == path-ineligible:/data/localonly/* ]] || fail "reason is not path-ineligible"
[[ "$(jq -r .ExitCode <<<"$j")" != null ]] || fail "no exit code recorded for a local job"

echo "== 2. subtitle extraction on a tentacle"
vtt="$(curl -sf "$api/Videos/$item/$item/Subtitles/2/0/Stream.vtt?ApiKey=$at")"
grep -q 'Tentacle subtitle line' <<<"$vtt" || fail "subtitle text missing: $vtt"
j="$(jobs | jq -c '[.[] | select(.Kind=="Extract" and (.Command | contains("-dump_attachment") | not))][0] | {Kind,Node,Reason,Outcome,ExitCode}')"; echo "$j"
[[ "$(jq -r .Node <<<"$j")" == worker[12] ]] || fail "subtitle extraction not on a tentacle"
[[ "$(jq -r .Outcome <<<"$j")" == ok ]] || fail "subtitle extraction failed"

echo "== 3. ASS burn-in: attachments (cwd variant) and the transcode on tentacles"
size="$(segment burn "$item" 0 '&SubtitleStreamIndex=3&SubtitleMethod=Encode')"
(( size > 10000 )) || fail "burn-in segment too small"
j="$(jobs | jq -c '[.[] | select(.Command | contains("-dump_attachment"))][0] | {Kind,Node,Reason,Outcome,ExitCode,Command}')"; echo "$j" | cut -c1-300
[[ "$(jq -r .Node <<<"$j")" == worker[12] ]] || fail "attachment extraction not on a tentacle"
[[ "$(jq -r .Outcome <<<"$j")" == ok ]] || fail "attachment extraction failed"
docker exec "$srv" sh -c 'find /config/data/data/attachments -name "*.ttf" | grep -q .' || fail "extracted font not visible on the server"
j="$(job_for 'subtitles=')"; echo "$j"
[[ "$(jq -r .Node <<<"$j")" == worker[12] ]] || fail "burn-in transcode not on a tentacle"
curl -sf -X DELETE "$api/Videos/ActiveEncodings?deviceId=m2-burn&playSessionId=burn" -H "$h"

echo "== 4. trickplay: background, only where temp is shared (worker1)"
task="$(get ScheduledTasks | jq -r '.[] | select(.Key=="RefreshTrickplayImages") | .Id')"
# Sample the nice value of the tentacle's trickplay ffmpeg while it runs (/proc/<pid>/stat field 19).
( for _ in $(seq 240); do
    docker exec $p-worker1 sh -c 'for pid in $(pgrep -f "^/usr/lib/jellyfin-ffmpeg/ffmpeg .*-f image2"); do awk "{print \$19}" /proc/$pid/stat; done' 2>/dev/null
    sleep 0.25
  done > /tmp/tentacle-m2-nice.txt ) &
sampler=$!
post "ScheduledTasks/Running/$task" >/dev/null
for _ in $(seq 120); do
    [[ "$(get "ScheduledTasks/$task" | jq -r '.State')" == Idle && "$(get "ScheduledTasks/$task" | jq -r '.LastExecutionResult.Status // empty')" != "" ]] && break
    sleep 1
done
get "ScheduledTasks/$task" | jq -c '{State,Result:.LastExecutionResult.Status}'
kill "$sampler" 2>/dev/null || true
nices="$(sort -u /tmp/tentacle-m2-nice.txt | tr '\n' ' ')"; rm -f /tmp/tentacle-m2-nice.txt
echo "trickplay ffmpeg nice on worker1: ${nices:-none sampled}"
[[ "$nices" == "12 " ]] || fail "trickplay did not run at the configured nice 12"
[[ "$(jobs | jq -r '[.[] | select(.Kind=="Trickplay" and .Node=="worker1")][0].Nice')" == 12 ]] || fail "job record lacks nice 12"
t="$(jobs | jq -c '[.[] | select(.Kind=="Trickplay" and (.Command | contains("Tentacle Test")))] | {count:length, nodes:([.[].Node] | unique), outcomes:([.[].Outcome] | unique)}')"; echo "trickplay jobs: $t"
[[ "$(jq -r '.count' <<<"$t")" -ge 1 ]] || fail "no trickplay job"
[[ "$(jq -c '.nodes' <<<"$t")" == '["worker1"]' ]] || fail "trickplay ran somewhere other than worker1"
[[ "$(jq -c '.outcomes' <<<"$t")" == '["ok"]' ]] || fail "trickplay jobs failed"
code="$(curl -s -o /dev/null -w '%{http_code}' "$api/Videos/$item/Trickplay/320/tiles.m3u8?ApiKey=$at")"
[[ "$code" == 200 ]] || fail "trickplay tiles not served ($code)"

echo "== 5. SIGTERM drains the tentacle running a playback: nothing new goes there"
segment d1 "$item" 0 >/dev/null
# Ties rotate between tentacles, so drain whichever one took this playback.
d="$(jobs | jq -r '[.[] | select(.Kind=="Transcode")][0].Node')"
[[ "$d" == worker1 || "$d" == worker2 ]] || fail "the playback did not go to a tentacle ($d)"
echo "playback on $d; draining it"
docker stop -t 30 "$p-$d" >/dev/null &
stopper=$!
for _ in $(seq 20); do [[ "$(state "$d")" == Draining ]] && break; sleep 0.5; done
[[ "$(state "$d")" == Draining ]] || fail "$d not Draining after SIGTERM"
segment d2 "$item" 0 >/dev/null
j="$(jobs | jq -c '[.[] | select(.Kind=="Transcode")][0] | {Node,Reason,Outcome}')"; echo "new session during drain: $j"
[[ "$(jq -r .Node <<<"$j")" != "$d" ]] || fail "a new job went to the draining tentacle"
wait "$stopper"
docker logs "$p-$d" 2>&1 | grep -E 'draining|drain timeout|agent stopped' || fail "no drain in the agent log"

echo "PASS: M2 e2e"
