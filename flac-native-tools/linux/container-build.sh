#!/usr/bin/env bash
# ==============================================================================================
# container-build.sh - builds AND verifies libcodebrix_flac.so INSIDE the build container
# ==============================================================================================
#
# Do not run this on your workstation. build.sh runs it inside the derived image
# (codebrix-flac-build-<arch>) for the requested architecture, WITH THE NETWORK DISABLED:
#
#     podman run --network none ... codebrix-flac-build-<arch> bash /work/linux/container-build.sh
#
# That is the mechanical proof of the rule this folder exists for: if any step ever tried to
# fetch a source file or a dependency, it would fail here. Step 0 prints the proof into the log.
#
# It expects:
#   /work            flac-native-tools/, mounted read-write (only output/ is written)
#   $TARGET_RID      linux-x64 | linux-arm64 | linux-riscv64
#   the pins         passed through the environment by build.sh, which sources pins.env
#
# Everything it compiles is already in the repository: /work/flac is the unmodified Xiph
# flac-1.5.0 release tree (only libFLAC is compiled, through flac's own CMake), /work/shim the
# codebrix_flac shim + its CMakeLists, and /work/smoke-test.c the gate's load-and-run program
# (which links its own static libFLAC as an independent verifying DECODER). Copied from
# CodeBrix.Audio.Vorbis's vorbis-native-tools/linux/container-build.sh and adapted.
# ==============================================================================================

set -euo pipefail

trap 'rc=$?; echo "ERROR: container-build.sh failed (exit $rc) at line $LINENO: $BASH_COMMAND" >&2' ERR

# first_line: the first line of stdin WITHOUT the `head -1` pitfall (SIGPIPE under pipefail).
first_line() { sed -n '1p'; }

WORK=/work
PATCHES="$WORK/patches"
SCRATCH=/tmp/codebrix-flac-src
BUILD=/tmp/codebrix-flac-build
OUT="$WORK/output/$TARGET_RID"
STARTED_AT="$(date -u '+%Y-%m-%d %H:%M:%S UTC')"
START_SECONDS="$SECONDS"
CONTAINER_OS="$(grep PRETTY_NAME /etc/os-release 2>/dev/null | first_line | cut -d= -f2- | tr -d '"')"

echo "=============================================================================="
echo " codebrix_flac (libFLAC $FLAC_VERSION) - $TARGET_RID"
echo "=============================================================================="
echo " started   : $STARTED_AT"
echo " container : $CONTAINER_OS"
echo " image     : ${DERIVED_IMAGE:-unknown}"
echo " base      : ${BASE_IMAGE_REF:-unknown}"
echo " arch      : $(uname -m)"
echo

# ----------------------------------------------------------------------------------------------
# 0. Prove there is no network.
# ----------------------------------------------------------------------------------------------
IFACES="$(ls /sys/class/net 2>/dev/null | tr '\n' ' ' | sed 's/ *$//')"
echo "--- network ---"
echo "  interfaces: ${IFACES:-none}"
if [ "$IFACES" = "lo" ] || [ -z "$IFACES" ]; then
    echo "  [ok] no external network interface - this build cannot reach outside the repository"
else
    echo "  [warn] this container HAS a network interface ($IFACES)."
    echo "         The build still fetches nothing, but the proof is weaker. build.sh normally"
    echo "         passes --network none; something overrode it."
fi
echo

# ----------------------------------------------------------------------------------------------
# 1. Toolchain - whatever the pinned base image carries (Containerfile.<arch> installs nothing).
# ----------------------------------------------------------------------------------------------
echo "--- toolchain ---"
for t in cc cmake make file nm objdump readelf strip; do
    command -v "$t" > /dev/null 2>&1 || { echo "ERROR: $t is missing from this image." >&2; exit 1; }
