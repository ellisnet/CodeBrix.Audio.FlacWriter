#!/usr/bin/env bash
# ==============================================================================================
# android/build.sh - build the two Android codebrix_flac native libraries, from this repo only
# ==============================================================================================
#
#   ./build.sh                build both (android-arm64 = arm64-v8a, android-x64 = x86_64)
#   ./build.sh arm64          android-arm64 only
#   ./build.sh x64            android-x64 only
#
# Options (environment variables):
#   ANDROID_NDK=<dir>         the NDK (default: ~/Android/Sdk/ndk/30.0.16248370). Its
#                             source.properties Pkg.Revision MUST equal the pin below.
#   ADOPT=0                   do NOT copy the gated result into the package (default ADOPT=1)
#   DEVICE_ARM64=<serial>     optional: adb-push the smoke test + library to this arm64 device,
#   DEVICE_X64=<serial>       run it there (in /data/local/tmp/codebrix-flac-smoke, removed
#                             afterwards) and record the result. Unset = not run on a device.
#
# What it does, per ABI: copies flac/ shim/ smoke-test.c into a fixed scratch folder
# (patches/*.patch applied to the copy, never to the vendored tree), configures
# shim/CMakeLists.txt (which builds libFLAC through flac's own CMake) with the NDK's
# android.toolchain.cmake (API 33, -Wl,-z,max-page-size=16384) and CBF_BUILD_SMOKE_TEST=ON,
# builds codebrix_flac + codebrix_flac_smoke, keeps the unstripped twin, strips with llvm-strip,
# cross-compiles android/layout-check.c (ABI static asserts), then GATES the result:
#   ELF64 / right machine, every PT_LOAD aligned to >= 16 KB (Android 15+ 16 KB-page devices),
#   no W+X segment, Android API note == 33, NEEDED only libc/libm/libdl, no TEXTREL / RPATH /
#   RUNPATH, SONAME libcodebrix_flac.so, EXACTLY the 23 header exports, build-id equal in the
#   stripped and unstripped files, licence file verbatim.
# It then writes LICENSE-Flac.txt beside the binary and BUILD-INFO.txt, and (ADOPT=1) copies the
# two files into src/CodeBrix.Audio.FlacWriter/runtimes/<rid>/native/ and the unstripped twin
# into unstripped/<rid>/.
#
# Uses the host's own cmake + make and the NDK; reads only this repository; installs and downloads
# nothing. Copied from CodeBrix.Audio.Vorbis's vorbis-native-tools/android/build.sh and adapted.
# ==============================================================================================

set -euo pipefail

trap 'rc=$?; echo "ERROR: android/build.sh failed (exit $rc) at line $LINENO: $BASH_COMMAND" >&2' ERR

first_line() { sed -n '1p'; }
# sha256 of a file on Linux (sha256sum) and macOS (shasum -a 256) alike
sha256_of() { if command -v sha256sum > /dev/null 2>&1; then sha256sum "$@"; else shasum -a 256 "$@"; fi; }

NDK_PIN=30.0.16248370
API=33
LIB_NAME=libcodebrix_flac.so
LICENSE_FLAC_NAME=LICENSE-Flac.txt
ALLOWED_NEEDED="libc.so libm.so libdl.so"
REQUIRED_SYMBOLS="codebrix_flac_api_version codebrix_flac_library_version codebrix_flac_encoder_create codebrix_flac_encoder_add_comment codebrix_flac_encoder_set_padding codebrix_flac_encoder_set_seek_table codebrix_flac_encoder_set_packet_mode codebrix_flac_encoder_start codebrix_flac_encoder_write_int32 codebrix_flac_encoder_finish codebrix_flac_encoder_pending_output codebrix_flac_encoder_read_output codebrix_flac_encoder_header_length codebrix_flac_encoder_read_header codebrix_flac_encoder_patch_count codebrix_flac_encoder_patch_info codebrix_flac_encoder_read_patch codebrix_flac_encoder_pending_packets codebrix_flac_encoder_packet_info codebrix_flac_encoder_read_packet codebrix_flac_encoder_samples_written codebrix_flac_encoder_last_state codebrix_flac_encoder_destroy"
FLAC_VERSION=1.5.0

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
TOOLS_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
REPO_DIR="$(cd "$TOOLS_DIR/.." && pwd)"
OUTPUT_DIR="$TOOLS_DIR/output"
RUNTIMES_DIR="$REPO_DIR/src/CodeBrix.Audio.FlacWriter/runtimes"
UNSTRIPPED_DIR="$TOOLS_DIR/unstripped"
NDK="${ANDROID_NDK:-$HOME/Android/Sdk/ndk/$NDK_PIN}"

