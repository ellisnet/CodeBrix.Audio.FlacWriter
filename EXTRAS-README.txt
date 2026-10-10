================================================================================
EXTRAS-README: CodeBrix.Audio.FlacWriter
Samples, tools and other content in this repository that is not part of a NuGet
package
================================================================================

Exactly one project is packable - src/CodeBrix.Audio.FlacWriter - and
everything else listed below exists only to build, test or document it. None of
it is included in the CodeBrix.Audio.FlacWriter.BsdLicenseForever package (the
package carries only the native BINARIES that flac-native-tools produces, under
runtimes/<rid>/native/).

For runnable, compilable usage of the library, read the test project: the
"WORKING EXAMPLES ON GITHUB" section of AGENT-README.txt maps each feature area
to the test file that exercises it.


FLAC-NATIVE-TOOLS
=================
    flac-native-tools/

Everything needed to build the native codebrix_flac library for every runtime
identifier, from this repository alone:
  - flac/: the vendored, unmodified Xiph.Org FLAC release source tree
    (upstream-downloads/ holds the release tarball; UPSTREAM.txt records its
    identity and hash). Only libFLAC (src/libFLAC + include/FLAC) is ever
    compiled from it;
  - shim/: codebrix_flac.h (the fixed-ABI contract with the managed assembly)
    and the C shim + CMake build that links a static libFLAC into one shared
    library;
  - the per-platform build scripts (Linux manylinux containers, Android NDK,
    Windows MSVC, macOS) and the smoke test;
  - BUILD-PROVENANCE.txt: one record per RID actually built.

Start with flac-native-tools/README.txt. MAINTAINER-README.txt (NATIVE
LIBRARIES) has the build matrix: which machine builds which RID.


TEST PROJECT
============
    tests/CodeBrix.Audio.FlacWriter.Tests/

xUnit v3; run it as described in MAINTAINER-README.txt (TESTING). It copies the
committed natives into its output and round-trips generated tones through the
encoder and two independent decoders: CodeBrix.Audio.Core's FlacFileReader and,
where installed, the ffmpeg / ffprobe programs (those checks skip, with the
reason, on a machine without them).


ANDROID DIAGNOSTICS SAMPLE
==========================
    samples/FlacDiagnostics/

A small .NET for Android app (API 33 and later) that proves the package on a
real device: it registers the encoder, encodes generated tones in stream mode
(16-bit, and 24-bit at level 8, verify on) and in packet mode, decodes them
back with CodeBrix.Audio.Core's FlacFileReader and compares them sample-exact
with the quantised input, plays one audibly through CodeBrix.Audio.Android, and
prints "FLAC-DIAG RESULT: PASS" (also to logcat, tag FlacDiag). It references
the library by ProjectReference, which does NOT carry the runtimes natives into
an APK, so its csproj adds explicit AndroidNativeLibrary items for
android-arm64 and android-x64; an application that references the published
package needs nothing of the kind. Its README.txt has the adb install / launch /
log-collection commands. Build it in Release with the android workload
installed; it is in the solution's Samples folder.
