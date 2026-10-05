#!/usr/bin/env bash
# M5 soak: Jellyfin 12.1 + two tentacles under steady load while things break.
#   load:  3 playback loops (open a session, seek around, stop) and a stream of
#          short jobs through the shim (ffprobe, ffmpeg -f null)
#   chaos: every 15-35 s one of: SIGKILL a worker container (then start it),
#          restart a worker (drain), SIGKILL an agent (s6 restarts it); the server
#          (and so the broker) restarts once, a third of the way in
#   after: no ffmpeg left anywhere, no job "running", every slot released, both
#          tentacles back, the broker's fds did not grow
#   SOAK_SECONDS=600 tests/e2e/soak.sh [version]   (needs scripts/build.sh mod first)
set -euo pipefail
version="${1:-$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$(dirname "$0")/../../Directory.Build.props")}"
seconds="${SOAK_SECONDS:-600}"
fd_slack="${SOAK_FD_SLACK:-40}"
base="linuxserver/jellyfin:version-12.1ubu2604"
p=tentacle-soak
srv=$p-server
workers=(w1 w2)
vols=($p-media $p-transcodes $p-temp $p-config)
plugin=3a65d525-990c-4f73-89e9-a0d1500a53d2
work="$(mktemp -d)"
fail() {
    echo "FAIL: $*" >&2
    docker logs "$srv" 2>&1 | grep -i tentacle | tail -20 >&2 || true
    for w in "${workers[@]}"; do docker logs "$p-$w" 2>&1 | grep '\[tentacle\]' | tail -5 | sed "s/^/$w: /" >&2 || true; done
    exit 1
}
cleanup() {
    jobs -p | xargs -r kill 2>/dev/null || true
    docker rm -f "$srv" "${workers[@]/#/$p-}" >/dev/null 2>&1 || true
    docker network rm "$p" >/dev/null 2>&1 || true
    docker volume rm "${vols[@]}" >/dev/null 2>&1 || true
    rm -rf "$work"
}
[[ "${KEEP:-0}" == 1 ]] || trap cleanup EXIT
cleanup
work="$(mktemp -d)"

for role in server worker; do
    printf 'FROM %s\nCOPY --from=tentacle:%s-%s / /\nRUN mkdir -p /config/data/data /config/cache && chown -R 1000:1000 /config\n' "$base" "$role" "$version" | docker build -q -t "tentacle-dev:${role}" - >/dev/null
done
docker network create "$p" >/dev/null
for v in "${vols[@]}"; do docker volume create "$v" >/dev/null; done
docker run --rm -v $p-media:/a -v $p-transcodes:/b -v $p-temp:/c -v $p-config:/d alpine sh -c 'mkdir -p /d/data/data /d/cache && chown -R 1000:1000 /a /b /c /d'
shared=(-v $p-media:/data/media -v $p-transcodes:/config/cache/transcodes -v $p-temp:/config/cache/temp)
env=(-e PUID=1000 -e PGID=1000 -e TZ=Etc/UTC -e TMPDIR=/config/cache/temp -e TENTACLE_TOKEN=soak-token)

api=http://localhost:8096
curl() { docker exec "$srv" curl "$@"; }
healthy() { [[ "$(curl -s -m 5 $api/health 2>/dev/null || true)" == Healthy ]]; }
wait_healthy() { for _ in $(seq 180); do healthy && return 0; sleep 1; done; fail "server not healthy"; }

echo "== server"
docker run -d --name "$srv" --network "$p" -v $p-config:/config "${shared[@]}" "${env[@]}" tentacle-dev:server >/dev/null
wait_healthy
docker exec -u abc "$srv" sh -c 'mkdir -p "/data/media/movies/Soak (2026)" && /usr/lib/jellyfin-ffmpeg/ffmpeg -loglevel error -y \
    -f lavfi -i testsrc2=s=640x360:r=24:d=600 -f lavfi -i sine=f=440:d=600 \
    -c:v libx264 -preset ultrafast -crf 32 -g 48 -c:a aac "/data/media/movies/Soak (2026)/Soak (2026).mkv"'
auth='MediaBrowser Client="tentacle-soak", Device="soak", DeviceId="tentacle-soak", Version="1"'
curl -sf -X POST "$api/Startup/Configuration" -H 'Content-Type: application/json' -d '{"UICulture":"en-US","MetadataCountryCode":"US","PreferredMetadataLanguage":"en"}' >/dev/null
curl -sf "$api/Startup/User" >/dev/null
curl -sf -X POST "$api/Startup/User" -H 'Content-Type: application/json' -d '{"Name":"soak","Password":"soak"}' >/dev/null
curl -sf -X POST "$api/Startup/Complete" >/dev/null
at="$(curl -sf -X POST "$api/Users/AuthenticateByName" -H "Authorization: $auth" -H 'Content-Type: application/json' -d '{"Username":"soak","Pw":"soak"}' | jq -r .AccessToken)"
h="Authorization: ${auth}, Token=\"${at}\""
get() { curl -sf -m 20 "$api/$1" -H "$h"; }
curl -sf -X POST "$api/Library/VirtualFolders?name=Movies&collectionType=movies&refreshLibrary=true&paths=%2Fdata%2Fmedia%2Fmovies" -H "$h" \
    -H 'Content-Type: application/json' -d '{"LibraryOptions":{"EnableRealtimeMonitor":false,"EnableTrickplayImageExtraction":false,"EnableChapterImageExtraction":false}}' >/dev/null
