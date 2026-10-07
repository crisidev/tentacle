# Contributing to Tentacle

Thanks for helping. Bug reports, fixes and new hardware reports are all welcome.

## Reporting a bug

Open an issue with the bug template. The most useful things to include:

- the Tentacle version (Dashboard → Tentacle shows it) and the Jellyfin version;
- the `tentacle_job` lines from Jellyfin's log for the jobs that went wrong, and the
  `[tentacle]` lines from the agent's log;
- for a failed transcode, `/config/log/FFmpeg.Transcode-*.log` from the server;
- the tentacle's card on the dashboard (a screenshot is fine), which shows its checks.

Security problems go through [SECURITY.md](SECURITY.md), not public issues.

## Building

You need Docker. A local .NET 10 SDK is optional: `scripts/dotnet.sh` runs the SDK in a
container when there is none.

```sh
scripts/build.sh test                  # unit tests
scripts/build.sh rules                 # promtool tests for the alert rules
scripts/build.sh                       # everything: tests, AOT binary, plugin, worker mod image
TENTACLE_ARCHES=amd64 scripts/build.sh cli plugin mod   # quicker (the plugin still needs both binaries)
```

The NativeAOT binary is always built in the `sdk:10.0-noble-aot` container (unless
`TENTACLE_AOT_LOCAL=1`), so it links against the standard glibc loader the
LinuxServer image has.

## Testing

Unit tests run with `scripts/build.sh test`. The end-to-end suites start Jellyfin 12.1 (or
the image in `TENTACLE_BASE_IMAGE`, e.g. `lscr.io/linuxserver/jellyfin:version-10.11.11ubu2604`)
with the plugin installed and workers with the worker mod in Docker, after `scripts/build.sh mod`:

```sh
tests/e2e/m0-smoke.sh    # the server starts with the shim; a worker registers
tests/e2e/m1-e2e.sh      # HLS on a tentacle, seek, stop, a tentacle killed mid-stream, metrics
tests/e2e/m2-e2e.sh      # verification states, extraction, burn-in fonts, trickplay, drain
tests/e2e/m5-e2e.sh      # TLS pinning and CA files, refused fingerprints and ws://, token rotation
tests/e2e/soak.sh        # chaos under load, then leak and orphan checks (SOAK_SECONDS)
tests/e2e/hw-e2e.sh      # needs an Intel GPU (/dev/dri/renderD128): QSV and VAAPI
```

`tests/e2e/dev-up.sh` leaves a playground running on http://localhost:18096 (user
`dev`, password `dev`) with three tentacles and a playback session, which is handy
when working on the dashboard page. `tests/e2e/dev-up.sh down` removes it.

CI runs all of these except `hw-e2e.sh` on every pull request. If you change anything
about hardware placement, please run `hw-e2e.sh` on a machine with an Intel GPU, or say
in the pull request that you could not.

## Code

- The build treats warnings as errors, with every .NET analyzer and StyleCop enabled.
  Keep it that way rather than adding suppressions.
- `Tentacle.Protocol` and `Tentacle.Cli` must stay NativeAOT-compatible: no reflection,
  JSON through the source generator.
- `Tentacle.Broker` must not reference Jellyfin. The plugin is the thin adapter between
  the two, which keeps the broker testable on its own.
- Wire protocol changes only add JSON fields. A breaking change bumps
  `ProtocolInfo.Version`.
- [docs/architecture.md](docs/architecture.md) explains how the pieces fit together.

## Releases

One version covers every artifact: the plugin, the binary and the worker mod image. To cut
a release, bump `<Version>` in `Directory.Build.props` and `version` in `build.yaml`, add
the entry at the top of `CHANGELOG.md` and the one-paragraph summary in `build.yaml`,
merge to main, wait for CI to pass, then push the `vX.Y.Z` tag. The release workflow
pushes the images and creates the GitHub release.

## License

By contributing you agree that your work is licensed under the
[GPL-3.0](LICENSE), like the rest of the project.
