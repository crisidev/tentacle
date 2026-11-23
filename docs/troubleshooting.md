# Troubleshooting

* **Every job runs on the server.** Look at **Why there** in the job list, or the
  `reason` label of `tentacle_placements_total`:
  * `dry-run`: placement is in Shadow mode;
  * `path-ineligible:<path>`: no tentacle proved that path. Share it, mount it at the
    same place, or add it to "Also shared";
  * `no-ready-tentacle`, `no-tentacle`: check the tentacles' states and checks on the
    dashboard;
  * `no-hardware-tentacle`: the job uses the GPU and no tentacle has one of that vendor
    that passed the self-test;
  * `all-busy`, `background-slots-full`: raise `TENTACLE_MAX_JOBS` /
    `TENTACLE_MAX_BACKGROUND_JOBS`, or add tentacles.
  * `cross-vendor:<vendor>-<api>`: rebuilt for a tentacle with another vendor's GPU
    (experimental setting, Active). The job's details show the command line Jellyfin
    built and the one that ran. In Shadow the job keeps its usual reason, and its
    details show the command line it would have been rebuilt to. `tentacle_cross_vendor_total{outcome=~"skipped:.*"}` says why a
    transcode was not rebuilt; `no-match` after a Jellyfin upgrade means Jellyfin
    changed how it writes the command line, and the job simply stays as built.
  * `cross-vendor-mismatch`: refused. A shim's command line for a rebuilt transcode was
    not the one the plugin recorded for that output, so neither the tentacle nor this
    server ran it. The server log names the tentacle.
* **A tentacle never shows up.** Its log has `[tentacle]` lines saying why: wrong
  fingerprint, refused token (401), unreachable port 8097, or a `ws://` URL.
* **A tentacle is Degraded.** Expand its checks: a `root:<path>` check that fails means
  the directory is missing, not shared, or not writable as the server's uid; a
  `hardware:<type>` failure means the GPU self-test failed (`/dev/dri` not passed in,
  device permissions, drivers).
* **A tentacle is Incompatible.** Its ffmpeg differs from the server's: run the same
  Jellyfin image tag on both.
* **Trickplay or image extraction fails on tentacles** with "Could not open file
  /tmp/jellyfin/...": set `TMPDIR` to a shared directory on the server.
* **A transcode fails on a tentacle.** Jellyfin's
  `/config/log/FFmpeg.Transcode-*.log` has ffmpeg's output, and the `tentacle_job`
  line has the exit code (237 is usually the GPU, 254 a missing path). Set
  `TENTACLE_DEBUG=1` on the server for the shim's own decisions.
* **Rule it out.** `TENTACLE_DISABLE=1` on the server makes the shim run the real ffmpeg
  every time; uninstalling the plugin (and unsetting `JELLYFIN_FFMPEG` if you pointed it
  at the shim) takes Tentacle out completely.