item=""
for i in $(seq 90); do
    item="$(get 'Items?Recursive=true&IncludeItemTypes=Movie' | jq -r '.Items[0].Id // empty' || true)"
    [[ -n "$item" ]] && break
    (( i % 20 == 0 )) && curl -sf -X POST "$api/Library/Refresh" -H "$h" >/dev/null
    sleep 1
done
[[ -n "$item" ]] || fail "library scan found no movie"
cfg="$(get "Plugins/$plugin/Configuration" | jq -c '.Placement="Active"')"
curl -sf -X POST "$api/Plugins/$plugin/Configuration" -H "$h" -H 'Content-Type: application/json' -d "$cfg" >/dev/null
fp="$(get Tentacle/Status | jq -r .TlsFingerprint)"

echo "== tentacles"
for w in "${workers[@]}"; do
    docker run -d --name "$p-$w" --network "$p" "${shared[@]}" "${env[@]}" -e TENTACLE_BROKER_URL="wss://$srv:8097" \
        -e TENTACLE_ROOTS=/data/media:/config/cache/transcodes:/config/cache/temp:/config/data/data/subtitles:/config/data/data/attachments -e TENTACLE_BROKER_FINGERPRINT="$fp" -e TENTACLE_NODE_NAME="$w" -e TENTACLE_DRAIN_SECONDS=5 tentacle-dev:worker >/dev/null
done
ready() { [[ "$(get Tentacle/Nodes 2>/dev/null | jq '[.[] | select(.State=="Ready" or .State=="Degraded")] | length' 2>/dev/null || echo 0)" == "${#workers[@]}" ]]; }
for _ in $(seq 90); do ready && break; sleep 1; done
ready || fail "tentacles did not register"
get Tentacle/Nodes | jq -c '.[] | {Name,State}'

deadline=$(( $(date +%s) + seconds ))
running() { (( $(date +%s) < deadline )); }
count() { echo 1 >> "$work/$1"; }

# Playback: open, fetch a few segments far apart (each far seek restarts the
# transcode), stop. Segment failures while chaos hits are expected and counted.
playback() {
    local n=0 session main segs seg
    while running; do
        n=$((n + 1)); session="s$1-$n"
        q="MediaSourceId=${item}&VideoCodec=h264&AudioCodec=aac&VideoBitrate=800000&AudioBitrate=96000&MaxWidth=640&SegmentContainer=ts&PlaySessionId=$session&DeviceId=d$1&ApiKey=${at}"
        if main="$(curl -sf -m 20 "$api/Videos/$item/main.m3u8?$q" 2>/dev/null)"; then
            segs="$(grep -v '^#' <<<"$main" || true)"
            for k in 0 $((RANDOM % 100)) $((RANDOM % 100)); do
                seg="$(sed -n "$((k + 1))p" <<<"$segs")"
                [[ -n "$seg" ]] || continue
                if curl -sf -m 45 -o /dev/null "$api/Videos/$item/$seg" 2>/dev/null; then count seg-ok; else count seg-fail; fi
            done
        else
            count playlist-fail
        fi
        curl -sf -m 10 -X DELETE "$api/Videos/ActiveEncodings?deviceId=d$1&playSessionId=$session" -H "$h" >/dev/null 2>&1 || true
        sleep $((RANDOM % 3))
    done
}

# Short jobs through the shim, as Jellyfin's uid: ffprobe (always local exec) and
# a 1 s ffmpeg decode (through the broker, placed locally as "other").
shim_jobs() {
    local movie="/data/media/movies/Soak (2026)/Soak (2026).mkv"
    while running; do
        if docker exec -u abc "$srv" /usr/local/bin/tentacle/ffprobe -v error -show_format "$movie" >/dev/null 2>&1; then count probe-ok; else count probe-fail; fi
        if docker exec -u abc "$srv" /usr/local/bin/tentacle/ffmpeg -v error -t 1 -i "$movie" -f null - >/dev/null 2>&1; then count shim-ok; else count shim-fail; fi
    done
}

chaos() {
    local server_restart_at=$(( $(date +%s) + seconds / 3 )) restarted=0 w
    while running; do
        sleep $((15 + RANDOM % 20))
        running || break
        if (( restarted == 0 && $(date +%s) >= server_restart_at )); then
            echo "chaos: restart the server (broker)"; count chaos-server
            docker restart -t 15 "$srv" >/dev/null || true
            restarted=1
            continue
        fi
        w="${workers[$((RANDOM % ${#workers[@]}))]}"
        case $((RANDOM % 3)) in
            0) echo "chaos: SIGKILL $w"; count chaos-kill
               docker kill "$p-$w" >/dev/null || true; sleep 5; docker start "$p-$w" >/dev/null || true ;;
            1) echo "chaos: restart $w (drain)"; count chaos-restart
               docker restart -t 20 "$p-$w" >/dev/null || true ;;
            2) echo "chaos: SIGKILL the agent on $w"; count chaos-agent
               docker exec "$p-$w" pkill -9 -f 'tentacle agent' >/dev/null 2>&1 || true ;;
        esac
    done
}

