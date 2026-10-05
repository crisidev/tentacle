# Architecture

How Tentacle moves Jellyfin's ffmpeg jobs to other machines, and why it is built the
way it is. The [README](../README.md) and the [other docs](README.md) cover installing and running it.

## In short

```
Jellyfin ──spawns──▶ shim (ffmpeg → tentacle)
   │  plugin: broker   ◀── unix socket ──┘ │  local: exec the real ffmpeg
   │                                       │
   └─ :8097 wss ◀── control + one WebSocket per job ── tentacle agent ── sandbox → ffmpeg
```

1. The server mod points Jellyfin's ffmpeg at **the shim**, the `tentacle` binary under
   another name. Information queries and Jellyfin's startup probes go straight to the
   real ffmpeg.
2. For a real job, the shim asks **the broker**, which runs inside the plugin on its own
   Kestrel, where it should run. No answer within 2 s, or no broker at all: it runs
   locally.
3. **Locally**, the shim *becomes* ffmpeg (`execve`), so Jellyfin's process handling is
   exactly as without Tentacle.
4. **Remotely**, the broker assigns the job to a tentacle's **agent**, which opens a
   WebSocket for it and starts ffmpeg in a Landlock sandbox. The shim relays stdin byte
   by byte, stdout and stderr unbuffered, signals, and finally the exit code. To
   Jellyfin it is just ffmpeg.
5. Kill any piece and its connections close: the job dies everywhere, and Jellyfin's
   next request places it again.

## Constraints from Jellyfin

Jellyfin 12.1 needs a real local process for ffmpeg. `TranscodingJob` holds a
`Process`, writes to its stdin and kills it, so a stand-in binary is unavoidable. For
Tentacle, "native" means that the scheduling, the registry, the configuration, the
metrics and the UI all live in a Jellyfin plugin. The binary Jellyfin starts is only a
thin local proxy for the remote process.

- **The ffmpeg path.** Jellyfin takes it from `--ffmpeg`/`JELLYFIN_FFMPEG`, then from
  `encoding.xml`, then from `PATH`. Neither the dashboard nor a plugin can change it,
  so the LinuxServer mod sets `FFMPEG_PATH` to the shim. ffprobe is resolved as the
  sibling file `ffprobe`.
- **Startup probes.** Jellyfin validates ffmpeg (`-version`, `-encoders`, lavfi test
  encodes...) before plugins start. The shim answers these on its own by running the
  real binary.
- **The transcode I/O contract.** stdin and stderr are redirected, stdout is not.
  Progress lines on stderr are split on `\r`. stdin carries bare `p`/`u`/`c` bytes
  (throttling); stop is `q\n`, then a SIGKILL 5 s later.
- **Other ffmpeg users.** Probes and keyframe extraction (ffprobe, output on stdout),
  images and trickplay (files under `$TMPDIR/jellyfin`; Jellyfin watches the growing
  jpg count as the sign trickplay is alive), subtitle and attachment extraction (one
  variant relies on the working directory), audio normalization, Live TV.
- **Paths.** Jellyfin hands ffmpeg absolute paths, so a tentacle must see the same
  file at the same path: the transcode directory, `$TMPDIR/jellyfin`, `<cache>/concat`,
  `<data>/subtitles`, `<data>/attachments` (also the fonts directory) and the media.
- **Plugins.** `IPluginServiceRegistrator` registers into the web host's container, so
  hosted services work. Jellyfin's own port intercepts every WebSocket upgrade, so the
  broker runs **its own Kestrel**: a unix socket for shims and TCP 8097 for agents.
- **LinuxServer mods** are applied before the s6 service database is compiled. They can
  add or overwrite files but not delete them, so the worker role overwrites
  `svc-jellyfin/run`, and readiness is signalled on fd 3.

## Overview

```
Jellyfin ──spawns──▶ shim (/usr/local/bin/tentacle/ffmpeg)
   │  plugin: broker (own Kestrel)  ◀── unix socket ──┘ │  local: execve the real ffmpeg
   │   ├─ scheduler / registry / verifier               │         (socket inherited; EOF = done)
   │   └─ :8097 wss ◀── control WS + one WS per job ── tentacle agent (one per worker)
   └─ /metrics, dashboard, admin API                     └─ tentacle sandbox → real ffmpeg (own pgid)
shared storage: transcodes, temp, subtitles, attachments, concat, media; same paths everywhere
```

**A job lives exactly as long as its connections.** The shim's socket, the job's
WebSocket and the agent's control session are all tied to the job, so there is no
persistent state to go stale.

- If Jellyfin SIGKILLs the shim, the kernel closes its socket, the broker cancels the
  job, and the agent SIGKILLs the job's process group.
- If a tentacle dies, the shim exits non-zero. Jellyfin's next segment request restarts
  the transcode, which is placed somewhere else.
- No job outlives its agent's control session: a missed heartbeat (15 s) kills them all.

## Projects

