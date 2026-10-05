# shellcheck shell=bash
# Server role: point Jellyfin at the shim and install the bundled plugin, so the
# plugin, shim and agent always come from the same build.

# svc-jellyfin passes --ffmpeg="${FFMPEG_PATH}" (it falls back to the real
# binary if the path is not a file). ffprobe is derived as its sibling.
printf '/usr/local/bin/tentacle/ffmpeg' > /var/run/s6/container_environment/FFMPEG_PATH
if [[ -z "${TENTACLE_SOCKET:-}" ]]; then
    printf '/run/tentacle/broker.sock' > /var/run/s6/container_environment/TENTACLE_SOCKET
fi

if [[ "${TENTACLE_INSTALL_PLUGIN:-true}" == "true" ]]; then
    version="$(cat /tentacle/plugin/VERSION)"
    plugins=/config/data/plugins
    target="${plugins}/Tentacle_${version}"
    mkdir -p "${plugins}"
    for old in "${plugins}"/Tentacle_*; do
        [[ -d "${old}" && "${old}" != "${target}" ]] && rm -rf "${old}"
    done
    if ! cmp -s /tentacle/plugin/meta.json "${target}/meta.json" 2>/dev/null; then
        rm -rf "${target}"
        mkdir -p "${target}"
        cp /tentacle/plugin/*.dll /tentacle/plugin/meta.json "${target}/"
        echo "**** tentacle: installed plugin ${version} into ${target} ****"
    fi
    lsiown -R abc:abc "${target}"
fi
