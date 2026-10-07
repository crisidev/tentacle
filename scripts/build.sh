#!/usr/bin/env bash
# Builds every Tentacle artifact in containers (no local .NET SDK needed).
#   scripts/build.sh [test|rules|plugin|cli|mod|all]...   (default: all)
# Outputs in dist/: plugin/jellyfin-<12|10.11>/ (DLLs, meta.json and the tentacle
# binary per arch), tentacle_<ver>_jellyfin-<12|10.11>.zip (what Jellyfin installs
# from the plugin repository),
# cli/tentacle (amd64), cli-arm64/tentacle, mod/<amd64|arm64>/worker (the worker
# docker-mod build contexts) and the local amd64 images tentacle:worker-<ver> (the
# mod) and tentacle:server-<ver> (the plugin as Jellyfin installs it, for e2e).
# TENTACLE_ARCHES="amd64 arm64" (default) picks the binaries to build; CI pushes
# both as one multi-arch image, so LinuxServer's mod loader picks the right one.
# The plugin always carries both binaries, whatever TENTACLE_ARCHES says.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"

version="${TENTACLE_VERSION:-$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props)}"
# The plugin, once per Jellyfin line: line:framework:targetAbi:revision. The
# revision is the plugin version's 4th part, so the two builds of a release have
# different versions and Jellyfin installs the newest one it can load.
plugin_builds=("12:net10.0:12.1.0.0:1" "10.11:net9.0:10.11.0.0:0")
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
    # The plugin installs the shim itself: it ships the binary for every arch.
    for arch in amd64 arm64; do
        [[ -x "$(cli_dir "${arch}")/tentacle" ]] || do_cli_arches "${arch}"
    done
    rm -rf dist/plugin dist/plugin-raw dist/tentacle_*.zip
    # Only our assemblies: Jellyfin provides everything else, and PluginLoadContext
    # loads every *.dll it finds in the folder.
    assemblies=(Jellyfin.Plugin.Tentacle.dll Tentacle.Broker.dll Tentacle.Protocol.dll)
    for build in "${plugin_builds[@]}"; do
        IFS=: read -r line framework abi revision <<<"${build}"
        out="dist/plugin/jellyfin-${line}"
        "${dn[@]}" publish src/Jellyfin.Plugin.Tentacle/Jellyfin.Plugin.Tentacle.csproj \
            -c Release -f "${framework}" -o dist/plugin-raw "${vprops[@]}"
        mkdir -p "${out}"
        for a in "${assemblies[@]}"; do cp "dist/plugin-raw/${a}" "${out}/"; done
        for arch in amd64 arm64; do
            install -m 0755 "$(cli_dir "${arch}")/tentacle" "${out}/tentacle-${arch}"
        done
        cat > "${out}/meta.json" <<JSON
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
  "version": "${version}.${revision}",
  "status": "Active",
  "autoUpdate": false,
  "assemblies": [$(printf '"%s",' "${assemblies[@]}" | sed 's/,$//')]
}
JSON
        zip_file="tentacle_${version}_jellyfin-${line}.zip"
        (cd "${out}" && zip -q "../../${zip_file}" ./*.dll ./tentacle-* meta.json)
        rm -rf dist/plugin-raw
        echo "plugin: dist/${zip_file} (Jellyfin ${line}, version ${version}.${revision})"
    done
}

# dist/<dir>/tentacle for one architecture: amd64 → cli, arm64 → cli-arm64.
cli_dir() { [[ "$1" == amd64 ]] && echo dist/cli || echo "dist/cli-$1"; }

do_cli() { do_cli_arches "${arches[@]}"; }

do_cli_arches() {
    for arch in "$@"; do
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
        [[ -x "$(cli_dir "${arch}")/tentacle" ]] || do_cli_arches "${arch}"
    done
    [[ -f "dist/tentacle_${version}_jellyfin-12.zip" ]] || do_plugin
    rm -rf dist/mod
    # The worker mod is one tree, merged here so the image has one layer.
    for arch in "${arches[@]}"; do
        dir="dist/mod/${arch}/worker"
        mkdir -p "${dir}"
        cp -a mod/root-worker/. "${dir}/"
        install -D -m 0755 "$(cli_dir "${arch}")/tentacle" "${dir}/usr/local/bin/tentacle/tentacle"
    done
    # The local images (e2e tests) are amd64; CI pushes every arch with buildx.
    docker build -q --platform linux/amd64 -f mod/Dockerfile -t "${image}:worker-${version}" dist/mod
    echo "mod: ${image}:worker-${version}"
    # The server for e2e: no mod, the plugin unpacked where Jellyfin installs it
    # from the repository (what the e2e scripts copy over the stock image), one
    # image per Jellyfin line: :server-<ver> for 12, :server-<ver>-10.11.
    for build in "${plugin_builds[@]}"; do
        IFS=: read -r line _ _ revision <<<"${build}"
        tag="${image}:server-${version}$([[ "${line}" == 12 ]] || echo "-${line}")"
        plugin_dir="dist/e2e-server/config/data/plugins/Tentacle_${version}.${revision}"
        rm -rf dist/e2e-server && mkdir -p "${plugin_dir}"
        unzip -q "dist/tentacle_${version}_jellyfin-${line}.zip" -d "${plugin_dir}"
        printf 'FROM scratch\nCOPY config /config\n' | docker build -q -f - -t "${tag}" dist/e2e-server >/dev/null
        echo "plugin (e2e): ${tag}"
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
