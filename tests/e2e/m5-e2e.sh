#!/usr/bin/env bash
# M5 acceptance (security): real Jellyfin 12.1 + the AOT agent.
#   - the agent port speaks TLS by default (self-signed); agents pin its fingerprint
#   - a wrong fingerprint, or plain ws://, never registers
#   - token rotation: agents keep working on the old token during the overlap, a
#     token file is re-read on reconnect, an agent left on the old token is dropped
#   - certificate files (cert-manager style) with TENTACLE_CA_FILE on the agent
#   tests/e2e/m5-e2e.sh [version]   (needs scripts/build.sh mod first)
set -euo pipefail
version="${1:-$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$(dirname "$0")/../../Directory.Build.props")}"
base="linuxserver/jellyfin:version-12.1ubu2604"
p=tentacle-m5
srv=$p-server
plugin=3a65d525-990c-4f73-89e9-a0d1500a53d2
workers=(pinned wrongpin plain rotated stale ca)
fail() {
    echo "FAIL: $*" >&2
    docker logs "$srv" 2>&1 | grep -i tentacle | tail -15 >&2 || true
    for w in "${workers[@]}"; do docker logs "$p-$w" 2>&1 | grep '\[tentacle\]' | tail -4 | sed "s/^/$w: /" >&2 || true; done
    exit 1
}
cleanup() {
    docker rm -f "$srv" "${workers[@]/#/$p-}" >/dev/null 2>&1 || true
    docker network rm "$p" >/dev/null 2>&1 || true
    docker volume rm "$p-config" >/dev/null 2>&1 || true
}
[[ "${KEEP:-0}" == 1 ]] || trap cleanup EXIT
cleanup

for role in server worker; do
    printf 'FROM %s\nCOPY --from=tentacle:%s-%s / /\nRUN mkdir -p /config/data/data /config/cache && chown -R 1000:1000 /config\n' "$base" "$role" "$version" | docker build -q -t "tentacle-dev:${role}" - >/dev/null
done
docker network create "$p" >/dev/null
docker volume create "$p-config" >/dev/null
env=(-e PUID=1000 -e PGID=1000 -e TZ=Etc/UTC)

api=http://localhost:8096
curl() { docker exec "$srv" curl "$@"; }
start_server() {
    docker run -d --name "$srv" --network "$p" -v "$p-config:/config" "${env[@]}" "$@" tentacle-dev:server >/dev/null
    for _ in $(seq 120); do [[ "$(curl -s $api/health || true)" == Healthy ]] && break; sleep 1; done
    [[ "$(curl -s $api/health)" == Healthy ]] || fail "server not healthy"
}

echo "== server (TLS by default)"
docker run --rm -v "$p-config:/config" alpine sh -c 'mkdir -p /config/data/data /config/cache && chown -R 1000:1000 /config'
start_server
auth='MediaBrowser Client="tentacle-e2e", Device="e2e", DeviceId="tentacle-m5", Version="1"'
curl -sf -X POST "$api/Startup/Configuration" -H 'Content-Type: application/json' -d '{"UICulture":"en-US","MetadataCountryCode":"US","PreferredMetadataLanguage":"en"}' >/dev/null
curl -sf "$api/Startup/User" >/dev/null
curl -sf -X POST "$api/Startup/User" -H 'Content-Type: application/json' -d '{"Name":"e2e","Password":"e2e"}' >/dev/null
curl -sf -X POST "$api/Startup/Complete" >/dev/null
login() {
    at="$(curl -sf -X POST "$api/Users/AuthenticateByName" -H "Authorization: $auth" -H 'Content-Type: application/json' -d '{"Username":"e2e","Pw":"e2e"}' | jq -r .AccessToken)"
    h="Authorization: ${auth}, Token=\"${at}\""
}
login
get() { curl -sf "$api/$1" -H "$h"; }
status() { get Tentacle/Status; }
status | jq -c '{Tls,TlsSelfSigned,TlsFingerprint,TlsNotAfter,StartError}'
[[ "$(status | jq -r .Tls)" == true && "$(status | jq -r .TlsSelfSigned)" == true ]] || fail "no self-signed TLS by default"
fp="$(status | jq -r .TlsFingerprint)"
token="$(get "Plugins/$plugin/Configuration" | jq -r .Token)"
[[ -n "$token" ]] || fail "no token"

# A worker; extra docker args after the name. Files go in with docker cp before
# the start (no bind mounts in CI's rootless docker).
worker() { docker create --name "$p-$1" --network "$p" "${env[@]}" -e TENTACLE_NODE_NAME="$1" "${@:2}" tentacle-dev:worker >/dev/null; }
put() { printf '%s' "$3" > "/tmp/$p-file"; docker cp -q "/tmp/$p-file" "$p-$1:$2"; rm -f "/tmp/$p-file"; }
state() { get Tentacle/Nodes | jq -r --arg n "$1" '.[] | select(.Name==$n) | .State'; }
node() { get Tentacle/Nodes | jq -c --arg n "$1" '.[] | select(.Name==$n) | {Name,State,UsesPreviousToken}'; }
wait_for() { for _ in $(seq "$1"); do eval "$2" && return 0; sleep 1; done; return 1; }
agent_log() { docker logs "$p-$1" 2>&1 | grep '\[tentacle\]'; }