# ----------------------------------------------------------------------------------------------
# Prerequisites - checked, never installed
# ----------------------------------------------------------------------------------------------
[ -f "$NDK/source.properties" ] || { echo "ERROR: no NDK at $NDK (set ANDROID_NDK). This script installs nothing." >&2; exit 1; }
NDK_REVISION="$(sed -n 's/^Pkg.Revision *= *//p' "$NDK/source.properties" | tr -d '\r' | first_line)"
[ "$NDK_REVISION" = "$NDK_PIN" ] || { echo "ERROR: NDK $NDK_PIN is pinned; $NDK is $NDK_REVISION." >&2; exit 1; }
case "$(uname -s)" in
    Linux)  NDK_HOST=linux-x86_64 ;;
    Darwin) NDK_HOST=darwin-x86_64 ;;
    *) echo "ERROR: run on Linux or macOS." >&2; exit 1 ;;
esac
TOOLCHAIN="$NDK/toolchains/llvm/prebuilt/$NDK_HOST/bin"
TOOLCHAIN_FILE="$NDK/build/cmake/android.toolchain.cmake"
for t in cmake make; do
    command -v "$t" > /dev/null 2>&1 || { echo "ERROR: $t is not installed (this script installs nothing)." >&2; exit 1; }
done
for t in llvm-readelf llvm-nm llvm-strip llvm-objdump; do
    [ -x "$TOOLCHAIN/$t" ] || { echo "ERROR: $TOOLCHAIN/$t missing from the NDK." >&2; exit 1; }
done
[ -f "$TOOLCHAIN_FILE" ] || { echo "ERROR: $TOOLCHAIN_FILE missing from the NDK." >&2; exit 1; }
for f in flac/src/libFLAC/stream_encoder.c flac/CMakeLists.txt flac/COPYING.Xiph shim/CMakeLists.txt \
         shim/codebrix_flac.c shim/codebrix_flac.h smoke-test.c android/layout-check.c \
         licenses/LICENSE-Flac.txt licenses/compose-license-flac.sh; do
    [ -f "$TOOLS_DIR/$f" ] || { echo "ERROR: $TOOLS_DIR/$f is missing; restore it from git." >&2; exit 1; }
done
CMAKE_ACTUAL="$(cmake --version | first_line)"
MAKE_ACTUAL="$(make --version | first_line)"

abi_of()     { case "$1" in arm64) echo arm64-v8a;; x64) echo x86_64;; esac; }
triple_of()  { case "$1" in arm64) echo aarch64-linux-android;; x64) echo x86_64-linux-android;; esac; }
machine_of() { case "$1" in arm64) echo AArch64;; x64) echo "Advanced Micro Devices X86-64";; esac; }

# ----------------------------------------------------------------------------------------------
# The gate (inspection only - nothing is executed on the build host)
# ----------------------------------------------------------------------------------------------
GATE_FAILED=0
fail() { echo "  [FAIL] $1"; GATE_FAILED=1; }
pass() { echo "  [ok] $1"; }

build_id_of() { { "$TOOLCHAIN/llvm-readelf" --notes "$1" 2>/dev/null || true; } | awk '/Build ID/{print $3}' | first_line; }

