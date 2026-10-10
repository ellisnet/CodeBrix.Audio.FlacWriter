================================================================================
flac-native-tools - how the codebrix_flac native libraries are built
================================================================================

WHAT IS BUILT
--------------------------------------------------------------------------------
ONE native library per runtime identifier, `codebrix_flac`
(libcodebrix_flac.so / codebrix_flac.dll / libcodebrix_flac.dylib), which
CodeBrix.Audio.FlacWriter P/Invokes. It is the C shim in shim/codebrix_flac.c
with libFLAC (flac-1.5.0, encoder side used) statically linked in. Encoding
only, lossless only; decoding stays the managed FlacFileReader in
CodeBrix.Audio.Core.

The shim implements shim/codebrix_flac.h - THE fixed-ABI contract with the
managed side: opaque handles, int32_t / int64_t / byte pointers only, no libFLAC
type, no callback and no C `long` (32-bit on Windows), so one managed import set
works on every RID. Do not change the header without bumping
CODEBRIX_FLAC_API_VERSION and changing the managed side in the same change.
Only the header's 23 functions are exported (every object, libFLAC's included,
is compiled with hidden visibility); every gate checks the exact export set.

How the shim produces output (details in the comments of codebrix_flac.c):
  * libFLAC's write / seek / tell callbacks live INSIDE the shim, over a virtual
    stream position. Metadata writes (the stream header) always go to the
    output AND to the shim's copy of the header; audio frames go to the output
    (stream mode) or become one packet each (packet mode - libFLAC writes
    exactly one frame per call, so packet boundaries are exact).
  * At finish libFLAC seeks back to rewrite STREAMINFO (total samples, MD5,
    min/max frame sizes) and the SEEKTABLE. Each such write before the current
    end is recorded as a PATCH (offset + bytes; contiguous writes merged) and
    applied to the header copy. The pending output is NOT patched - applying
    the patches is the caller's job (a seekable destination), and a caller that
    cannot seek keeps the placeholders (still a valid FLAC stream).
  * Out-of-range input samples are rejected with CBF_E_INVALID_ARGUMENT before
    libFLAC sees them (libFLAC would otherwise put the encoder into a permanent
    CLIENT_ERROR state); the encoder stays usable.
  * MD5 of the input is always computed (libFLAC's default; its switch is an
    undeclared test hook and is not called). Multithreading is OFF.

