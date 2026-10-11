#!/usr/bin/env bash
# ==============================================================================================
# build-osx-arm64.sh - build libcodebrix_flac.dylib for the osx-arm64 runtime identifier
# ==============================================================================================
#
#   Written on Linux on 2026-10-10 as a copy of CodeBrix.Audio.Vorbis's script of the same name
#   (run successfully, unchanged, by Jeremy on his Apple Silicon Mac that day), adapted to FLAC.
#   FIRST RUN for FLAC 2026-10-11 UTC (2026-10-10 evening, local), same Mac: ran UNCHANGED, the
#   complete gate passed and the result was adopted - see ../BUILD-PROVENANCE.txt. If something
#   breaks on another machine, fix it IN THE SCRIPT (or build-common.sh) and record it there.
#
# USAGE
#     cd flac-native-tools/macos
#     ./build-osx-arm64.sh            # builds, gates, and (gate COMPLETE) adopts into src/
#     ADOPT=0 ./build-osx-arm64.sh    # build + gate only
#
# TWO ROUTES, chosen automatically from the host:
#   * On an Apple Silicon Mac (uname -m = arm64): native. The whole gate runs. PREFERRED.
#   * On an Intel Mac: a cross build via -DCMAKE_OSX_ARCHITECTURES=arm64. An Intel Mac cannot
#     execute arm64 code, so the smoke test cannot run: reported as a FAILURE, exit 1, nothing
#     adopted. Run it again on an Apple Silicon Mac.
#
# Deployment target 11.0 (MACOS_MIN_ARM64 in build-common.sh) - the first macOS on Apple Silicon.
# Output: ../output/osx-arm64/.  It installs nothing.
# ==============================================================================================

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=build-common.sh
. "$SCRIPT_DIR/build-common.sh"

[ "$(uname -s)" = "Darwin" ] || { echo "ERROR: run this on macOS." >&2; exit 1; }

if [ "$(uname -m)" = "arm64" ]; then
    CAN_RUN=yes
    ROUTE="native (Apple Silicon)"
else
    CAN_RUN=no
    ROUTE="cross-compiled on $(uname -m) via CMAKE_OSX_ARCHITECTURES=arm64 - smoke test cannot run here"
fi

run_macos_build osx-arm64 arm64 "$MACOS_MIN_ARM64" "$CAN_RUN" "$ROUTE" build-osx-arm64.sh