# By process name: a pattern on the command line would match these sh -c wrappers.
fds() { docker exec "$srv" sh -c 'ls /proc/$(pgrep -o -x jellyfin)/fd | wc -l'; }
rss() { docker exec "$srv" sh -c 'awk "/VmRSS/ {print \$2}" /proc/$(pgrep -o -x jellyfin)/status'; }

echo "== soak for ${seconds}s"
for i in 1 2 3; do playback "$i" & done
shim_jobs &
chaos &
# After the server restart (a third in) the broker is fresh: take the fd baseline
# once it has been under load for a while.
sleep $(( seconds / 3 + 60 ))
wait_healthy
fd_base="$(fds)"; rss_base="$(rss)"
echo "baseline: jellyfin fds ${fd_base}, rss ${rss_base} kB"
wait

echo "== settle"
wait_healthy
for w in "${workers[@]}"; do docker start "$p-$w" >/dev/null 2>&1 || true; done
for _ in $(seq 90); do ready && break; sleep 1; done
sleep 20

tally() { [[ -f "$work/$1" ]] && wc -l < "$work/$1" || echo 0; }
for k in seg-ok seg-fail playlist-fail probe-ok probe-fail shim-ok shim-fail chaos-server chaos-kill chaos-restart chaos-agent; do printf '%s=%s ' "$k" "$(tally $k)"; done; echo
get 'Tentacle/Jobs?limit=500' | jq -r 'group_by(.Node + " " + .Kind + " " + .Outcome) | .[] | "\(length)\t\(.[0].Node) \(.[0].Kind) \(.[0].Outcome)"' | sort -rn

ready || fail "tentacles did not all come back"
for w in "${workers[@]}"; do
    left="$(docker exec "$p-$w" pgrep -f /usr/lib/jellyfin-ffmpeg/ffmpeg 2>/dev/null | tr '\n' ' ' || true)"
    [[ -z "$left" ]] || fail "ffmpeg left on $w: $left"
done
left="$(docker exec "$srv" pgrep -f '^(/usr/lib/jellyfin-ffmpeg|/usr/local/bin/tentacle)/ff' 2>/dev/null | tr '\n' ' ' || true)"
[[ -z "$left" ]] || fail "ffmpeg or shims left on the server: $left"
stuck="$(get 'Tentacle/Jobs?limit=500' | jq '[.[] | select(.Outcome=="running")] | length')"
[[ "$stuck" == 0 ]] || fail "$stuck jobs still 'running'"
loads="$(get Tentacle/Nodes | jq -c '[.[] | {Name,Load,BackgroundLoad,ActiveJobs}]')"
echo "tentacles: $loads"
[[ "$(jq '[.[] | select(.Load != 0 or .ActiveJobs != 0)] | length' <<<"$loads")" == 0 ]] || fail "slots not released"

ok=$(( $(tally seg-ok) )); bad=$(( $(tally seg-fail) + $(tally playlist-fail) ))
(( ok > 0 && ok * 100 / (ok + bad) >= 70 )) || fail "too many playback failures: $ok ok, $bad failed"
(( $(tally probe-fail) * 100 <= ($(tally probe-ok) + 1) * 10 )) || fail "too many ffprobe failures"
(( $(tally shim-ok) >= 50 )) || fail "too few shim jobs ran: $(tally shim-ok)"
(( $(tally chaos-server) == 1 )) || fail "the server restart never happened"

fd_end="$(fds)"; rss_end="$(rss)"
echo "end: jellyfin fds ${fd_end} (baseline ${fd_base}), rss ${rss_end} kB (baseline ${rss_base} kB)"
(( fd_end <= fd_base + fd_slack )) || fail "jellyfin fds grew from $fd_base to $fd_end"
for w in "${workers[@]}"; do
    # As the agent's user: root in the container may not read another uid's fd links.
    fdlist="$(docker exec -u abc "$p-$w" sh -c 'for f in /proc/$(pgrep -o -x tentacle)/fd/*; do readlink "$f"; done')"
    n="$(wc -l <<<"$fdlist")"; pipes="$(grep -c '^pipe:' <<<"$fdlist" || true)"
    echo "agent $w: $n fds ($pipes pipes)"
    # Idle, an agent holds ~10 (stdio, epoll, its runtime's pipes, the control socket);
    # every job that leaked its stdio would add 3 pipes.
    (( n <= 16 )) || fail "agent on $w holds $n fds while idle: $(sort <<<"$fdlist" | uniq -c | tr '\n' ' ')"
done

echo "PASS: soak (${seconds}s)"