verify_lib() {
    local lib="$1" arch="$2" header machine loads bad_align wx api dyn needed unexpected exports funcs want missing extra
    header="$("$TOOLCHAIN/llvm-readelf" -h "$lib")"
    machine="$(printf '%s\n' "$header" | awk -F: '/Machine:/{gsub(/^ +/,"",$2); print $2}')"
    if printf '%s\n' "$header" | grep -q 'Class:.*ELF64' && [ "$machine" = "$(machine_of "$arch")" ] \
       && printf '%s\n' "$header" | grep -q 'Type:.*DYN'; then
        pass "ELF64 shared object, $machine"
    else
        fail "wrong ELF class/type/machine: $machine"
    fi

    # Program headers: every PT_LOAD aligned to >= 16 KB (power of two), offset == vaddr mod 16 KB,
    # never writable AND executable.
    loads="$("$TOOLCHAIN/llvm-readelf" -lW "$lib" | awk '$1=="LOAD"{print $2, $3, $(NF-1), $NF}')"
    bad_align=""; wx=""
    LOAD_ALIGNMENTS=""
    while read -r off vaddr flags align; do
        [ -n "$off" ] || continue
        a=$((align)); o=$((off)); v=$((vaddr))
        LOAD_ALIGNMENTS="$LOAD_ALIGNMENTS $align"
        if [ "$a" -lt 16384 ] || [ $((a & (a - 1))) -ne 0 ] || [ $((o % 16384)) -ne $((v % 16384)) ]; then
            bad_align="$bad_align $align@$off"
        fi
        case "$flags" in *W*E*) wx="$wx $flags" ;; esac
    done <<< "$loads"
    LOAD_ALIGNMENTS="${LOAD_ALIGNMENTS# }"
    [ -n "$LOAD_ALIGNMENTS" ] || bad_align="no PT_LOAD"
    if [ -z "$bad_align" ]; then pass "PT_LOAD alignments: $LOAD_ALIGNMENTS (16 KB page-size ready)"; else fail "PT_LOAD not 16 KB aligned:$bad_align"; fi
    if [ -z "$wx" ]; then pass "no writable+executable segment"; else fail "W+X segment:$wx"; fi

    # Android API level note (NT_ANDROID_IDENT, type 1, owner "Android").
    api="$("$TOOLCHAIN/llvm-readelf" --notes "$lib" 2>/dev/null | awk '/Android/{f=1} f && /description data:/{print; exit}' | sed 's/.*data: *//' || true)"
    # the first 4 description bytes are the little-endian API level
    set -- $api
    ANDROID_API_NOTE=""
    [ $# -ge 4 ] && ANDROID_API_NOTE="$(printf '%d' "0x$4$3$2$1" 2>/dev/null || true)"
    if [ "$ANDROID_API_NOTE" = "$API" ]; then pass "Android API note: $ANDROID_API_NOTE"; else fail "Android API note: '$ANDROID_API_NOTE' (raw '$api'), expected $API"; fi

    dyn="$("$TOOLCHAIN/llvm-readelf" --dynamic "$lib")"
    needed="$(printf '%s\n' "$dyn" | sed -n 's/.*(NEEDED).*\[\(.*\)\].*/\1/p' | sort | tr '\n' ' ' | sed 's/ *$//')"
    unexpected=""
    for d in $needed; do case " $ALLOWED_NEEDED " in *" $d "*) ;; *) unexpected="$unexpected $d" ;; esac; done
    NEEDED_RECORD="$needed"
    if [ -z "$unexpected" ] && [ -n "$needed" ]; then pass "NEEDED system-only: $needed"; else fail "unexpected NEEDED:$unexpected"; fi
    if printf '%s\n' "$dyn" | grep -Eq '\((TEXTREL|RPATH|RUNPATH)\)'; then fail "TEXTREL/RPATH/RUNPATH present"; else pass "no TEXTREL / RPATH / RUNPATH"; fi
    if printf '%s\n' "$dyn" | grep -Eq '\(SONAME\).*\[libcodebrix_flac\.so\]'; then pass "SONAME libcodebrix_flac.so"; else fail "wrong SONAME"; fi
    if "$TOOLCHAIN/llvm-readelf" --version-info "$lib" | grep -q GLIBC_; then fail "desktop glibc dependency"; fi

    exports="$("$TOOLCHAIN/llvm-nm" -D --defined-only "$lib" | awk '{print $NF}' | sed 's/@.*//' | sort -u)"
    funcs="$("$TOOLCHAIN/llvm-nm" -D --defined-only "$lib" | awk '$2=="T"||$2=="W"{print $NF}' | sed 's/@.*//' | sort -u)"
    want="$(printf '%s\n' $REQUIRED_SYMBOLS | sort -u)"
    missing="$(comm -23 <(printf '%s\n' "$want") <(printf '%s\n' "$funcs") | tr '\n' ' ' | sed 's/ *$//')"
    extra="$(comm -13 <(printf '%s\n' "$want") <(printf '%s\n' "$exports") | tr '\n' ' ' | sed 's/ *$//')"
    if [ -z "$missing" ]; then pass "all $(printf '%s\n' $REQUIRED_SYMBOLS | grep -c .) codebrix_flac_* functions exported"; else fail "missing exports: $missing"; fi
    if [ -z "$extra" ]; then pass "nothing else exported"; else fail "unexpected exports: $extra"; fi
}

