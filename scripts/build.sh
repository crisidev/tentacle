#!/usr/bin/env bash
# Builds every Tentacle artifact in containers (no local .NET SDK needed).
#   scripts/build.sh [test|rules|plugin|cli|mod|all]...   (default: all)
# Outputs in dist/: plugin/ (DLLs + meta.json), tentacle_<ver>.zip, cli/tentacle
# (amd64), cli-arm64/tentacle, mod/<amd64|arm64>/<role> (docker-mod build contexts)
# and the local amd64 images tentacle:{server,worker}-<ver>.
# TENTACLE_ARCHES="amd64 arm64" (default) picks the binaries to build; CI pushes
# both as one multi-arch image, so LinuxServer's mod loader picks the right one.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"

version="${TENTACLE_VERSION:-$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props)}"
abi="12.1.0.0"
guid="3a65d525-990c-4f73-89e9-a0d1500a53d2"
image="${TENTACLE_IMAGE:-tentacle}"
dn=(scripts/dotnet.sh)
# The NativeAOT binary must link against a standard glibc loader (/lib64/...), so it
# is built in the Ubuntu 24.04 SDK image even when a local dotnet exists (a Nix SDK
# would bake a /nix/store interpreter in). CI runs on Ubuntu and sets TENTACLE_AOT_LOCAL=1.
if [[ "${TENTACLE_AOT_LOCAL:-0}" == "1" ]]; then
    dn_aot=(scripts/dotnet.sh)
    dn_cross=(scripts/dotnet.sh)
else
    dn_aot=(env TENTACLE_DOTNET=docker TENTACLE_SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0-noble-aot scripts/dotnet.sh)
    dn_cross=(env TENTACLE_DOTNET=docker TENTACLE_SDK_IMAGE=tentacle-sdk-cross:10.0 scripts/dotnet.sh)
fi
read -r -a arches <<<"${TENTACLE_ARCHES:-amd64 arm64}"
vprops=(-p:Version="${version}" -p:AssemblyVersion="${version}.0" -p:FileVersion="${version}.0")

do_test() {
    "${dn[@]}" test --solution Tentacle.slnx -c Release "${vprops[@]}"
}

# promtool unit tests for the alerts; downloads promtool when it is not installed.
do_rules() {
    local promtool prom_version=3.15.0
    promtool="$(command -v promtool || true)"
    if [[ -z "${promtool}" ]]; then
        promtool=".cache/prometheus-${prom_version}/promtool"
        if [[ ! -x "${promtool}" ]]; then
            mkdir -p .cache
            curl -fsSL "https://github.com/prometheus/prometheus/releases/download/v${prom_version}/prometheus-${prom_version}.linux-amd64.tar.gz" \
                | tar -xz -C .cache --strip-components=1 --one-top-level="prometheus-${prom_version}" "prometheus-${prom_version}.linux-amd64/promtool"
        fi
    fi
    "${promtool}" check rules deploy/prometheus/tentacle.rules.yaml
    "${promtool}" test rules deploy/prometheus/tentacle.rules.test.yaml
}