done
CC_VERSION="$(cc --version | first_line)"
LD_VERSION="$(ld --version | first_line)"
CMAKE_ACTUAL="$(cmake --version | first_line)"
MAKE_ACTUAL="$(make --version | first_line)"
GLIBC_ACTUAL="$(ldd --version | first_line)"
echo "  cc      : $CC_VERSION"
echo "  ld      : $LD_VERSION"
echo "  cmake   : $CMAKE_ACTUAL"
echo "  make    : $MAKE_ACTUAL"
echo "  glibc   : $GLIBC_ACTUAL"
echo

# ----------------------------------------------------------------------------------------------
# 2. Source. flac/, shim/ and smoke-test.c are copied to scratch and built THERE, so the
#    vendored tree is never written to. Patches (none today) are applied to the copy.
# ----------------------------------------------------------------------------------------------
echo "--- source ---"
for f in "$FLAC_DIR/src/libFLAC/stream_encoder.c" "$FLAC_DIR/CMakeLists.txt" shim/CMakeLists.txt smoke-test.c; do
    [ -f "$WORK/$f" ] || { echo "ERROR: $WORK/$f is missing" >&2; exit 1; }
done
rm -rf "$SCRATCH" "$BUILD"
mkdir -p "$SCRATCH"
cp -a "$WORK/$FLAC_DIR" "$SCRATCH/flac"
cp -a "$WORK/shim" "$SCRATCH/shim"
cp -a "$WORK/smoke-test.c" "$SCRATCH/smoke-test.c"
echo "  copied flac/ shim/ smoke-test.c -> $SCRATCH"

