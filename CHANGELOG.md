# Changelog

Every artifact (the plugin, the `tentacle` binary and both mod images) shares one
version. The plugin's own changelog in `build.yaml` carries the latest entry.

## 1.0.0

First public release, on GitHub. Changes since 0.12.0:

- Documentation: a new README, and `docs/` with installation (Docker Compose,
  Kubernetes), configuration, observability, security, troubleshooting and
  architecture; `CONTRIBUTING.md` and `SECURITY.md`.
- Examples: `examples/compose/` and `examples/kubernetes/`.
- The server's card on the dashboard shows its version as "Agent", like the tentacles'.
- Security: a job inherits only the agent's locale, `PATH` and GPU driver variables
  (before, everything but `TENTACLE_*`), so other credentials in a tentacle's
  environment stay out of its reach. Driver names a job carries from the server
  (`LIBVA_DRIVER_NAME`, `LIBVA_DRIVER_NAME_JELLYFIN`) must be bare words: libva puts
  them in a file name, so `../` could have loaded a library from a shared directory.
- Security: when it proves a shared directory, the agent (which is not confined itself)
  resolves symlinks and `..` first, so a server could no longer make it create its reply
  file outside `TENTACLE_ROOTS`, or check whether any file exists through a "sample"
  outside the directory. Paths with `.` or `..` segments never count as shared.
- Security: the shim socket refuses a connection whose uid it cannot read, instead of
  accepting it.
- Security: removed `/tentacle/v1/status` from the agent port: it served the job history
  (users, items, command lines) to anything holding the agent token.
- The dashboard and the agent's log say what each tentacle's kernel confines: files
  from Linux 5.13, TCP listen from 6.7, signals from 6.12.
- CI on GitHub Actions. Releases go to GitHub Releases, with the images on Docker Hub
  (`crisidev/tentacle:server-X.Y.Z`, `crisidev/tentacle:worker-X.Y.Z`).

## 0.12.0

New metric tentacle_server_info{name,role}: this server's name and whether it takes jobs
(worker), only what no tentacle can (backup) or never. The bundled Grafana board treats
the server as a node: named "<name> (server)" in every by-node panel, in "Nodes now" with
its role and slots, and counted in "Nodes taking jobs"; "Transcodes on tentacles" becomes
each node's share ("Transcodes by node"), and slots used are a share of each node's slots.

## 0.11.2

The server's card shows Ready when it takes jobs (Backup only, No transcoding otherwise),
like a tentacle's state.

## 0.11.1

Every node card shows its weight (TENTACLE_WEIGHT for tentacles, the setting for the
server in worker mode).

## 0.11.0

Ties in placement rotate: when nodes are equally loaded (relative to their weight), the
job goes to the one that took a job least recently, the server included. Before, the
server always lost ties and among tentacles the first name always won. The server's card
shows what its GPU can encode and decode, measured like a tentacle's shortly after
Jellyfin starts, and its background slots.

## 0.10.0

The Jellyfin server is a node on the dashboard, next to the tentacles: its own card
(marked "Jellyfin server", with its machine, GPU setting and slot meter), and its name
instead of "this server" in the job lists. Set TENTACLE_NODE_NAME on the server to name it
(the hostname otherwise). New "This server runs jobs" setting: as a backup (as before), as
a worker (competing by load, the former "Jobs this server takes itself"), or never when a
tentacle could. In the last mode a job waits up to 10 s for a tentacle, then is refused:
Jellyfin reports a playback error, the job shows as Refused, and node="none" in the
metrics.

## 0.9.0