| Project | What it is |
|---|---|
| `Tentacle.Protocol` | Frame codec, messages (source-generated JSON), argv analysis, hardware detection. NativeAOT-compatible, shared by everything. |
| `Tentacle.Broker` | The broker: Kestrel host, scheduler, node registry and verification, job history, metrics. ASP.NET Core only, no Jellyfin reference. |
| `Jellyfin.Plugin.Tentacle` | The thin adapter: plugin entry point, configuration, admin API, dashboard page, encoding settings and paths from Jellyfin, job attribution. |
| `Tentacle.Cli` | The NativeAOT `tentacle` binary. As `ffmpeg`/`ffprobe` (by argv[0]) it is the shim; `tentacle agent` is the worker; `tentacle sandbox` confines a job; `tentacle agent-status` is the readiness probe. |

## Wire protocol

**Frames** are `u8 type | payload`. On the unix socket each frame has a `u32le` length
prefix (at most 1 MiB); on a WebSocket one binary message is one frame. Data payloads
are at most 64 KiB; control payloads are JSON.

**Endpoints.**

- Shim: the unix socket `$TENTACLE_SOCKET` (default `/run/tentacle/broker.sock`). The
  broker checks with `SO_PEERCRED` that the peer runs as its own uid.
- Agents, on the agent port (8097, TLS by default):
  - `GET /tentacle/v1/control`: the control WebSocket, `Authorization: Bearer <token>`,
    compared in constant time.
  - `GET /tentacle/v1/job/{jobId}`: one WebSocket per job, with the token and a one-time
    job key.

**Control messages.** The agent sends `Hello` (protocol range, versions, node name,
ffmpeg build, machine, GPU and measured capabilities, slots). The broker answers
`Welcome` (protocol, heartbeat, the roots to prove with their nonces, the hardware
self-test, the expected ffmpeg) or `Reject`. Then: `SelfTestResult`, `State`,
`ConfigUpdate`, `Ping`/`Pong`, `Assign`, `Cancel`, `Drain`.

**Job frames.** `Start`, `Placement`, `Started`, `Stdin`, `StdinEof`, `Stdout`,
`Stderr`, `StdoutClosed`, `Signal`, `Exit`, `Error`.

**Why one WebSocket per job** rather than multiplexing on the control session:

- closing the connection kills the job, with nothing to clean up;
- TCP gives end-to-end backpressure (the shim keeps a bounded queue per stream, in
  place of the pipe buffer);
- one heavy stdout stream cannot delay another job's `q` or the heartbeats.

**Versioning.** An integer protocol version, negotiated at connect. JSON fields are
only ever added. A shim on an unsupported version runs locally.

## The shim

1. Read argv[0] from `/proc/self/cmdline` (NativeAOT resolves symlinks, so the usual
   API would not show it). The real binaries are in `$TENTACLE_REAL_DIR` (default
   `/usr/lib/jellyfin-ffmpeg`). Only raw fds 0-2 are used, never `Console`.
2. Exec the real binary straight away when `TENTACLE_DISABLE=1`, for information
   queries (`-version`, `-encoders`, `-hwaccels`...) and when there is no real input
   (lavfi or null only). This covers all of Jellyfin's startup probes.
3. Connect to the broker socket (250 ms). If the socket is missing, refuses or times
   out, exec the real binary: that is what happens while Jellyfin starts, before the
   broker exists.
4. Send `Start` (argv, cwd, an allowlist of environment variables, pid) and wait up to
   2 s for a `Placement`; on timeout, exec the real binary.
5. **Local:** apply the nice value, reset signal dispositions, and exec the real ffmpeg
   with the broker socket inherited. Jellyfin's `Process` *is* now ffmpeg, so `q`,
   `Kill()` and the pipes behave exactly as without Tentacle. The broker sees EOF when
   it exits and reads its real exit code.
6. **Remote:** relay stdin byte by byte, stdout and stderr byte-exact and unbuffered,
   forward SIGTERM/INT/HUP, and exit with ffmpeg's exit code (128 + signal for a signal
   death). If the job is lost, print `[tentacle] job <id> on <node> lost: <reason>` and
   exit 1.

## The agent

1. Find the GPUs (`/sys/class/drm`, vendor, PCI id, driver, model) and measure what
   each can do with one-second hardware encodes (H.264, HEVC, HEVC 10-bit, AV1) and
   decodes (also VP9). Register one tentacle per usable GPU, or one CPU tentacle.
2. Signal readiness to s6 on fd 3 and write the status file `agent-status` reads.
3. Dial the broker with backoff (0.5 s to 30 s, with jitter). Exchange `Hello` and
   `Welcome`, prove the shared roots and run the hardware self-test, then wait for
   `State`.
