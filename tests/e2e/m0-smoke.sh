#!/usr/bin/env bash
# M0 acceptance, locally: Jellyfin 12.1 starts through the shim with the plugin
# Active and its dashboard page listed; a worker runs the agent, not Jellyfin.
# The mod layer is copied in at build time, which is what docker-mods does at
# container start (cp of the single mod layer before the s6 DB is compiled).
#   tests/e2e/m0-smoke.sh [version]   (needs scripts/build.sh mod first)
set -euo pipefail
version="${1:-$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$(dirname "$0")/../../Directory.Build.props")}"
base="linuxserver/jellyfin:version-12.1ubu2604"
net=tentacle-smoke
fail() { echo "FAIL: $*" >&2; exit 1; }
cleanup() { docker rm -f tentacle-smoke-server tentacle-smoke-worker >/dev/null 2>&1 || true; docker network rm "$net" >/dev/null 2>&1 || true; }
trap cleanup EXIT
cleanup

for role in server worker; do
    printf 'FROM %s\nCOPY --from=tentacle:%s-%s / /\nRUN mkdir -p /config/data/data /config/cache && chown -R 1000:1000 /config\n' "$base" "$role" "$version" |
        docker build -q -t "tentacle-dev:${role}" - >/dev/null
done
docker network create "$net" >/dev/null

echo "== server"
docker run -d --name tentacle-smoke-server --network "$net" -e PUID=1000 -e PGID=1000 tentacle-dev:server >/dev/null
# All HTTP goes through the server container's own curl: published ports are not
# reachable from a CI job under docker-in-docker.
curl() { docker exec tentacle-smoke-server curl "$@"; }
for _ in $(seq 120); do
    [[ "$(curl -s "http://localhost:8096/health" || true)" == "Healthy" ]] && break
    sleep 1
done
[[ "$(curl -s "http://localhost:8096/health")" == "Healthy" ]] || { docker logs tentacle-smoke-server | tail -40; fail "server not healthy"; }

ffmpeg_path="$(docker exec tentacle-smoke-server sed -n 's:.*<EncoderAppPathDisplay>\(.*\)</EncoderAppPathDisplay>.*:\1:p' /config/encoding.xml)"
echo "encoder path: ${ffmpeg_path}"
[[ "$ffmpeg_path" == "/usr/local/bin/tentacle/ffmpeg" ]] || fail "Jellyfin is not using the shim"

api="http://localhost:8096"
auth='MediaBrowser Client="tentacle-smoke", Device="smoke", DeviceId="tentacle-smoke", Version="1"'
curl -sf -X POST "$api/Startup/Configuration" -H 'Content-Type: application/json' \
    -d '{"UICulture":"en-US","MetadataCountryCode":"US","PreferredMetadataLanguage":"en"}' >/dev/null
curl -sf "$api/Startup/User" >/dev/null
curl -sf -X POST "$api/Startup/User" -H 'Content-Type: application/json' -d '{"Name":"smoke","Password":"smoke"}' >/dev/null
curl -sf -X POST "$api/Startup/Complete" >/dev/null
token="$(curl -sf -X POST "$api/Users/AuthenticateByName" -H "Authorization: $auth" -H 'Content-Type: application/json' \
    -d '{"Username":"smoke","Pw":"smoke"}' | jq -r .AccessToken)"
[[ -n "$token" && "$token" != null ]] || fail "could not authenticate"
h="Authorization: ${auth}, Token=\"${token}\""

plugin="$(curl -sf "$api/Plugins" -H "$h" | jq -c '.[] | select(.Name=="Tentacle") | {Name,Version,Status}')"
echo "plugin: ${plugin}"
[[ "$(jq -r .Status <<<"$plugin")" == "Active" ]] || fail "plugin not Active"
pages="$(curl -sf "$api/web/ConfigurationPages?enableInMainMenu=true" -H "$h" | jq -c '[.[] | select(.Name=="Tentacle") | {Name,DisplayName,MenuIcon}]')"
echo "menu pages: ${pages}"
[[ "$pages" == *Tentacle* ]] || fail "dashboard page not in main menu"
curl -sf "$api/web/ConfigurationPage?name=Tentacle" -H "$h" | grep 'TentacleConfigPage' >/dev/null || fail "page not served"
docker exec tentacle-smoke-server sh -c "grep -h 'Tentacle .* started' /config/log/*.log" || fail "hosted service did not start"

echo "== worker"
docker run -d --name tentacle-smoke-worker --network "$net" -e PUID=1000 -e PGID=1000 \
    -e TENTACLE_BROKER_URL=wss://tentacle-smoke-server:8097 -e TENTACLE_NODE_NAME=smoke-worker tentacle-dev:worker >/dev/null
# No token: the agent starts, tells s6 it is ready and idles as "unconfigured"
# (registration is covered by m1-e2e.sh).
for _ in $(seq 60); do
    docker exec tentacle-smoke-worker cat /run/tentacle/agent.status 2>/dev/null | grep unconfigured >/dev/null && break
    sleep 1
done
docker exec tentacle-smoke-worker cat /run/tentacle/agent.status | grep unconfigured >/dev/null || { docker logs tentacle-smoke-worker | tail -30; fail "agent not up"; }
docker exec tentacle-smoke-worker pgrep -f /usr/bin/jellyfin >/dev/null && fail "worker runs Jellyfin"
docker exec tentacle-smoke-worker pgrep -u abc -f 'tentacle agent' >/dev/null || fail "agent not running as abc"
docker exec tentacle-smoke-worker /usr/local/bin/tentacle/ffmpeg -version | head -1
docker logs tentacle-smoke-worker 2>&1 | grep -E '\[tentacle\]|tentacle:' | head -5
docker stop -t 20 tentacle-smoke-worker >/dev/null
docker logs tentacle-smoke-worker 2>&1 | grep -E 'agent stop' >/dev/null || fail "agent did not stop cleanly on SIGTERM"
echo "PASS: M0 smoke"