PATCHES_APPLIED="none"
if [ -d "$PATCHES" ] && ls "$PATCHES"/*.patch > /dev/null 2>&1; then
    command -v patch > /dev/null 2>&1 || { echo "ERROR: patches/ has patches but this image has no 'patch'." >&2; exit 1; }
    PATCHES_APPLIED=""
    for p in "$PATCHES"/*.patch; do
        echo "  applying $(basename "$p")"
        ( cd "$SCRATCH" && patch -p1 --forward --batch < "$p" ) \
            || { echo "ERROR: patch $(basename "$p") did not apply. Patches are never applied best-effort." >&2; exit 1; }
        PATCHES_APPLIED="$PATCHES_APPLIED $(basename "$p")"
    done
    PATCHES_APPLIED="${PATCHES_APPLIED# }"
fi
echo "  patches applied: $PATCHES_APPLIED"
echo

# ----------------------------------------------------------------------------------------------
# 3. Configure + build. On top of CMake's Release -O3 -DNDEBUG:
#      -g                       debug info for the unstripped twin (strip removes it)
#      -ffile-prefix-map=...    no scratch path lands in the binary
#    shim/CMakeLists.txt itself adds -fvisibility=hidden (all objects, libFLAC's included), -fPIC,
#    the link options --no-undefined --exclude-libs,ALL -z noexecstack --build-id=sha1, and forces
#    libFLAC's CMake options (static, no C++/programs/examples/tests/docs/Ogg/multithreading).
#    Only the two targets the gate needs are built (codebrix_flac + codebrix_flac_smoke).
# ----------------------------------------------------------------------------------------------
C_FLAGS="-g -ffile-prefix-map=$SCRATCH=flac-native-tools -ffile-prefix-map=$BUILD=build"
echo "--- configuring ---"
echo "  cmake -S $SCRATCH/shim -B $BUILD -G \"Unix Makefiles\" $CBF_CMAKE_OPTIONS"
echo "        -DCMAKE_C_FLAGS=\"$C_FLAGS\""
mkdir -p "$BUILD"
# shellcheck disable=SC2086
cmake -S "$SCRATCH/shim" -B "$BUILD" -G "Unix Makefiles" $CBF_CMAKE_OPTIONS \
    -DCMAKE_C_FLAGS="$C_FLAGS" > "$BUILD/configure.log" 2>&1 \
    || { echo "ERROR: cmake configure failed:" >&2; tail -60 "$BUILD/configure.log" >&2; exit 1; }
grep -E 'CMake Warning' "$BUILD/configure.log" | sed 's/^/  /' || true

echo
echo "--- building ($(nproc) jobs) ---"
cmake --build "$BUILD" -j "$(nproc)" --target codebrix_flac codebrix_flac_smoke -- VERBOSE=1 > "$BUILD/build.log" 2>&1 \
    || { echo "ERROR: build failed:" >&2; grep -E -B2 -A8 'error:|Error ' "$BUILD/build.log" | head -80 >&2; exit 1; }
WARNINGS_ALL="$(grep -c 'warning:' "$BUILD/build.log" || true)"
WARNINGS="$(grep 'warning:' "$BUILD/build.log" | grep -c -E 'codebrix_flac\.c|smoke-test\.c' || true)"
echo "  compiler warnings: shim + smoke test $WARNINGS, total (libFLAC with its own -Wall -Wextra ...) $WARNINGS_ALL"
SHIM_COMPILE_LINE="$(grep -E 'codebrix_flac\.c' "$BUILD/build.log" | grep -E ' -c ' | first_line | sed "s|$SCRATCH|<src>|g; s|$BUILD|<build>|g")"
FLAC_COMPILE_LINE="$(grep -E 'stream_encoder\.c' "$BUILD/build.log" | grep -E ' -c ' | first_line | sed "s|$SCRATCH|<src>|g; s|$BUILD|<build>|g")"
FLAC_CPU_PATHS="$(grep -E '^(FLAC__CPU_X86_64|FLAC__CPU_IA32|FLAC__CPU_ARM64|FLAC__HAS_X86INTRIN|FLAC__HAS_NEONINTRIN|FLAC__HAS_A64NEONINTRIN|WITH_ASM|WITH_AVX|ENABLE_64_BIT_WORDS|ENABLE_MULTITHREADING|WITH_OGG|BUILD_SHARED_LIBS|HAVE_PTHREAD):' "$BUILD/CMakeCache.txt" | sed 's/:[A-Z]*=/=/' | tr '\n' ' ' | sed 's/ *$//')"
echo "  libFLAC configuration: $FLAC_CPU_PATHS"
LINK_LINE="$(grep -E ' -shared ' "$BUILD/build.log" | first_line | sed "s|$SCRATCH|<src>|g; s|$BUILD|<build>|g")"
echo

# ----------------------------------------------------------------------------------------------
# 4. Collect.
# ----------------------------------------------------------------------------------------------
BUILT="$BUILD/$LIB_NAME"
[ -f "$BUILT" ] && [ ! -L "$BUILT" ] || { echo "ERROR: $BUILT was not produced as a regular file" >&2; ls -la "$BUILD" >&2; exit 1; }

echo "--- collecting ---"
rm -rf "$OUT"
mkdir -p "$OUT/unstripped"
cp "$BUILT" "$OUT/unstripped/$LIB_NAME"
cp "$BUILT" "$OUT/$LIB_NAME"
chmod 0755 "$OUT/$LIB_NAME" "$OUT/unstripped/$LIB_NAME"
SIZE_UNSTRIPPED="$(stat -c %s "$OUT/unstripped/$LIB_NAME")"
strip --strip-unneeded "$OUT/$LIB_NAME"
SIZE_STRIPPED="$(stat -c %s "$OUT/$LIB_NAME")"
# readelf -n can exit 1 on Red Hat toolchain output (annobin "Gap in build notes"): hence || true.
build_id_of() { { readelf -n "$1" 2>/dev/null || true; } | awk '/Build ID/{print $3}' | first_line; }
BUILD_ID="$(build_id_of "$OUT/$LIB_NAME")"
BUILD_ID_UNSTRIPPED="$(build_id_of "$OUT/unstripped/$LIB_NAME")"
echo "  unstripped: $SIZE_UNSTRIPPED bytes -> stripped: $SIZE_STRIPPED bytes"
echo "  build-id  : ${BUILD_ID:-none}"
bash "$WORK/licenses/compose-license-flac.sh" --check
cp "$WORK/licenses/$LICENSE_FLAC_NAME" "$OUT/$LICENSE_FLAC_NAME"
echo "  $LICENSE_FLAC_NAME = licenses/LICENSE-Flac.txt (= flac/COPYING.Xiph, BSD-3), beside the binary"
echo

# ----------------------------------------------------------------------------------------------
# 5. THE GATE. Any failed check exits non-zero; output/<rid> is left for inspection but build.sh
#    will not adopt it.
# ----------------------------------------------------------------------------------------------
echo "--- verifying ---"
FAILED=0
fail() { echo "  [FAIL] $1"; FAILED=1; }
pass() { echo "  [ok] $1"; }

LIB="$OUT/$LIB_NAME"

# 5a. ELF class and machine.
FILE_OUT="$(file -b "$LIB")"
ELF_CLASS="$(readelf -h "$LIB" | awk -F: '/Class:/{gsub(/^ +/,"",$2); print $2}')"
ELF_MACHINE="$(readelf -h "$LIB" | awk -F: '/Machine:/{gsub(/^ +/,"",$2); print $2}')"
case "$TARGET_RID" in
    linux-x64)     WANT_MACHINE="Advanced Micro Devices X86-64" ;;
    linux-arm64)   WANT_MACHINE="AArch64" ;;
    linux-riscv64) WANT_MACHINE="RISC-V" ;;
    *)             WANT_MACHINE="?" ;;
esac
if [ "$ELF_CLASS" = "ELF64" ] && [ "$ELF_MACHINE" = "$WANT_MACHINE" ]; then
    pass "ELF class/machine: $ELF_CLASS / $ELF_MACHINE ($FILE_OUT)"
else
    fail "ELF class/machine: got '$ELF_CLASS' / '$ELF_MACHINE', expected ELF64 / '$WANT_MACHINE'"
fi

# 5b. SONAME.
SONAME="$(readelf -d "$LIB" | sed -n 's/.*(SONAME).*\[\(.*\)\].*/\1/p')"
if [ "$SONAME" = "$LIB_SONAME" ]; then pass "SONAME: $SONAME"; else fail "SONAME: got '$SONAME', expected '$LIB_SONAME'"; fi

