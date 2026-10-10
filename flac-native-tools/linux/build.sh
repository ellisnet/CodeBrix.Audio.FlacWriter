#!/usr/bin/env bash
# ==============================================================================================
# build.sh - build the three Linux codebrix_flac native libraries, from this repository only
# ==============================================================================================
#
#   ./build.sh                build all three (linux-x64, linux-arm64, linux-riscv64)
#   ./build.sh all            the same
#   ./build.sh x64            build linux-x64 only
#   ./build.sh arm64          build linux-arm64 only
#   ./build.sh riscv64        build linux-riscv64 only
#
# Options (environment variables):
#   CONTAINER_ENGINE=podman|docker   force an engine (default: podman, then docker)
#   FORCE_IMAGE_REBUILD=1            rebuild the derived build image even if it exists
#   ADOPT=0                          do NOT copy the gated result into the package
#                                    (default ADOPT=1: see "ADOPTION" below)
#
# WHAT THIS DOES, IN ONE PARAGRAPH
#   For each architecture it makes sure a derived build image exists (the digest-pinned
#   manylinux base image + a toolchain probe - see Containerfile.<arch>; it installs nothing, so
#   even that image build runs with --network none), then runs container-build.sh inside it WITH
#   THE NETWORK DISABLED, mounting flac-native-tools/ as /work. Everything compiled comes from
#   this repository: the unmodified Xiph flac-1.5.0 tree in flac/ (libFLAC only, through flac's
#   own CMake), the shim in shim/ and the gate's smoke-test.c.
#
# WHY A CONTAINER, EVEN FOR X64
#   glibc symbol versioning is forward-only: a binary built against the glibc on a current
#   desktop distro refuses to load on anything older. The manylinux images fix the floor at glibc
#   2.28 (2.39 for riscv64, where nothing older exists), and the gate CHECKS that floor. On one
#   x86_64 host the two foreign architectures run under qemu-user emulation (slow, but this
#   library is small).
#
# ADOPTION (ADOPT=1, the default). After container-build.sh's gate has passed for a RID, this
#   script copies output/<rid>/{libcodebrix_flac.so,LICENSE-Flac.txt} into
#   ../../src/CodeBrix.Audio.FlacWriter/runtimes/<rid>/native/ and the unstripped twin into
#   ../unstripped/<rid>/. A RID whose gate failed is never adopted. Record the build in
#   ../BUILD-PROVENANCE.txt from output/<rid>/BUILD-INFO.txt.
#
# THIS SCRIPT NEVER INSTALLS ANYTHING ON YOUR MACHINE. If a prerequisite is missing it names it,
# prints the command that installs it, and stops.
#
# Copied from CodeBrix.Audio.Vorbis's vorbis-native-tools/linux/build.sh (itself modelled on
# CodeBrix.Sdl3's sdl3-native-tools/linux/build.sh) and adapted.
# ==============================================================================================

set -euo pipefail

trap 'rc=$?; echo "ERROR: build.sh failed (exit $rc) at line $LINENO: $BASH_COMMAND" >&2' ERR

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
TOOLS_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"           # flac-native-tools/
REPO_DIR="$(cd "$TOOLS_DIR/.." && pwd)"
OUTPUT_DIR="$TOOLS_DIR/output"
RUNTIMES_DIR="$REPO_DIR/src/CodeBrix.Audio.FlacWriter/runtimes"
UNSTRIPPED_DIR="$TOOLS_DIR/unstripped"

# shellcheck source=pins.env
. "$SCRIPT_DIR/pins.env"

# ----------------------------------------------------------------------------------------------
# Prerequisites - checked, never installed
# ----------------------------------------------------------------------------------------------
pick_engine() {
    if [ -n "${CONTAINER_ENGINE:-}" ]; then
        command -v "$CONTAINER_ENGINE" > /dev/null 2>&1 || {
            echo "ERROR: CONTAINER_ENGINE=$CONTAINER_ENGINE is not on PATH." >&2; exit 1; }
        echo "$CONTAINER_ENGINE"
        return
    fi
    if command -v podman > /dev/null 2>&1; then echo podman; return; fi
    if command -v docker > /dev/null 2>&1; then echo docker; return; fi
    cat >&2 <<'MSG'
ERROR: neither podman nor docker is installed.

  This script does not install anything. Install a container engine yourself:

    Debian-based Linux:  sudo apt install podman

  Then re-run. See ../README.txt for the complete list of prerequisites.
MSG
    exit 1
}

ENGINE="$(pick_engine)"

for f in "$FLAC_DIR/src/libFLAC/stream_encoder.c" "$FLAC_DIR/CMakeLists.txt" "$FLAC_DIR/COPYING.Xiph" \
         shim/codebrix_flac.c shim/codebrix_flac.h shim/CMakeLists.txt smoke-test.c \
         licenses/LICENSE-Flac.txt licenses/compose-license-flac.sh; do
    [ -f "$TOOLS_DIR/$f" ] || {
        echo "ERROR: $TOOLS_DIR/$f is missing. It is part of this repository and is never" >&2
        echo "       downloaded - restore it from git." >&2
        exit 1; }
