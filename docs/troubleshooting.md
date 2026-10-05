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
  every time; removing the server mod takes Tentacle out completely.