# 5c. No RPATH / RUNPATH, no TEXTREL, non-executable stack.
RPATHS="$(readelf -d "$LIB" | grep -E '\((RPATH|RUNPATH)\)' || true)"
if [ -z "$RPATHS" ]; then pass "no RPATH / RUNPATH"; else fail "RPATH/RUNPATH present: $RPATHS"; fi
if readelf -d "$LIB" | grep -q TEXTREL; then fail "TEXTREL present (non-PIC code)"; else pass "no TEXTREL"; fi
STACK_FLAGS="$(readelf -lW "$LIB" | awk '/GNU_STACK/{print $7}')"
if [ "$STACK_FLAGS" = "RW" ]; then pass "GNU_STACK is RW (no executable stack)"; else fail "GNU_STACK flags '$STACK_FLAGS' (expected RW)"; fi

# 5d. EXACT export set: every function of codebrix_flac.h, and nothing else.
EXPORTS="$(nm -D --defined-only "$LIB" | awk '$2 ~ /^[TtWiDdBbRrVv]$/ {n=$3; sub(/@.*/, "", n); print n}' | sort -u)"
EXPORT_FUNCS="$(nm -D --defined-only "$LIB" | awk '$2 == "T" || $2 == "W" || $2 == "i" {n=$3; sub(/@.*/, "", n); print n}' | sort -u)"
WANT_SORTED="$(printf '%s\n' $REQUIRED_SYMBOLS | sort -u)"
MISSING="$(comm -23 <(printf '%s\n' "$WANT_SORTED") <(printf '%s\n' "$EXPORT_FUNCS") | tr '\n' ' ' | sed 's/ *$//')"
EXTRA="$(comm -13 <(printf '%s\n' "$WANT_SORTED") <(printf '%s\n' "$EXPORTS") | tr '\n' ' ' | sed 's/ *$//')"
EXPORT_COUNT="$(printf '%s\n' "$EXPORTS" | grep -c . || true)"
if [ -n "$MISSING" ]; then fail "missing exports: $MISSING"; else pass "all $(printf '%s\n' $REQUIRED_SYMBOLS | grep -c .) codebrix_flac_* functions exported"; fi
if [ -n "$EXTRA" ]; then fail "unexpected exports (libFLAC must stay hidden): $EXTRA"; else pass "nothing else exported ($EXPORT_COUNT dynamic symbols in total)"; fi

