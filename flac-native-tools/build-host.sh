#!/usr/bin/env bash
# ==============================================================================================
# build-host.sh - a quick, NOT-SHIPPED build of libcodebrix_flac.so with THIS machine's tools
# ==============================================================================================
#
#   ./build-host.sh
#
# Builds shim/ (+ libFLAC from the unmodified flac/ tree, through flac's own CMake) with the host's
# own cmake and C compiler into output/host/libcodebrix_flac.so and runs the smoke test
# (codebrix_flac_smoke, built by the same CMake) against it. It exists so the managed tests can run
# on a Linux dev box before (or without) a container build.
#
# The result is NEVER copied into src/.../runtimes: its glibc floor is whatever this machine has.
# Shipped Linux binaries come only from linux/build.sh (manylinux containers, --network none).
# Reads only this repository; installs nothing.
# ==============================================================================================
set -euo pipefail

TOOLS_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SCRATCH="$TOOLS_DIR/build-scratch/host"
OUT="$TOOLS_DIR/output/host"

for t in cmake cc; do
    command -v "$t" > /dev/null 2>&1 || { echo "ERROR: $t is not installed (this script installs nothing)." >&2; exit 1; }
done

rm -rf "$SCRATCH"
mkdir -p "$SCRATCH" "$OUT"
cmake -S "$TOOLS_DIR/shim" -B "$SCRATCH" -DCMAKE_BUILD_TYPE=Release -DCBF_BUILD_SMOKE_TEST=ON > "$SCRATCH/configure.txt"
cmake --build "$SCRATCH" -j "$(nproc)" --target codebrix_flac codebrix_flac_smoke
cp "$SCRATCH/libcodebrix_flac.so" "$OUT/libcodebrix_flac.so"
bash "$TOOLS_DIR/licenses/compose-license-flac.sh" --check
cp "$TOOLS_DIR/licenses/LICENSE-Flac.txt" "$OUT/LICENSE-Flac.txt"
cp "$SCRATCH/codebrix_flac_smoke" "$OUT/smoke-test"
"$OUT/smoke-test" "$OUT/libcodebrix_flac.so" "$OUT/smoke-test-440hz.flac"
rm -rf "$SCRATCH"
echo
echo "host build: $OUT/libcodebrix_flac.so ($(stat -c %s "$OUT/libcodebrix_flac.so") bytes) - NOT for shipping"