done

host_machine="$(uname -m)"

arch_rid()      { case "$1" in x64) echo linux-x64;; arm64) echo linux-arm64;; riscv64) echo linux-riscv64;; esac; }
arch_platform() { case "$1" in x64) echo linux/amd64;; arm64) echo linux/arm64;; riscv64) echo linux/riscv64;; esac; }
arch_native()   { case "$1" in x64) echo x86_64;; arm64) echo aarch64;; riscv64) echo riscv64;; esac; }
arch_image()    {
    case "$1" in
        x64)     echo "${IMAGE_X64%%:*}@$IMAGE_X64_DIGEST" ;;
        arm64)   echo "${IMAGE_ARM64%%:*}@$IMAGE_ARM64_DIGEST" ;;
        riscv64) echo "${IMAGE_RISCV64%%:*}@$IMAGE_RISCV64_DIGEST" ;;
    esac
}
arch_tag()      {
    case "$1" in x64) echo "$IMAGE_X64";; arm64) echo "$IMAGE_ARM64";; riscv64) echo "$IMAGE_RISCV64";; esac
}
arch_glibc_max() {
    case "$1" in x64) echo "$GLIBC_MAX_X64";; arm64) echo "$GLIBC_MAX_ARM64";; riscv64) echo "$GLIBC_MAX_RISCV64";; esac
}

check_emulation() {
    local arch="$1" want
    want="$(arch_native "$arch")"
    [ "$want" = "$host_machine" ] && { echo "  emulation : none needed - native $want host"; return 0; }

    # grep WITHOUT -q on purpose: -q exits at the first match, `ls` then dies of SIGPIPE, and
    # under `set -o pipefail` that reads as "no handler registered".
    if [ -d /proc/sys/fs/binfmt_misc ] && ls /proc/sys/fs/binfmt_misc 2>/dev/null | grep -i "qemu-$want" > /dev/null; then
        echo "  emulation : qemu-$want binfmt handler registered (expect a slower build)"
        return 0
    fi
    cat >&2 <<MSG

ERROR: building $arch on a $host_machine host needs qemu user-mode emulation, and no
       binfmt handler for qemu-$want is registered.

  Run this script on a native $want machine, or set up emulation yourself (not done for you):

    sudo apt install qemu-user-static binfmt-support

  Verify with:  ls /proc/sys/fs/binfmt_misc | grep qemu

MSG
    exit 1
}

ensure_base_present() {
    # The base image must already be on this machine: nothing is pulled (--pull=never below).
    local base="$1"
    "$ENGINE" image exists "$base" 2>/dev/null && return 0
    cat >&2 <<MSG
ERROR: the pinned base image is not on this machine:
         $base
  This tooling never pulls. Fetch it yourself once (the only step that needs the network):
         $ENGINE pull $base
MSG
    exit 1
}

ensure_image() {
    local arch="$1" narch derived base platform
    narch="$(arch_native "$arch")"
    derived="$DERIVED_IMAGE_PREFIX-$narch"
    base="$(arch_image "$arch")"
    platform="$(arch_platform "$arch")"

    if [ "${FORCE_IMAGE_REBUILD:-0}" != "1" ] && "$ENGINE" image exists "$derived" 2>/dev/null; then
        echo "  image     : $derived (already built - reusing)"
        return 0
    fi

    ensure_base_present "$base"
    echo "  image     : $derived - building it now (toolchain probe only; --network none)"
    "$ENGINE" build \
        --pull=never \
        --network none \
        --platform "$platform" \
        -f "$SCRIPT_DIR/Containerfile.$narch" \
        -t "$derived" \
        --build-arg BASE_IMAGE="$base" \
        "$SCRIPT_DIR"
}

adopt_rid() {
    local rid="$1" src="$OUTPUT_DIR/$1" dest="$RUNTIMES_DIR/$1/native"
    if [ "${ADOPT:-1}" = "0" ]; then
        echo "  adopt     : skipped (ADOPT=0) - result stays in output/$rid"
        return 0
    fi
    for f in "$LIB_NAME" "$LICENSE_FLAC_NAME" "unstripped/$LIB_NAME"; do
        [ -f "$src/$f" ] || { echo "ERROR: output/$rid/$f is missing; not adopting." >&2; exit 1; }
    done
    mkdir -p "$dest" "$UNSTRIPPED_DIR/$rid"
    cp "$src/$LIB_NAME" "$src/$LICENSE_FLAC_NAME" "$dest/"
    cp "$src/unstripped/$LIB_NAME" "$UNSTRIPPED_DIR/$rid/$LIB_NAME"
    echo "  adopt     : -> src/CodeBrix.Audio.FlacWriter/runtimes/$rid/native/ ($LIB_NAME + $LICENSE_FLAC_NAME)"
    echo "              -> flac-native-tools/unstripped/$rid/$LIB_NAME"
}