echo "== 1. pinned fingerprint registers; wrong fingerprint and plain ws:// do not"
worker pinned -e TENTACLE_BROKER_URL="wss://$srv:8097" -e TENTACLE_BROKER_FINGERPRINT="$fp" -e TENTACLE_TOKEN="$token"
worker wrongpin -e TENTACLE_BROKER_URL="wss://$srv:8097" -e TENTACLE_BROKER_FINGERPRINT="$(printf '0%.0s' $(seq 64))" -e TENTACLE_TOKEN="$token"
worker plain -e TENTACLE_BROKER_URL="ws://$srv:8097" -e TENTACLE_TOKEN="$token"
docker start "$p-pinned" "$p-wrongpin" "$p-plain" >/dev/null
wait_for 60 '[[ "$(state pinned)" =~ Ready|Degraded ]]' || fail "pinned agent did not register"
wait_for 30 'agent_log wrongpin | grep "is not TENTACLE_BROKER_FINGERPRINT" >/dev/null' || fail "wrong fingerprint not reported"
wait_for 30 'agent_log plain | grep "must be wss://" >/dev/null' || fail "plain ws:// not hinted"
agent_log wrongpin | tail -1
agent_log plain | tail -1
[[ -z "$(state wrongpin)$(state plain)" ]] || fail "an untrusted connection registered"
docker exec "$p-pinned" /usr/local/bin/tentacle/tentacle agent-status | grep 'tls=pinned' >/dev/null || fail "agent-status lacks tls=pinned"

echo "== 2. token rotation: token file re-read on reconnect; an agent left on the old token is dropped"
worker rotated -e TENTACLE_BROKER_URL="wss://$srv:8097" -e TENTACLE_BROKER_FINGERPRINT="$fp" -e TENTACLE_TOKEN_FILE=/config/token
put rotated /config/token "$token"
worker stale -e TENTACLE_BROKER_URL="wss://$srv:8097" -e TENTACLE_BROKER_FINGERPRINT="$fp" -e TENTACLE_TOKEN="$token"
docker start "$p-rotated" "$p-stale" >/dev/null
wait_for 60 '[[ "$(state rotated)" =~ Ready|Degraded && "$(state stale)" =~ Ready|Degraded ]]' || fail "rotation agents did not register"

rotation="$(curl -sf -X POST "$api/Tentacle/Token/Rotate?overlapMinutes=0.5" -H "$h")"
new="$(jq -r .Token <<<"$rotation")"
echo "rotated: old token valid until $(jq -r .PreviousTokenExpires <<<"$rotation")"
[[ -n "$new" && "$new" != "$token" ]] || fail "no new token"
sleep 7
node rotated; node stale
[[ "$(get Tentacle/Nodes | jq '[.[] | select(.UsesPreviousToken)] | length')" == 3 ]] || fail "agents on the old token not flagged during the overlap"

# The tentacles get the new token: the file one through its file, "stale" never.
docker exec "$p-rotated" sh -c "printf '%s' '$new' > /config/token"
wait_for 60 '[[ -z "$(state stale)" ]]' || fail "agent on the expired token not dropped"
wait_for 60 '[[ "$(state rotated)" =~ Ready|Degraded ]] && [[ "$(get Tentacle/Nodes | jq -r ".[] | select(.Name==\"rotated\") | .UsesPreviousToken")" == false ]]' \
    || fail "token-file agent did not come back on the new token"
wait_for 60 'agent_log stale | grep "rejected the token (401)" >/dev/null' || fail "stale agent not refused"
node rotated
agent_log stale | grep 401 | tail -1
docker logs "$srv" 2>&1 | grep 'Tentacle stale authenticated with a token that is no longer valid' >/dev/null || fail "server did not log the retired token"

echo "== 3. certificate files (cert-manager style) + TENTACLE_CA_FILE on the agent"
docker exec -u abc "$srv" sh -c 'set -e; mkdir -p /config/tls && cd /config/tls
    openssl req -x509 -newkey ec -pkeyopt ec_paramgen_curve:prime256v1 -nodes -days 30 -subj "/CN=Tentacle e2e CA" \
        -addext basicConstraints=critical,CA:TRUE -addext keyUsage=critical,keyCertSign -keyout ca.key -out ca.crt 2>/dev/null
    openssl req -newkey ec -pkeyopt ec_paramgen_curve:prime256v1 -nodes -subj "/CN=tentacle-m5-server" -keyout tls.key -out tls.csr 2>/dev/null
    printf "subjectAltName=DNS:tentacle-m5-server\nextendedKeyUsage=serverAuth\n" > ext
    openssl x509 -req -in tls.csr -CA ca.crt -CAkey ca.key -CAcreateserial -days 7 -extfile ext -out tls.crt 2>/dev/null'
docker cp -q "$srv:/config/tls/ca.crt" "/tmp/$p-ca.crt"
docker rm -f "$srv" >/dev/null
start_server -e TENTACLE_TLS_CERT=/config/tls/tls.crt -e TENTACLE_TLS_KEY=/config/tls/tls.key
login
status | jq -c '{Tls,TlsSelfSigned,TlsFingerprint,StartError}'
[[ "$(status | jq -r .Tls)" == true && "$(status | jq -r .TlsSelfSigned)" == false ]] || fail "certificate files not used"
worker ca -e TENTACLE_BROKER_URL="wss://$srv:8097" -e TENTACLE_CA_FILE=/config/ca.crt -e TENTACLE_TOKEN="$new"
docker cp -q "/tmp/$p-ca.crt" "$p-ca:/config/ca.crt"
rm -f "/tmp/$p-ca.crt"
docker start "$p-ca" >/dev/null
wait_for 60 '[[ "$(state ca)" =~ Ready|Degraded ]]' || fail "CA-trusting agent did not register"
docker exec "$p-ca" /usr/local/bin/tentacle/tentacle agent-status | grep 'tls=ca' >/dev/null || fail "agent-status lacks tls=ca"
# The self-signed pin no longer matches the new certificate: "pinned" stays out.
wait_for 90 'agent_log pinned | grep "is not TENTACLE_BROKER_FINGERPRINT" >/dev/null' || fail "pinned agent accepted a different certificate"
node ca

echo "PASS: M5 e2e"
