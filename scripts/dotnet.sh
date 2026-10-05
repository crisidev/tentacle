#!/usr/bin/env bash
# Runs dotnet inside the .NET 10 SDK container: no local SDK needed.
# NuGet packages are cached in a named volume between runs.
#   scripts/dotnet.sh build Tentacle.slnx
#   TENTACLE_SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0-noble-aot scripts/dotnet.sh publish ...
set -euo pipefail
# CI installs the SDK in the job container (bind mounts do not work under
# docker-in-docker), so a local dotnet wins unless TENTACLE_DOTNET=docker.
if [[ "${TENTACLE_DOTNET:-auto}" != "docker" ]] && command -v dotnet >/dev/null 2>&1; then
    exec dotnet "$@"
fi
root="$(cd "$(dirname "$0")/.." && pwd)"
image="${TENTACLE_SDK_IMAGE:-mcr.microsoft.com/dotnet/sdk:10.0}"
exec docker run --rm \
  -u "$(id -u):$(id -g)" \
  -e HOME=/tmp -e DOTNET_CLI_HOME=/tmp -e DOTNET_CLI_TELEMETRY_OPTOUT=1 -e DOTNET_NOLOGO=1 \
  -e NUGET_PACKAGES=/nuget \
  -v tentacle-nuget:/nuget \
  -v "$root:/src" -w /src \
  "$image" dotnet "$@"
