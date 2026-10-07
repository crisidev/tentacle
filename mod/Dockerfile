# LinuxServer docker mod for linuxserver/jellyfin: turns the container into a
# tentacle (worker). The server needs no mod: the Jellyfin plugin installs the shim.
# docker-mods applies only the FIRST layer of a mod image, so the mod is a single
# COPY of the tree scripts/build.sh prepared.
# Build context is dist/mod, one tree per architecture: build with
# --platform linux/amd64,linux/arm64 for a multi-arch image (no emulation needed:
# nothing runs during the build). The mod loader picks the host's architecture.
FROM scratch
ARG TARGETARCH
COPY ${TARGETARCH}/worker/ /