THE RULE: SCRIPTS NEVER REACH OUTSIDE THIS REPOSITORY
--------------------------------------------------------------------------------
Every build reads only what is committed here. Nothing is downloaded, cloned, or
installed at build time:
  * flac/ is the unmodified Xiph release tarball, unpacked; the tarball itself
    is in upstream-downloads/ (UPSTREAM.txt has its sha256, the BSD-3 / GPL /
    LGPL / FDL split of its contents, and the no-network verification recipe).
    Never edit that tree; a needed change goes in patches/*.patch, applied to a
    scratch copy.
  * Only libFLAC (BSD-3) is compiled. libFLAC++, the GPL flac + metaflac
    programs, the GPL/LGPL src/share helpers, examples, tests and the FDL docs
    are vendored (they are part of the tarball) but never built or shipped -
    shim/CMakeLists.txt drives flac's own CMake with them OFF and
    EXCLUDE_FROM_ALL.
  * Linux builds run in podman with --network none (and --pull=never). The
    derived images (linux/Containerfile.<arch>) install NOTHING - the pinned
    manylinux base images already carry gcc, cmake, make and binutils - so even
    building the derived image needs no network.
  * Tools on a build machine (podman, the NDK, Visual Studio, Xcode CLT, cmake)
    are prerequisites the scripts check for and NAME when missing; they never
    install them.

WHAT BUILDS WHERE
--------------------------------------------------------------------------------
  RID            script                          machine                 status
  linux-x64      linux/build.sh x64              any Linux + podman      built here
  linux-arm64    linux/build.sh arm64            ditto (+ qemu binfmt)   built here
  linux-riscv64  linux/build.sh riscv64          ditto (+ qemu binfmt)   built here
  android-arm64  android/build.sh arm64          Linux/macOS + NDK 30    built here
  android-x64    android/build.sh x64            Linux/macOS + NDK 30    built here
  win-x64        windows/build-win-x64.ps1       x64 Windows + VS C++    NOT YET BUILT (Jeremy)
  win-arm64      windows/build-win-arm64.ps1     ARM64 Windows (or x64,  NOT YET BUILT (Jeremy)
                                                 cross; smoke test unrun)
  osx-arm64      macos/build-osx-arm64.sh        Apple Silicon Mac       NOT YET BUILT (Jeremy)
  osx-x64        macos/build-osx-x64.sh          Intel Mac, or Apple     NOT YET BUILT (Jeremy)
                                                 Silicon + Rosetta 2
  (host)         build-host.sh                   this Linux dev box      NOT shipped

The Windows and macOS scripts were written on Linux on 2026-10-10 as copies of
CodeBrix.Audio.Vorbis's - which Jeremy ran successfully that day (Windows 11 +
Visual Studio 2026; Apple Silicon Mac + Xcode 27, osx-x64 under Rosetta 2) -
changed only where FLAC differs (one vendored tree built through flac's own
CMake, one licence file, 23 exports, the smoke test built by the same CMake run).
They have NOT been run for FLAC yet. They gate themselves exactly as the Vorbis
ones do; if one breaks, fix it in the script and record it in
BUILD-PROVENANCE.txt.

HOW TO REBUILD
--------------------------------------------------------------------------------
  cd flac-native-tools/linux   && ./build.sh            # all three Linux RIDs
  cd flac-native-tools/android && ./build.sh            # both Android ABIs
       DEVICE_ARM64=<adb serial> DEVICE_X64=<adb serial> ./build.sh   # + on-device smoke test
  cd flac-native-tools\windows ; .\build-win-x64.ps1 ; .\build-win-arm64.ps1
  cd flac-native-tools/macos   && ./build-osx-arm64.sh && ./build-osx-x64.sh
  cd flac-native-tools         && ./build-host.sh       # dev-box .so for the tests

Every script: copies flac/ shim/ smoke-test.c to scratch (patches applied
there), configures shim/CMakeLists.txt with -DCBF_BUILD_SMOKE_TEST=ON and builds
the two targets codebrix_flac + codebrix_flac_smoke, keeps an unstripped twin,
strips, writes LICENSE-Flac.txt beside the binary, runs THE GATE (architecture,
exact exports, system-only dependencies, glibc floor / 16 KB alignment /
deployment target as the platform needs, licence file, and the smoke test
against the STAGED file), writes output/<rid>/BUILD-INFO.txt (including the
libFLAC CPU configuration the RID got), and - only when the gate passed - ADOPTS
the result: copies <binary> + LICENSE-Flac.txt into
../src/CodeBrix.Audio.FlacWriter/runtimes/<rid>/native/ and the unstripped twin
(or .pdb) into unstripped/<rid>/. ADOPT=0 (bash) / -NoAdopt (PowerShell) skips
that. Then update that RID's block in BUILD-PROVENANCE.txt from BUILD-INFO.txt.

THE SMOKE TEST
--------------------------------------------------------------------------------
smoke-test.c loads the library BY PATH (dlopen / LoadLibrary, as .NET does) and
resolves all 23 entry points by name. Unlike Vorbis's it also DECODES: the
codebrix_flac_smoke target links its own static libFLAC and uses ONLY its
FLAC__StreamDecoder to verify the encoder's output sample-exactly (MD5
checked). The library under test keeps its own libFLAC hidden, so the two never
meet. Coverage: stream mode (placeholders, patches inside the header, patched
totals + MD5, read-back header, patched and unpatched streams decoding), packet
mode (header-only output, packet sizes/first samples, header + packets
decoding, byte-identical to a stream-mode encode), verify on, 16- and 24-bit,
levels 0/5/8, an empty stream, non-ASCII comments, and every error path of the
contract. 118 checks; "SMOKE TEST PASSED" + exit 0.

THE LICENCE FILE (identical on all nine RIDs)
--------------------------------------------------------------------------------
  LICENSE-Flac.txt    licenses/LICENSE-Flac.txt = flac/COPYING.Xiph VERBATIM
                      (BSD-3: Josh Coalson 2000-2009, Xiph.Org Foundation
                      2011-2025), written by licenses/compose-license-flac.sh
                      --write.

  There is NO composed-in third-party notice (unlike Vorbis's lpc.c notice).
  A survey of every source compiled into codebrix_flac (src/libFLAC, its
  private headers, include/FLAC, the include/share headers libFLAC includes,
  and src/share/win_utf8_io/win_utf8_io.c on Windows) found only those two
  copyright holders under the COPYING.Xiph terms, plus two public-domain
  dedications with no reproduction condition (src/libFLAC/md5.c and its
  header: Colin Plumb's MD5, "no copyright is claimed"; include/private/
  bitmath.h: a CC0 de Bruijn table by Timothy B. Terriberry). The bash scripts
  run `compose-license-flac.sh --check`, which re-runs that survey (any new
  copyright holder in the compiled set fails the build) and checks the file is
  current, before copying it; every gate compares the copy again. The Windows
  gate checks the copy equals the committed file and flac\COPYING.Xiph.
  .gitattributes here marks flac/** and licenses/LICENSE-Flac.txt -text; the
  repository-root .gitattributes is the place for the matching rule on the
  ADOPTED copies (src/CodeBrix.Audio.FlacWriter/runtimes/** -text), so no
  checkout can change their bytes.

FILES
--------------------------------------------------------------------------------
  flac/                   unmodified Xiph flac-1.5.0 tree (UPSTREAM.txt)
  upstream-downloads/     the release tarball it came from
  shim/                   codebrix_flac.h (contract), codebrix_flac.c, CMakeLists.txt
  smoke-test.c            the gate's load-and-decode program (118 checks)
  licenses/               LICENSE-Flac.txt + its generator / survey
  linux/                  build.sh, container-build.sh, Containerfile.<arch>, pins.env
                          (pins.env also feeds the Windows and macOS scripts)
  android/                build.sh, layout-check.c (ABI static asserts)
  windows/                build-common.ps1, build-win-x64.ps1, build-win-arm64.ps1
  macos/                  build-common.sh, build-osx-arm64.sh, build-osx-x64.sh
  build-host.sh           the not-shipped dev-box build
  patches/                empty (see its README)
  unstripped/             committed pre-strip twins for crash triage
  output/                 build results, git-ignored (except its README)
  BUILD-PROVENANCE.txt    what was built, when, with what, and the result
