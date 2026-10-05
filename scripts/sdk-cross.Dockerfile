# The NativeAOT SDK plus the aarch64 linker and libc: builds the linux-arm64 binary
# on amd64 (clang in the -aot image already targets arm64). Built by scripts/build.sh.
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble-aot
RUN apt-get update -q \
 && apt-get install -yq --no-install-recommends gcc-aarch64-linux-gnu binutils-aarch64-linux-gnu libc6-dev-arm64-cross \
 && rm -rf /var/lib/apt/lists/*
