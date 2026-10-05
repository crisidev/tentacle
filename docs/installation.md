# Installation

## Requirements

* Jellyfin **12.1**, ideally the [LinuxServer image](https://docs.linuxserver.io/images/docker-jellyfin/)
  (`linuxserver/jellyfin:12.1...`) on the server and on every tentacle.
* Linux **5.13** or newer on the tentacles for job confinement (Landlock), **6.12** or
  newer for all of it ([details](security.md)); recommended, not required.
* Shared storage for the media and Jellyfin's working directories, see
  [Shared directories](configuration.md#shared-directories).
* The tentacles must reach the server on TCP **8097**.

## The images

Tentacle ships as two [LinuxServer docker mods](https://github.com/linuxserver/docker-mods)
on Docker Hub, built from the same release:

* `crisidev/tentacle:server-<version>` for the Jellyfin server: puts the shim in front of
  ffmpeg and installs the plugin.
* `crisidev/tentacle:worker-<version>` for each tentacle: the same Jellyfin image runs the
  Tentacle agent instead of Jellyfin.

Pin the digest from the [release notes](https://github.com/crisidev/tentacle/releases)
(`crisidev/tentacle:server-1.0.0@sha256:...`): a tag can be overwritten, a digest cannot.
`:server-main` and `:worker-main` follow the main branch.

## Docker Compose

1. **Server.** Add the mod to your Jellyfin container and open port 8097
   ([examples/compose/server.yaml](../examples/compose/server.yaml)):

   ```yaml
   services:
     jellyfin:
       image: linuxserver/jellyfin:version-12.1ubu2604
       environment:
         DOCKER_MODS: crisidev/tentacle:server-1.0.0
         TMPDIR: /config/cache/temp      # trickplay writes under $TMPDIR/jellyfin: share it
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

2. Start it and open **Dashboard → Tentacle**. Expand **Connect a tentacle**: it has
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
         DOCKER_MODS: crisidev/tentacle:worker-1.0.0
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
  Deployment needs (the server mod, `TMPDIR`, the token, port 8097).

Workers mount an `emptyDir` as `/config` with the shared directories from the same
volume as the server's, by `subPath`. A rolling update of the DaemonSet drains one
tentacle at a time.

## Without the LinuxServer image

The mods are a convenience; the parts are plain files.

* **Server:** install `tentacle-plugin_<version>.zip` from the
  [release](https://github.com/crisidev/tentacle/releases) into Jellyfin's plugin
  directory (or keep the mod's `TENTACLE_INSTALL_PLUGIN=true`, the default). Put the
  `tentacle` binary somewhere with `ffmpeg` and `ffprobe` symlinks to it, start Jellyfin
  with `--ffmpeg=/that/dir/ffmpeg`, and set `TENTACLE_REAL_DIR` to the directory of the
  real ffmpeg (default `/usr/lib/jellyfin-ffmpeg`). The directory `/run/tentacle` must
  exist and belong to Jellyfin's user.
* **Tentacle:** run `tentacle agent` as the same uid as Jellyfin, with the same
  jellyfin-ffmpeg build in `TENTACLE_REAL_DIR` and the environment described below.

## Trying it safely: shadow mode

A new install starts with **Where jobs run: Shadow**. Every job runs on the server as
before, and the dashboard and metrics record where it *would* have gone (`would_run_on`
in the log line). Switch to **Active** when that looks right. **Off** runs
everything on the server.

## Uninstalling

Remove the server mod from `DOCKER_MODS` and restart: Jellyfin goes back to its own
ffmpeg. Remove the plugin from Dashboard → Plugins if you also want its settings gone.