adopt_rid() {
    local rid="$1" src="$OUTPUT_DIR/$1" dest="$RUNTIMES_DIR/$1/native"
    if [ "${ADOPT:-1}" = "0" ]; then echo "  adopt     : skipped (ADOPT=0)"; return 0; fi
    mkdir -p "$dest" "$UNSTRIPPED_DIR/$rid"
    cp "$src/$LIB_NAME" "$src/$LICENSE_FLAC_NAME" "$dest/"
    cp "$src/unstripped/$LIB_NAME" "$UNSTRIPPED_DIR/$rid/$LIB_NAME"
    echo "  adopt     : -> src/CodeBrix.Audio.FlacWriter/runtimes/$rid/native/ ($LIB_NAME + $LICENSE_FLAC_NAME)"
    echo "              -> flac-native-tools/unstripped/$rid/$LIB_NAME"
}

run_on_device() {
    local serial="$1" dest="$2" dir=/data/local/tmp/codebrix-flac-smoke out rc
    DEVICE_RESULT="not run (set DEVICE_ARM64 / DEVICE_X64 to an adb serial)"
    [ -n "$serial" ] || return 0
    command -v adb > /dev/null 2>&1 || { fail "DEVICE requested but adb is not on PATH"; return 0; }
    echo "  --- smoke test on device $serial ---"
    adb -s "$serial" shell "rm -rf $dir && mkdir -p $dir" > /dev/null
    adb -s "$serial" push "$dest/$LIB_NAME" "$dest/smoke-test" "$dir/" > /dev/null 2>&1
    out="$(adb -s "$serial" shell "chmod 755 $dir/smoke-test && $dir/smoke-test $dir/$LIB_NAME; echo EXIT=\$?" 2>&1 | tr -d '\r')" || true
    adb -s "$serial" shell "rm -rf $dir" > /dev/null || true
    printf '%s\n' "$out" | sed 's/^/    /'
    rc="$(printf '%s\n' "$out" | sed -n 's/^EXIT=//p' | tail -1)"
    local model abi
    model="$(adb -s "$serial" shell getprop ro.product.model | tr -d '\r')"
    abi="$(adb -s "$serial" shell getprop ro.product.cpu.abi | tr -d '\r')"
    if [ "$rc" = "0" ]; then
        pass "smoke test PASSED on $serial ($model, $abi)"
        DEVICE_RESULT="PASSED on $serial ($model, $abi) at $(date -u '+%Y-%m-%d %H:%M:%S UTC')"
    else
        fail "smoke test failed on $serial (exit '$rc')"
        DEVICE_RESULT="FAILED on $serial ($model, $abi), exit '$rc'"
    fi
    DEVICE_OUTPUT="$out"
}