build_arch() {
    local arch="$1" rid narch derived base platform started elapsed
    rid="$(arch_rid "$arch")"
    narch="$(arch_native "$arch")"
    derived="$DERIVED_IMAGE_PREFIX-$narch"
    base="$(arch_image "$arch")"
    platform="$(arch_platform "$arch")"
    started="$SECONDS"

    echo
    echo "=============================================================================="
    echo " BUILD $rid"
    echo "=============================================================================="
    echo "  base tag  : $(arch_tag "$arch")"
    echo "  base pin  : $base"
    echo "  platform  : $platform"
    echo "  host      : $host_machine"
    check_emulation "$arch"
    ensure_image "$arch"
    echo "  network   : DISABLED for the build itself (--network none)"
    echo

    "$ENGINE" run --rm \
        --pull=never \
        --network none \
        --platform "$platform" \
        -v "$TOOLS_DIR":/work:Z \
        -e TARGET_RID="$rid" \
        -e DERIVED_IMAGE="$derived" \
        -e BASE_IMAGE_REF="$base" \
        -e FLAC_DIR="$FLAC_DIR" \
        -e FLAC_VERSION="$FLAC_VERSION" \
        -e FLAC_TARBALL_SHA256="$FLAC_TARBALL_SHA256" \
        -e LIB_NAME="$LIB_NAME" \
        -e LIB_SONAME="$LIB_SONAME" \
        -e LICENSE_FLAC_NAME="$LICENSE_FLAC_NAME" \
        -e CBF_CMAKE_OPTIONS="$CBF_CMAKE_OPTIONS" \
        -e GLIBC_MAX="$(arch_glibc_max "$arch")" \
        -e ALLOWED_DEPS="$ALLOWED_DEPS" \
        -e REQUIRED_SYMBOLS="$REQUIRED_SYMBOLS" \
        "$derived" \
        bash /work/linux/container-build.sh

    adopt_rid "$rid"
    elapsed=$(( SECONDS - started ))
    echo "  $rid finished in ${elapsed}s"
}

write_sha256sums() {
    # One checksum file covering every built RID, including any built on other machines and
    # copied in. Paths are relative to output/.
    [ -d "$OUTPUT_DIR" ] || return 0
    ( cd "$OUTPUT_DIR" && \
      find . -path ./host -prune -o -type f \( -name "$LIB_NAME" -o -name 'codebrix_flac.dll' -o -name 'libcodebrix_flac.dylib' \) -print \
        | grep -v '/unstripped/' | sed 's|^\./||' | sort | xargs -r sha256sum > SHA256SUMS )
    echo
    echo "  output/SHA256SUMS:"
    sed 's/^/    /' "$OUTPUT_DIR/SHA256SUMS"
    if [ -d "$UNSTRIPPED_DIR" ]; then
        ( cd "$UNSTRIPPED_DIR" && find . -type f \( -name "$LIB_NAME" -o -name '*.dylib' \) \
            | sed 's|^\./||' | sort | xargs -r sha256sum > SHA256SUMS )
    fi
}

# ----------------------------------------------------------------------------------------------
# Main
# ----------------------------------------------------------------------------------------------
TARGET="${1:-all}"

echo "container engine : $ENGINE"
echo "tools folder     : $TOOLS_DIR"
echo "flac (libFLAC)   : $FLAC_VERSION (tarball sha256 $FLAC_TARBALL_SHA256)"
echo "adopt into src   : $([ "${ADOPT:-1}" = "0" ] && echo no || echo yes)"

case "$TARGET" in
    x64|arm64|riscv64) build_arch "$TARGET" ;;
    all)               for a in x64 arm64 riscv64; do build_arch "$a"; done ;;
    *) echo "usage: $0 [all|x64|arm64|riscv64]" >&2; exit 2 ;;
esac

write_sha256sums

echo
echo "=============================================================================="
echo " Outputs are in flac-native-tools/output/<rid>/"
echo "   $LIB_NAME   stripped, the file the package ships"
echo "   $LICENSE_FLAC_NAME        licenses/LICENSE-Flac.txt = flac/COPYING.Xiph (BSD-3), shipped beside it"
echo "   BUILD-INFO.txt          toolchain, pins, flags, sizes, sha256, glibc floor, smoke test"
echo "   unstripped/$LIB_NAME   the pre-strip twin, for crash triage; never shipped"
echo " Record each build in ../BUILD-PROVENANCE.txt from its BUILD-INFO.txt."
echo "=============================================================================="
