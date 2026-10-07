# Installation

## Requirements

* Jellyfin **10.11** or **12.1 and later** on Linux on the server: any install (the official image, the
  [LinuxServer image](https://docs.linuxserver.io/images/docker-jellyfin/), distro
  packages), amd64 or arm64.
* The LinuxServer image of the same Jellyfin version on every tentacle
  (`linuxserver/jellyfin:10.11...`, `:12.2...`), or the `tentacle` binary with the same
  jellyfin-ffmpeg build.
* Linux **5.13** or newer on the tentacles for job confinement (Landlock), **6.12** or
  newer for all of it ([details](security.md)); recommended, not required.
* Shared storage for the media and Jellyfin's working directories, see
  [Shared directories](configuration.md#shared-directories).
* The tentacles must reach the server on TCP **8097**.

## What you install

Every release ships, from the same build:

* **The Jellyfin plugin**, for the server, from the plugin repository
  `https://github.com/crisidev/tentacle/releases/latest/download/manifest.json`, in
  two builds: one for Jellyfin 12.1 and later (version `X.Y.Z.1`) and one for 10.11
  (`X.Y.Z.0`). Jellyfin installs the one it can load by itself, and offers the 12 build
  as an update once you move from 10.11 to 12. It
  carries the broker, the dashboard page and the shim (the `tentacle` binary for amd64
  and arm64), which it installs into `<data>/tentacle/bin` and puts in front of
  Jellyfin's ffmpeg.
* **`crisidev/tentacle:worker-<version>`**, a [LinuxServer docker mod](https://github.com/linuxserver/docker-mods)
  on Docker Hub, for each tentacle: the Jellyfin image runs the Tentacle agent instead
  of Jellyfin. Pin the digest from the [release notes](https://github.com/crisidev/tentacle/releases)
  (`crisidev/tentacle:worker-2.0.0@sha256:...`): a tag can be overwritten, a digest
  cannot. `:worker-main` follows the main branch.

Keep the plugin and the worker mod on the same version.

## The plugin

1. In **Dashboard → Plugins → Repositories**, add
   `https://github.com/crisidev/tentacle/releases/latest/download/manifest.json`.
2. In **Dashboard → Plugins → Catalog**, install **Tentacle**, then restart Jellyfin.
3. **Dashboard → Tentacle** shows the server card with **Shim**: the path of the shim
   Jellyfin now runs. If it says *Not in use*, the red notice above says why; see
   [How Jellyfin runs the shim](#how-jellyfin-runs-the-shim).

Without internet access from Jellyfin, unzip `tentacle-plugin_<version>_jellyfin-12.zip`
(or `_jellyfin-10.11.zip`) from the [release](https://github.com/crisidev/tentacle/releases)
into `<config>/plugins/Tentacle_<version>.1/` (`.0` for 10.11) (`/config/data/plugins/` in the LinuxServer
image) and restart.

## How Jellyfin runs the shim

Jellyfin picks its ffmpeg once at startup and offers plugins no way to change it. The
plugin installs the shim at `<data>/tentacle/bin/ffmpeg` (in the LinuxServer image
`/config/data/data/tentacle/bin/ffmpeg`; the dashboard shows the path) every time it
starts, before Jellyfin looks for ffmpeg, and then Jellyfin runs the shim one of two
ways:

* **By itself (the default).** Jellyfin validates its own ffmpeg as usual, then the
  plugin swaps the shim in. Nothing to configure, but it reaches into Jellyfin's
  internals: should a Jellyfin release change them, the dashboard says the shim is not
  in use and every job runs on the server, as without Tentacle.
* **Through Jellyfin's setting (recommended for production).** Set Jellyfin's ffmpeg
  path to the shim: `JELLYFIN_FFMPEG=<data>/tentacle/bin/ffmpeg` (the official image,
  and `/etc/default/jellyfin` for packages), `FFMPEG_PATH=...` in the LinuxServer
  image, or `--ffmpeg=...`. This is the supported way and survives any Jellyfin update.
  The LinuxServer image ignores `FFMPEG_PATH` while the file does not exist, so there
  it takes effect from the start after the plugin first installed the shim.
  If the real ffmpeg is not in `/usr/lib/jellyfin-ffmpeg`, also set `TENTACLE_REAL_DIR`
  to its directory.

Either way, information queries and Jellyfin's own checks go straight to the real
ffmpeg, and `ffprobe` stays Jellyfin's.

## Docker Compose

1. **Server.** Open port 8097 on your Jellyfin container, share its working
   directories, and [install the plugin](#the-plugin)
   ([examples/compose/server.yaml](../examples/compose/server.yaml)):

   ```yaml
   services:
     jellyfin:
       image: linuxserver/jellyfin:version-12.1ubu2604
       environment:
         TMPDIR: /config/cache/temp      # trickplay writes under $TMPDIR/jellyfin: share it
         # Optional, the supported way: run the shim the plugin installs.
         # FFMPEG_PATH: /config/data/data/tentacle/bin/ffmpeg
       ports:
         - 8096:8096
         - 8097:8097                     # tentacles connect here
       volumes:
         - ./config:/config
         - /mnt/shared/jellyfin/transcodes:/config/cache/transcodes
         - /mnt/shared/jellyfin/temp:/config/cache/temp
         - /mnt/shared/jellyfin/subtitles:/config/data/data/subtitles
         - /mnt/shared/jellyfin/attachments:/config/data/data/attachments
         - /mnt/shared/media:/data/media
   ```

2. Start it, install the plugin, restart, and open **Dashboard → Tentacle**. Expand **Connect a tentacle**: it has
   the token, the certificate fingerprint and a ready-made environment for the workers.

3. **Tentacles.** On each worker, the same image with the worker mod and the same
   shared directories at the same paths
   ([examples/compose/worker.yaml](../examples/compose/worker.yaml)):

   ```yaml
   services:
     tentacle:
       image: linuxserver/jellyfin:version-12.1ubu2604   # same tag as the server
       environment:
         PUID: "1000"                                    # same uid as the server
         PGID: "1000"
         DOCKER_MODS: crisidev/tentacle:worker-2.0.0
         TENTACLE_BROKER_URL: wss://jellyfin.lan:8097
         TENTACLE_BROKER_FINGERPRINT: "AB:CD:...:EF"     # from the dashboard
         TENTACLE_TOKEN_FILE: /run/secrets/tentacle-token
         TENTACLE_NODE_NAME: worker1
         TENTACLE_ROOTS: /data/media:/config/cache/transcodes:/config/cache/temp:/config/data/data/subtitles:/config/data/data/attachments
       stop_grace_period: 35s
       volumes:
         - /mnt/shared/jellyfin/transcodes:/config/cache/transcodes
         - /mnt/shared/jellyfin/temp:/config/cache/temp
         - /mnt/shared/jellyfin/subtitles:/config/data/data/subtitles
         - /mnt/shared/jellyfin/attachments:/config/data/data/attachments
         - /mnt/shared/media:/data/media:ro
         - ./tentacle-token:/run/secrets/tentacle-token:ro
       devices:
         - /dev/dri:/dev/dri                             # optional: its GPU
   ```

4. The tentacle shows up on the dashboard as **Verifying**, then **Ready** once it has
   proved its directories and passed the hardware test. Set **Where jobs run** to
   **Active** (it starts in [shadow mode](#trying-it-safely-shadow-mode)) and play
   something: the job list says where it ran and why.

## Kubernetes

[examples/kubernetes/](../examples/kubernetes/) has a complete setup:

* [`tentacle.yaml`](../examples/kubernetes/tentacle.yaml): the token Secret, a Service for
  the broker port, a NetworkPolicy that only lets tentacle pods reach it, and the
  workers as a DaemonSet (`TENTACLE_NODE_NAME` from `spec.nodeName`, the token as a
  mounted file, `agent-status` as readiness probe, drain-aware grace periods);
* [`jellyfin-patch.yaml`](../examples/kubernetes/jellyfin-patch.yaml): what the Jellyfin
  Deployment needs (`TMPDIR`, the shim as its ffmpeg, the token, port 8097). Install
  the plugin from the repository as above.

Workers mount an `emptyDir` as `/config` with the shared directories from the same
volume as the server's, by `subPath`. A rolling update of the DaemonSet drains one
tentacle at a time.

## Without the LinuxServer image

* **Server:** nothing special: the plugin works in any Jellyfin 10.11 or 12.1+ on Linux.
* **Tentacle:** the worker mod is a convenience. Run `tentacle_<version>_linux-<arch>`
  from the release as `tentacle agent` as the same uid as Jellyfin, with the same
  jellyfin-ffmpeg build in `TENTACLE_REAL_DIR` and the environment described below.

## Trying it safely: shadow mode

A new install starts with **Where jobs run: Shadow**. Every job runs on the server as
before, and the dashboard and metrics record where it *would* have gone (`would_run_on`
in the log line). Switch to **Active** when that looks right. **Off** runs
everything on the server.

## Uninstalling

If you set `JELLYFIN_FFMPEG` or `FFMPEG_PATH` to the shim, remove it first. Then
uninstall the plugin in Dashboard → Plugins and restart: Jellyfin goes back to its own
ffmpeg. `<data>/tentacle` (the shim, the socket, the certificate) can be deleted.

## Upgrading from the server mod (1.x)

Tentacle 1.0 delivered the plugin through a `crisidev/tentacle:server-<version>` mod.
Remove it from `DOCKER_MODS`, add the plugin repository, and update **Tentacle** from
the catalog (the installed copy keeps its settings). If you set `TENTACLE_SOCKET` or a
socket path in the settings for the mod, clear them.