build_abi() {
    local arch="$1" abi rid triple scratch src build dest started cc
    abi="$(abi_of "$arch")"; rid="android-$arch"; triple="$(triple_of "$arch")"
    started="$SECONDS"
    GATE_FAILED=0
    DEVICE_OUTPUT=""
    # A FIXED scratch path so recorded flags are identical on every run; prefix maps keep it out
    # of the binaries anyway.
    scratch="$TOOLS_DIR/build-scratch/$rid"
    src="$scratch/src"; build="$scratch/build"; dest="$OUTPUT_DIR/$rid"
    cc="$TOOLCHAIN/$triple$API-clang"

    echo
    echo "=============================================================================="
    echo " BUILD $rid ($abi, API $API, NDK $NDK_REVISION)"
    echo "=============================================================================="
    rm -rf "$scratch" "$dest"
    mkdir -p "$src" "$build" "$dest/unstripped"
    cp -a "$TOOLS_DIR/flac" "$src/flac"
    cp -a "$TOOLS_DIR/shim" "$src/shim"
    cp -a "$TOOLS_DIR/smoke-test.c" "$src/smoke-test.c"
    PATCHES_APPLIED="none"
    if ls "$TOOLS_DIR/patches"/*.patch > /dev/null 2>&1; then
        PATCHES_APPLIED=""
        for p in "$TOOLS_DIR/patches"/*.patch; do
            ( cd "$src" && patch -p1 --forward --batch < "$p" ) || { echo "ERROR: patch $(basename "$p") did not apply." >&2; exit 1; }
            PATCHES_APPLIED="$PATCHES_APPLIED $(basename "$p")"
        done
        PATCHES_APPLIED="${PATCHES_APPLIED# }"
    fi
    echo "  patches applied: $PATCHES_APPLIED"

    C_FLAGS="-g -ffile-prefix-map=$src=flac-native-tools -ffile-prefix-map=$build=build -ffile-prefix-map=$NDK=ndk -fdebug-compilation-dir=build"
    LINK_FLAGS="-Wl,-z,max-page-size=16384 -Wl,-z,common-page-size=16384 -Wl,--build-id=sha1"
    cmake -S "$src/shim" -B "$build" -G "Unix Makefiles" \
        -DCMAKE_TOOLCHAIN_FILE="$TOOLCHAIN_FILE" \
        -DANDROID_ABI="$abi" -DANDROID_PLATFORM="android-$API" \
        -DCMAKE_BUILD_TYPE=Release \
        -DCBF_BUILD_SMOKE_TEST=ON \
        -DCMAKE_C_FLAGS="$C_FLAGS" \
        -DCMAKE_SHARED_LINKER_FLAGS="$LINK_FLAGS" \
        -DCMAKE_EXE_LINKER_FLAGS="-Wl,-z,max-page-size=16384 -Wl,-z,common-page-size=16384" > "$build/configure.log" 2>&1 \
        || { echo "ERROR: cmake configure failed:" >&2; tail -40 "$build/configure.log" >&2; exit 1; }
    cmake --build "$build" -j "$(nproc 2>/dev/null || sysctl -n hw.ncpu)" --target codebrix_flac codebrix_flac_smoke -- VERBOSE=1 > "$build/build.log" 2>&1 \
        || { echo "ERROR: build failed:" >&2; grep -E -B2 -A8 'error:' "$build/build.log" | head -60 >&2; exit 1; }
    SHIM_WARNINGS="$(grep 'warning:' "$build/build.log" | grep -c -E 'codebrix_flac\.c|smoke-test\.c' || true)"
    ALL_WARNINGS="$(grep -c 'warning:' "$build/build.log" || true)"
    FLAC_CONFIG="$(grep -E '^(FLAC__CPU_X86_64|FLAC__CPU_IA32|FLAC__CPU_ARM64|FLAC__HAS_X86INTRIN|FLAC__HAS_NEONINTRIN|FLAC__HAS_A64NEONINTRIN|WITH_ASM|WITH_AVX|ENABLE_64_BIT_WORDS|ENABLE_MULTITHREADING|WITH_OGG|BUILD_SHARED_LIBS|HAVE_PTHREAD):' "$build/CMakeCache.txt" | sed 's/:[A-Z]*=/=/' | tr '\n' ' ' | sed 's/ *$//')"
    echo "  built; warnings: shim + smoke test $SHIM_WARNINGS, total incl. libFLAC $ALL_WARNINGS"
    echo "  libFLAC configuration: $FLAC_CONFIG"

    # ABI static asserts, on the TARGET's data model.
    "$cc" -std=c11 -Wall -Werror -I"$src/shim" -c "$SCRIPT_DIR/layout-check.c" -o "$build/layout-check.o"
    pass "layout-check.c compiled for $abi (ABI static asserts hold)"

    cp "$build/$LIB_NAME" "$dest/unstripped/$LIB_NAME"
    cp "$build/$LIB_NAME" "$dest/$LIB_NAME"
    "$TOOLCHAIN/llvm-strip" --strip-unneeded "$dest/$LIB_NAME"
    cp "$build/codebrix_flac_smoke" "$dest/smoke-test"
    "$TOOLCHAIN/llvm-strip" --strip-unneeded "$dest/smoke-test"
    bash "$TOOLS_DIR/licenses/compose-license-flac.sh" --check
    cp "$TOOLS_DIR/licenses/$LICENSE_FLAC_NAME" "$dest/$LICENSE_FLAC_NAME"

    echo "--- verifying (stripped) ---"
    verify_lib "$dest/$LIB_NAME" "$arch"
    echo "--- verifying (unstripped twin) ---"
    verify_lib "$dest/unstripped/$LIB_NAME" "$arch"
    BID="$(build_id_of "$dest/$LIB_NAME")"; BID_U="$(build_id_of "$dest/unstripped/$LIB_NAME")"
    if [ -n "$BID" ] && [ "$BID" = "$BID_U" ]; then pass "build-id $BID identical in both"; else fail "build-id mismatch '$BID' vs '$BID_U'"; fi
    if cmp -s "$dest/$LICENSE_FLAC_NAME" <(bash "$TOOLS_DIR/licenses/compose-license-flac.sh") && cmp -s "$dest/$LICENSE_FLAC_NAME" "$TOOLS_DIR/flac/COPYING.Xiph"; then
        pass "licence file exact (LICENSE-Flac.txt = flac/COPYING.Xiph)"
    else
        fail "licence file differs"
    fi
    # re-run the stripped inspection last so the recorded values are the shipped file's
    verify_lib "$dest/$LIB_NAME" "$arch" > /dev/null

    case "$arch" in arm64) run_on_device "${DEVICE_ARM64:-}" "$dest" ;; x64) run_on_device "${DEVICE_X64:-}" "$dest" ;; esac

    if [ "$GATE_FAILED" -ne 0 ]; then
        echo "VERIFICATION FAILED for $rid; output/$rid left for inspection, NOT adopted." >&2
        exit 1
    fi

    SIZE_STRIPPED="$(wc -c < "$dest/$LIB_NAME" | tr -d ' ')"
    SIZE_UNSTRIPPED="$(wc -c < "$dest/unstripped/$LIB_NAME" | tr -d ' ')"
    SHA="$(sha256_of "$dest/$LIB_NAME" | cut -d' ' -f1)"
    SHA_U="$(sha256_of "$dest/unstripped/$LIB_NAME" | cut -d' ' -f1)"
    SHIM_COMPILE_LINE="$(grep -E 'codebrix_flac\.c' "$build/build.log" | grep -E ' -c ' | first_line | sed "s|$src|<src>|g; s|$build|<build>|g; s|$NDK|<ndk>|g")"
    LINK_LINE="$(grep -E ' -shared ' "$build/build.log" | first_line | sed "s|$src|<src>|g; s|$build|<build>|g; s|$NDK|<ndk>|g")"
    ( cd "$dest" && sha256_of "$LIB_NAME" "unstripped/$LIB_NAME" "$LICENSE_FLAC_NAME" smoke-test > SHA256SUMS.txt )
    cat > "$dest/BUILD-INFO.txt" <<INFO
codebrix_flac native library - build information
==============================================================================
RID              : $rid  ($abi)
Built            : $(date -u '+%Y-%m-%d %H:%M:%S UTC')
Built by         : flac-native-tools/android/build.sh on $(uname -s) $(uname -m)
NDK              : $NDK_REVISION (pinned $NDK_PIN)
Compiler         : $("$cc" --version | first_line)
cmake / make     : $CMAKE_ACTUAL / $MAKE_ACTUAL (host)
Toolchain file   : <ndk>/build/cmake/android.toolchain.cmake, ANDROID_ABI=$abi, ANDROID_PLATFORM=android-$API

Source (vendored in-repo; nothing fetched at build time)
------------------------------------------------------------------------------
flac (libFLAC)   : $FLAC_VERSION (flac/)
Shim             : shim/codebrix_flac.c + codebrix_flac.h (API version 1)
Patches applied  : $PATCHES_APPLIED

Configuration
------------------------------------------------------------------------------
CMAKE_C_FLAGS    : $(printf '%s' "$C_FLAGS" | sed "s|$src|<src>|g; s|$build|<build>|g; s|$NDK|<ndk>|g")  (on top of the NDK's Release flags)
Linker flags     : $LINK_FLAGS
shim compile     : $SHIM_COMPILE_LINE
link             : $LINK_LINE
libFLAC config   : $FLAC_CONFIG
Warnings         : shim + smoke test $SHIM_WARNINGS; total incl. libFLAC $ALL_WARNINGS
Strip            : llvm-strip --strip-unneeded

Result
------------------------------------------------------------------------------
File             : $LIB_NAME (SONAME $LIB_NAME)
Build-id         : $BID
Size (stripped)  : $SIZE_STRIPPED bytes
Size (unstripped): $SIZE_UNSTRIPPED bytes -> unstripped/$LIB_NAME
SHA256           : $SHA
SHA256 unstripped: $SHA_U
PT_LOAD align    : $LOAD_ALIGNMENTS
Android API note : $ANDROID_API_NOTE
NEEDED           : $NEEDED_RECORD
Layout check     : layout-check.c static asserts passed for $abi
Licence beside   : $LICENSE_FLAC_NAME (flac/COPYING.Xiph) - BSD-3
Device smoke test: $DEVICE_RESULT
$( [ -n "$DEVICE_OUTPUT" ] && printf '%s\n' "$DEVICE_OUTPUT" | sed 's/^/    /' )
INFO
    rm -rf "$scratch"
    rmdir "$TOOLS_DIR/build-scratch" 2>/dev/null || true
    echo "  $rid: $SIZE_STRIPPED bytes, sha256 $SHA"
    adopt_rid "$rid"
    echo "  $rid finished in $(( SECONDS - started ))s"
}

TARGET="${1:-all}"
echo "NDK        : $NDK ($NDK_REVISION)"
echo "API level  : $API"
echo "cmake/make : $CMAKE_ACTUAL / $MAKE_ACTUAL"
case "$TARGET" in
    arm64|x64) build_abi "$TARGET" ;;
    all)       for a in arm64 x64; do build_abi "$a"; done ;;
    *) echo "usage: $0 [all|arm64|x64]" >&2; exit 2 ;;
esac