Security. Tentacles confine their jobs with Landlock to the directories in TENTACLE_ROOTS
(set it on every tentacle: the server decides each command line, and ffmpeg alone can read
and write any file the worker's user can). The dashboard shows "Jobs confined" per
tentacle. Jobs no longer inherit the agent's TENTACLE_* settings (the token). An agent
refuses a ws:// broker URL unless TENTACLE_ALLOW_PLAIN_WS=true, and always with a
fingerprint or CA file set, instead of sending the token in cleartext. The server reads a
tentacle's verification reply without following links and only its first 128 bytes: a
tentacle could otherwise exhaust its memory or block a thread.

## 0.8.1

New metric tentacle_running_job_start_time_seconds: one series per running ffmpeg job with
its user, app, item, kind and node, for Grafana tables of who is transcoding what where
(the Jellyfin / Overview board). User, app and item are only in it with "Name viewers in
metrics" on (off by default: whoever reads /metrics would see who watches what).

## 0.8.0

This server can take jobs itself: "Jobs this server takes itself" gives it slots, and it
then competes with the tentacles by load, (load + cost) / weight, losing ties; background
jobs get at most half its slots. Every job that could have gone to a tentacle but runs
here counts towards its load. 0 slots (the default) keeps the old behaviour: only what no
tentacle can take. Shown on the dashboard ("This server", job reason "this server was the
least busy") and as tentacle_node_slots{node="local"}. TentaclesUnused now counts only
transcodes that fell back to the server, not ones it won on load. Raspberry Pi tentacles
name their board.

## 0.7.0

Hardware detection and several GPUs per host. Each agent finds its GPUs (vendor, PCI id,
kernel driver, model) and measures what each can do with short hardware encodes (H.264,
HEVC, HEVC 10-bit, AV1) and decodes (also VP9), and registers one tentacle per GPU
(host/renderD129 when a host has several; jobs are pointed at that tentacle's render
node), or a CPU-only tentacle when it finds none. The server's GPU self-test only runs
where it can pass: a tentacle without a GPU or with another vendor's skips it and is not
degraded; it takes the jobs that use no hardware. Hardware jobs only go to a GPU of the
vendor their command line names (h264_qsv, driver=iHD...). TENTACLE_MAX_JOBS=0 makes a
detect-only tentacle that reports and never takes a job; TENTACLE_GPUS picks devices. The
dashboard shows each tentacle's machine, GPU and capabilities; metrics gain
tentacle_node_capability and GPU labels on tentacle_node_info. The mod images are multi-
arch (amd64 and arm64).

## 0.6.0

Jobs are matched to who and what they are for: the Jellyfin user, app and device of a
playback transcode, and the library item of every job (dashboard, job API, tentacle_job
log line). Redesigned dashboard page: summary, running jobs, one block per tentacle with
its checks, jobs in plain words, grouped settings, phone layout; "Check again" shows its
progress; the token section explains an environment-managed token instead of showing a
dead control. Only directories ffmpeg can use are checked: Collections and playlist
folders are skipped, and optional directories missing on the server (concat) are neither
checked nor created.

## 0.5.0

M5. TLS on the agent port by default (self-signed and pinned by fingerprint, or
certificate files reloaded on renewal with a CA file on the agents); token rotation with
an overlap window, sessions dropped when their token retires, token files re-read on
reconnect; fuzzed decoders (nulls in messages now rejected); soak test with chaos; fixed a
pipe leak per job in the agent.

## 0.4.0

M3. Prometheus metrics in Jellyfin's own /metrics (tentacle_jobs_total,
tentacle_placements_total, job durations, running jobs, node state, slots, checks,
versions, broker up); one logfmt tentacle_job line per job; alert rules with promtool
tests and a Grafana board in deploy/; agent env snippet on the dashboard page.

## 0.3.0

M2. Tentacles prove what they share (nonce round-trip for writable roots, stat for
libraries) and run a hardware self-test from the encoding settings; states
Ready/Degraded/Incompatible/Draining/CoolingDown; ffmpeg version gate; subtitle and
attachment extraction, trickplay and analysis placed remotely (background capped and
niced); cooldown after repeated failures; drain on SIGTERM; real exit codes for local
jobs; shim socket restricted to the server's uid.

## 0.2.0

M1. Broker in the plugin (own Kestrel: unix socket for shims, :8097 for agents), placement
Disabled/Shadow/Active, playback transcodes run on the least loaded tentacle that shares
every path, local exec otherwise; worker agent with heartbeat, reconnect and kill-on-
disconnect; dashboard with tentacles and recent jobs.

## 0.1.0

M0 scaffold. Plugin loads with a dashboard page, the shim passes ffmpeg/ffprobe through,
worker agent stub, LinuxServer mod (server/worker).