# 5e. Dependencies: system-only.
DEPS="$(objdump -p "$LIB" | awk '/NEEDED/ {print $2}' | sort)"
UNEXPECTED=""
for d in $DEPS; do
    case " $ALLOWED_DEPS " in *" $d "*) ;; *) UNEXPECTED="$UNEXPECTED $d" ;; esac
done
if [ -n "$UNEXPECTED" ]; then
    fail "unexpected dynamic dependencies:$UNEXPECTED (allowed: $ALLOWED_DEPS)"
else
    pass "NEEDED is system-only: $(printf '%s ' $DEPS)"
fi
LDD_OUT="$(ldd -r "$LIB" 2>&1 || true)"
UNDEFINED="$(printf '%s\n' "$LDD_OUT" | grep -i 'undefined symbol' || true)"
if [ -n "$UNDEFINED" ]; then
    fail "ldd -r reports undefined symbols:"; printf '%s\n' "$UNDEFINED" | sed 's/^/         /'
else
    pass "ldd -r: no undefined symbols"
fi

# 5f. glibc floor - CHECKED, not merely reported.
GLIBC_FLOOR="$(objdump -T "$LIB" | grep -oE 'GLIBC_[0-9]+\.[0-9]+(\.[0-9]+)?' | sed 's/GLIBC_//' | sort -V -u | tail -1 || true)"
GLIBC_FLOOR="${GLIBC_FLOOR:-none}"
if [ "$GLIBC_FLOOR" = "none" ]; then
    pass "glibc floor: none referenced"
elif printf '%s\n%s\n' "$GLIBC_FLOOR" "$GLIBC_MAX" | sort -V -C; then
    pass "glibc floor: $GLIBC_FLOOR (allowed <= $GLIBC_MAX)"
else
    fail "glibc floor $GLIBC_FLOOR is NEWER than the allowed $GLIBC_MAX"
fi

# 5g. Stripped and unstripped are the same build.
if [ -n "$BUILD_ID" ] && [ "$BUILD_ID" = "$BUILD_ID_UNSTRIPPED" ]; then
    pass "build-id $BUILD_ID identical in the stripped file and its unstripped twin"
else
    fail "build-id mismatch or missing: stripped '$BUILD_ID', unstripped '$BUILD_ID_UNSTRIPPED'"
fi

# 5h. Licence file: LICENSE-Flac.txt is exactly what licenses/compose-license-flac.sh produces
#     (flac/COPYING.Xiph verbatim).
if cmp -s "$OUT/$LICENSE_FLAC_NAME" <(bash "$WORK/licenses/compose-license-flac.sh") && cmp -s "$OUT/$LICENSE_FLAC_NAME" "$WORK/$FLAC_DIR/COPYING.Xiph"; then
    pass "$LICENSE_FLAC_NAME is exact (flac/COPYING.Xiph)"
else
    fail "$LICENSE_FLAC_NAME differs from licenses/compose-license-flac.sh output / flac/COPYING.Xiph"
fi

# 5i. dlopen smoke test, against the STAGED (stripped) file - the bytes that ship.
echo "  --- dlopen smoke test ---"
cp "$BUILD/codebrix_flac_smoke" "$OUT/smoke-test"
SMOKE_OUT="$("$OUT/smoke-test" "$LIB" "$OUT/smoke-test-440hz.flac" 2>&1)" && SMOKE_RC=0 || SMOKE_RC=$?
printf '%s\n' "$SMOKE_OUT" | sed 's/^/    /'
if [ "$SMOKE_RC" -eq 0 ]; then
    pass "smoke test (api_version 1; stream + packet mode, patches, verify, 24-bit, empty stream, error paths; libFLAC decoder sample-exact)"
else
    fail "smoke test exited $SMOKE_RC"
fi

