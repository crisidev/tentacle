# LinuxServer docker mod for linuxserver/jellyfin. One build, two tags:
#   --target server  ->  :server-<ver>  (shim + bundled plugin)
#   --target worker  ->  :worker-<ver>  (shim + agent replacing svc-jellyfin)
# docker-mods applies only the FIRST layer of a mod image, so each role is a
# single COPY of a tree that scripts/build.sh already merged (common + role).
# Build context is dist/mod, one tree per architecture: build with
# --platform linux/amd64,linux/arm64 for a multi-arch image (no emulation needed:
# nothing runs during the build). The mod loader picks the host's architecture.
FROM scratch AS server
ARG TARGETARCH
COPY ${TARGETARCH}/server/ /

FROM scratch AS worker
ARG TARGETARCH
COPY ${TARGETARCH}/worker/ /