4. On `Assign`: dial the job's WebSocket, and start `tentacle sandbox <roots> -- ffmpeg
   ...` in its own process group, with `PDEATHSIG`. The sandbox confines itself with
   Landlock and execs ffmpeg. Pump stdio in 64 KiB chunks and send `Exit` at the end.
5. `Cancel`, a closed job socket, or a lost control session: `kill(-pgid, SIGKILL)` at
   once.
6. On SIGTERM: send `Drain`, give running jobs `TENTACLE_DRAIN_SECONDS`, SIGKILL what is
   left and exit 0. The killed jobs' shims exit non-zero and Jellyfin restarts those
   transcodes elsewhere; players stall for a few seconds.

## Verification

When a tentacle registers, every `VerifyIntervalMinutes` and whenever the encoding
settings change, it proves what it shares:

- **Writable roots** (transcodes, temp, subtitles, attachments, concat): the broker
  writes a nonce file, the agent reads it and writes an answer back, and the broker
  reads that (without following links, at most 128 bytes). This also catches
  mismatched uids.
- **Read-only roots** (libraries, fonts): the agent stats a file the server sees.
- **ffmpeg build:** a different major.minor makes the tentacle Incompatible; a
  different patch level makes it Degraded (Incompatible with "strict ffmpeg version").
- **Hardware:** a self-test built from the server's encoding settings (QSV, VAAPI,
  NVENC). It only runs on tentacles with a GPU of the server's vendor; the others skip
  it and take jobs that use no hardware.

States: Verifying, Ready, Degraded (still used where it can be), Incompatible,
Draining, CoolingDown (after 3 infrastructure failures in a row: 1 min, doubling up to
15), DetectOnly (`TENTACLE_MAX_JOBS=0`).

## Scheduling

`ArgvAnalysis` reads each command line: its kind, every path it touches (inputs,
outputs, `-hls_segment_filename`, `-attach`, the working directory, file names inside
filters such as `subtitles=...:fontsdir=...`), URLs, devices and the hardware family it
uses (`-init_hw_device qsv`, `h264_nvenc`, `driver=iHD`...). Loopback URLs keep a job
local.

| Kind | Where | Class | Cost |
|---|---|---|---|
| probe, single image, other | local | | |
| transcode | tentacle preferred | interactive | 1 |
| subtitle/attachment extraction | anywhere | interactive | 0.25 |
| trickplay, analysis | tentacle preferred | background | 0.5 |

- A tentacle is eligible when it is Ready (or Degraded but passed what the job needs),
  every path in the job is under a root it proved, and, for a hardware job, it passed
  the self-test with a GPU of the vendor the command line names.
- Among eligible nodes the job goes to the lowest `(load + cost) / weight`; ties rotate
  to the node that took a job least recently.
- Background jobs are capped per node (`TENTACLE_MAX_BACKGROUND_JOBS`), so playback
  always has headroom, and run at the background nice value.
- There is no queue. A job no tentacle can take runs on the server; with "This server
  runs jobs: never" it waits up to 10 s for a tentacle and is then refused.
- With server slots ("worker" mode) the server is one more node in the same contest.
- `DryRun` placement records where each job would have gone and runs everything
  locally.

## Job attribution

Jellyfin registers a transcoding job before it starts ffmpeg. When a shim connects, the
plugin maps the job's output path back to the transcoding job, so the dashboard, the job
API and the log line show the user, app, device and library item of each job.

## The LinuxServer mod

One build, two tags, each a single layer (the mod loader applies only the first):

- **common:** `/usr/local/bin/tentacle/tentacle` with `ffmpeg` and `ffprobe` symlinks,
  and an init oneshot that creates `/run/tentacle` for `abc`.
- **server:** sets `FFMPEG_PATH` to the shim and `TENTACLE_SOCKET`, and installs the
  bundled plugin into `/config/data/plugins/Tentacle_<version>` (unless
  `TENTACLE_INSTALL_PLUGIN=false`), removing older copies. The plugin, shim and agent
  therefore always come from the same build.
- **worker:** replaces `svc-jellyfin/run` with `tentacle agent`, so Jellyfin never starts.

## Not done yet: other GPU vendors for hardware jobs

Jellyfin builds each ffmpeg command line for one hardware acceleration type, so a QSV
command line cannot run on an NVIDIA GPU. Today a hardware job only goes to a tentacle
with the server's GPU vendor; tentacles with other GPUs (or none) take everything that
uses no hardware.

Translating command lines between vendors would mean reimplementing Jellyfin's
`EncodingHelper` (device setup, hardware filters, tone mapping, subtitle overlays).
The plan instead is to have Jellyfin build the command line for the chosen node:

- choose the node for each playback at its first streaming request (a middleware keyed
  on the `PlaySessionId`);
- during that request, wrap `IServerConfigurationManager` and `IMediaEncoder` so
  `EncodingHelper` sees the chosen node's encoding profile: hardware type, device,
  decoding codecs, tone mapping, the vendor's device flags;
- use a placeholder device path the agent swaps for its real render node;
- fail over within the same vendor family.

Everything Jellyfin needs for this already comes through dependency injection in 12.1.
Contributions and testing on NVIDIA and AMD hardware are welcome.
