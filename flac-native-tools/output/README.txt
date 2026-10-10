flac-native-tools/output/ - build results (NOT committed, except this README)
================================================================================
Every build script writes here, one folder per RID:

  output/<rid>/<library>          the stripped library, exactly as the package ships it
  output/<rid>/LICENSE-Flac.txt   licenses/LICENSE-Flac.txt = flac/COPYING.Xiph (BSD-3)
  output/<rid>/unstripped/        the pre-strip twin (Linux / Android / macOS)
  output/<rid>/BUILD-INFO.txt     toolchain, flags, libFLAC configuration, sizes, sha256,
                                  gate results, the smoke test's output
  output/<rid>/smoke-test*        the gate's smoke-test program (codebrix_flac_smoke, built
                                  for the TARGET by the same CMake run; on Windows also kept
                                  so a cross-built win-arm64 can finish its gate on a device)

  output/host/libcodebrix_flac.so   a plain build with this machine's own cmake + cc, for
                                    running the managed tests on a Linux dev box. NEVER shipped
                                    (its glibc floor is whatever the dev box has).
                                    Rebuild:  ./build-host.sh

See ../README.txt.