do_plugin() {
    rm -rf dist/plugin dist/plugin-raw
    "${dn[@]}" publish src/Jellyfin.Plugin.Tentacle/Jellyfin.Plugin.Tentacle.csproj \
        -c Release -o dist/plugin-raw "${vprops[@]}"
    mkdir -p dist/plugin
    # Only our assemblies: Jellyfin provides everything else, and PluginLoadContext
    # loads every *.dll it finds in the folder.
    assemblies=(Jellyfin.Plugin.Tentacle.dll Tentacle.Broker.dll Tentacle.Protocol.dll)
    for a in "${assemblies[@]}"; do cp "dist/plugin-raw/${a}" dist/plugin/; done
    cat > dist/plugin/meta.json <<JSON
{
  "category": "General",
  "changelog": "",
  "description": "Brokers Jellyfin's ffmpeg jobs to worker nodes (tentacles) or runs them locally. Replaces rffmpeg.",
  "guid": "${guid}",
  "name": "Tentacle",
  "overview": "Native remote transcoding",
  "owner": "crisidev",
  "targetAbi": "${abi}",
  "timestamp": "$(date -u +%Y-%m-%dT%H:%M:%SZ)",
  "version": "${version}.0",
  "status": "Active",
  "autoUpdate": false,
  "assemblies": [$(printf '"%s",' "${assemblies[@]}" | sed 's/,$//')]
}
JSON
    printf '%s.0' "${version}" > dist/plugin/VERSION
    rm -f "dist/tentacle_${version}.zip"
    (cd dist/plugin && zip -q "../tentacle_${version}.zip" ./*.dll meta.json)
    rm -rf dist/plugin-raw
    echo "plugin: dist/plugin, dist/tentacle_${version}.zip"
}

# dist/<dir>/tentacle for one architecture: amd64 → cli, arm64 → cli-arm64.
cli_dir() { [[ "$1" == amd64 ]] && echo dist/cli || echo "dist/cli-$1"; }

do_cli() {
    for arch in "${arches[@]}"; do
        out="$(cli_dir "${arch}")"
        rm -rf "${out}"
        case "${arch}" in
            amd64)
                "${dn_aot[@]}" publish src/Tentacle.Cli/Tentacle.Cli.csproj -c Release -r linux-x64 -o "${out}" "${vprops[@]}"
                want=/lib64/ld-linux-x86-64.so.2 ;;
            arm64)
                # Cross-compiled: clang targets aarch64, the GNU cross binutils/libc link it.
                if [[ "${TENTACLE_AOT_LOCAL:-0}" != "1" ]]; then
                    docker build -q -t tentacle-sdk-cross:10.0 -f scripts/sdk-cross.Dockerfile scripts >/dev/null
                fi
                "${dn_cross[@]}" publish src/Tentacle.Cli/Tentacle.Cli.csproj -c Release -r linux-arm64 -o "${out}" \
                    -p:ObjCopyName=aarch64-linux-gnu-objcopy "${vprops[@]}"
                want=/lib/ld-linux-aarch64.so.1 ;;
            *) echo "cli: unknown arch ${arch}" >&2; exit 2 ;;
        esac
        rm -f "${out}"/*.dbg "${out}"/*.pdb "${out}"/*.xml
        interpreter="$(readelf -p .interp "${out}/tentacle" | awk '/ld-linux/ {print $NF}')"
        if [[ "${interpreter}" != "${want}" ]]; then
            echo "cli: unexpected ELF interpreter '${interpreter}' for ${arch}: the binary would not run in the LSIO image" >&2
            exit 1
        fi
        echo "cli: ${out}/tentacle (${arch})"
    done
}

do_mod() {
    for arch in "${arches[@]}"; do
        [[ -x "$(cli_dir "${arch}")/tentacle" ]] || do_cli
    done
    [[ -f dist/plugin/meta.json ]] || do_plugin
    rm -rf dist/mod
    # Each role = common + role overlay, merged here so the image has one layer.
    for arch in "${arches[@]}"; do
        for role in server worker; do
            dir="dist/mod/${arch}/${role}"
            mkdir -p "${dir}"
            cp -a mod/root-common/. "${dir}/"
            cp -a "mod/root-${role}/." "${dir}/"
            install -D -m 0755 "$(cli_dir "${arch}")/tentacle" "${dir}/usr/local/bin/tentacle/tentacle"
        done
        mkdir -p "dist/mod/${arch}/server/tentacle/plugin"
        cp dist/plugin/*.dll dist/plugin/meta.json dist/plugin/VERSION "dist/mod/${arch}/server/tentacle/plugin/"
    done
    # The local images (e2e tests) are amd64; CI pushes every arch with buildx.
    for role in server worker; do
        docker build -q --platform linux/amd64 -f mod/Dockerfile --target "${role}" -t "${image}:${role}-${version}" dist/mod
        echo "mod: ${image}:${role}-${version}"
    done
}

[[ $# -gt 0 ]] || set -- all
for step in "$@"; do
    case "${step}" in
        test) do_test ;;
        rules) do_rules ;;
        plugin) do_plugin ;;
        cli) do_cli ;;
        mod) do_mod ;;
        all) do_test; do_rules; do_plugin; do_cli; do_mod ;;
        *) echo "usage: $0 [test|rules|plugin|cli|mod|all]..." >&2; exit 2 ;;
    esac
done