if [ "$FAILED" -ne 0 ]; then
    echo
    echo "VERIFICATION FAILED for $TARGET_RID. output/$TARGET_RID is left in place for inspection,"
    echo "but this build must not be adopted into the package."
    exit 1
fi
echo

# ----------------------------------------------------------------------------------------------
# 6. Record.
# ----------------------------------------------------------------------------------------------
SHA="$(sha256sum "$LIB" | cut -d' ' -f1)"
SHA_UNSTRIPPED="$(sha256sum "$OUT/unstripped/$LIB_NAME" | cut -d' ' -f1)"
ELAPSED=$(( SECONDS - START_SECONDS ))
( cd "$OUT" && sha256sum "$LIB_NAME" "unstripped/$LIB_NAME" "$LICENSE_FLAC_NAME" smoke-test > SHA256SUMS.txt )
cp "$BUILD/configure.log" "$OUT/cmake-configure.log"
TOOLCHAIN_TABLE="$(cat /usr/local/share/codebrix-flac-build/toolchain.txt 2>/dev/null || echo '(not recorded - image not built from Containerfile.<arch>?)')"

cat > "$OUT/BUILD-INFO.txt" <<INFO
codebrix_flac native library - build information
==============================================================================
RID              : $TARGET_RID
Built            : $STARTED_AT
Build duration   : ${ELAPSED}s (wall clock inside the container)
Built by         : flac-native-tools/linux/build.sh -> container-build.sh
Network          : DISABLED during this build (interfaces: ${IFACES:-none})

Build machine
------------------------------------------------------------------------------
Derived image    : ${DERIVED_IMAGE:-unknown}
Base image       : ${BASE_IMAGE_REF:-unknown}
Container OS     : $CONTAINER_OS
Machine          : $(uname -m)
Compiler         : $CC_VERSION
Linker           : $LD_VERSION
cmake            : $CMAKE_ACTUAL
make             : $MAKE_ACTUAL
Container glibc  : $GLIBC_ACTUAL
Image probe      :
$TOOLCHAIN_TABLE

Source (vendored in-repo; nothing fetched at build time)
------------------------------------------------------------------------------
flac (libFLAC)   : $FLAC_VERSION   (flac/, tarball sha256 $FLAC_TARBALL_SHA256)
Shim             : shim/codebrix_flac.c + codebrix_flac.h (API version 1)
Patches applied  : $PATCHES_APPLIED

Configuration
------------------------------------------------------------------------------
cmake options    : -G "Unix Makefiles" $CBF_CMAKE_OPTIONS
CMAKE_C_FLAGS    : $C_FLAGS   (on top of Release's -O3 -DNDEBUG)
libFLAC config   : $FLAC_CPU_PATHS
shim compile     : $SHIM_COMPILE_LINE
libFLAC compile  : $FLAC_COMPILE_LINE
link             : $LINK_LINE
Warnings         : shim + smoke test $WARNINGS; total incl. libFLAC $WARNINGS_ALL

Result
------------------------------------------------------------------------------
File             : $LIB_NAME
SONAME           : $SONAME
Build-id         : ${BUILD_ID:-none}
Size (stripped)  : $SIZE_STRIPPED bytes
Size (unstripped): $SIZE_UNSTRIPPED bytes  -> unstripped/$LIB_NAME
SHA256           : $SHA
SHA256 unstripped: $SHA_UNSTRIPPED
ELF              : $ELF_CLASS, $ELF_MACHINE
glibc floor      : ${GLIBC_FLOOR} (allowed <= ${GLIBC_MAX:-n/a})
Dynamic deps     : $(printf '%s ' $DEPS)
Exports          : $(printf '%s ' $EXPORTS)
Licence beside   : $LICENSE_FLAC_NAME (flac/COPYING.Xiph) - BSD-3

Smoke test (smoke-test.c against the stripped file)
------------------------------------------------------------------------------
$SMOKE_OUT
INFO

echo "--- done ---"
echo "  $OUT/$LIB_NAME"
echo "  sha256 $SHA"
echo "  ${ELAPSED}s"
