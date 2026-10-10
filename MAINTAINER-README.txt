================================================================================
MAINTAINER-README: CodeBrix.Audio.FlacWriter
Notes for people and agents MAINTAINING this repository - not for package
consumers
================================================================================

If you are CONSUMING the NuGet package, stop reading and open AGENT-README.txt
instead. Everything below is about the repository itself: how it is laid out,
how it builds, how it is tested, how it is packaged, how the native libraries
are produced, and the conventions the source follows.


PURPOSE AND SCOPE
=================
PackageId:      CodeBrix.Audio.FlacWriter.BsdLicenseForever
Assembly:       CodeBrix.Audio.FlacWriter (net10.0)
Project:        src/CodeBrix.Audio.FlacWriter/CodeBrix.Audio.FlacWriter.csproj
License:        BSD-3-Clause (LICENSE); the compiled libFLAC code is also
                BSD-3-Clause
Consumer doc:   AGENT-README.txt

A lossless FLAC ENCODER: one managed assembly that P/Invokes one native library
per runtime identifier, named codebrix_flac (libcodebrix_flac.so,
libcodebrix_flac.dylib, codebrix_flac.dll). Decoding is NOT here - it is the
managed FlacFileReader in CodeBrix.Audio.Core.

The root namespace is CodeBrix.Audio.FlacWriter, NOT CodeBrix.Audio.Flac:
CodeBrix.Audio.Core already owns a CodeBrix.Audio.Flac namespace (its decoder
internals), and a second assembly contributing types to it would confuse
consumers.

The native contract is flac-native-tools/shim/codebrix_flac.h. It is the
single source of truth for the managed/native boundary: fixed-ABI (no C long,
no libFLAC structs, NO callbacks - libFLAC's write/seek/tell callbacks live
inside the shim over an internal buffer), versioned by
CODEBRIX_FLAC_API_VERSION. The managed side (Interop/FlacLibrary.cs,
ExpectedApiVersion) refuses a library reporting any other version. Change the
header only together with the shim, every native build, Interop/FlacNative.cs
and the version constant on both sides.

Three things the FLAC contract has that the sibling Vorbis contract does not:
  - PATCHES. libFLAC rewrites STREAMINFO (and the SEEKTABLE) at finish by
    seeking back. The shim records each rewrite as (offset, bytes);
    FlacEncoder.Flush applies them to the caller's stream when it CanSeek
    (offsets relative to the stream position at construction) and restores
    the position. A non-seekable stream keeps the placeholders.
  - HEADER READ-BACK. The shim keeps its own copy of the stream header with
    the patches applied; StreamHeader exposes it (cached at finish, so it
    outlives Dispose).
  - PACKET MODE. One FLAC frame per packet, exactly as libFLAC hands frames to
    its write callback (never scanned for), with first-sample and sample
    count. FlacPacketEncoder drains them.


REPOSITORY LAYOUT
=================
src/CodeBrix.Audio.FlacWriter/
    CodeBrixAudioFlac.cs             Register / EnsureLoaded / diagnostics
    FlacEncoder.cs                   stream mode (Stream out, patches)
    FlacPacketEncoder.cs             packet mode
    FlacPacket.cs                    one packet (readonly struct)
    FlacEncoderOptions.cs            settings + validation + Clone
    FlacEncoderException.cs          NativeCode + NativeState
    FlacAudioFileWriterFactory.cs    the ".flac" IAudioFileWriterFactory
    Internal/FlacNativeEncoder.cs    the shared native-encoder core (create,
                                     comments, padding, seek table, start,
                                     chunked writes, header, patches, packets)
    Internal/FlacSampleConverter.cs  float -> int quantisation
    Internal/FlacArguments.cs        channel / sample-rate range checks
    Internal/FlacStreamAudioFileWriter.cs   IAudioFileWriter adapter
    Interop/FlacLibrary.cs           native loader + DllImportResolver
    Interop/FlacNative.cs            LibraryImport declarations (the header)
    Interop/FlacEncoderHandle.cs     SafeHandle over the native encoder
    Interop/FlacResult.cs            CBF_* codes, messages, last_state
    InternalsVisibleTo.cs            -> CodeBrix.Audio.FlacWriter.Tests
    runtimes/<rid>/native/           COMMITTED native binaries, each beside
                                     LICENSE-Flac.txt
tests/CodeBrix.Audio.FlacWriter.Tests/   xUnit v3 test project
samples/FlacDiagnostics/             Android device-proof app (EXTRAS-README.txt);
                                     in the solution's Samples folder
flac-native-tools/                   everything that builds the natives (see
                                     NATIVE LIBRARIES below and its README.txt)

Root files: CodeBrix.Audio.FlacWriter.slnx, global.json, LICENSE, README.md,
AGENT-README.txt, MAINTAINER-README.txt, EXTRAS-README.txt, README-INDEX.txt,
THIRD-PARTY-NOTICES.txt, icon-codebrix-128.png, .gitignore, .gitattributes, and
the 8 AI-agent pointer stubs (AGENTS.md, CLAUDE.md, .clinerules, .cursorrules,
.windsurfrules, .cursor/rules/agent-readme.mdc, .github/copilot-instructions.md,
.junie/guidelines.md - byte-identical to the family canonical copies).

.gitattributes marks the committed natives and their licence texts as binary /
-text, so a Windows checkout with core.autocrlf=true cannot rewrite the licence
bytes that are packed beside every native. Do not remove it.

The .slnx "Solution Items" folder lists the canonical ten files: .gitignore,
AGENT-README.txt, EXTRAS-README.txt, global.json, icon-codebrix-128.png,
LICENSE, MAINTAINER-README.txt, README-INDEX.txt, README.md,
THIRD-PARTY-NOTICES.txt. Its "Samples" folder holds
samples/FlacDiagnostics; its "Tests" folder holds the test project.

global.json selects the Microsoft.Testing.Platform test runner; it pins no SDK.


BUILDING
========
    dotnet build --configuration Release CodeBrix.Audio.FlacWriter.slnx

Expect 0 warnings and 0 errors (Debug and Release). GenerateDocumentationFile
is on, so CS1591 is fixed by writing the doc comment, never with NoWarn.
AllowUnsafeBlocks is on for the source-generated LibraryImport interop.

The managed build does NOT build natives; it packs and copies whatever is
committed under src/CodeBrix.Audio.FlacWriter/runtimes/.


TESTING
=======
xUnit v3 + SilverAssertions. The test runner IS Microsoft.Testing.Platform,
selected by the root global.json - do not delete it; without it `dotnet test`
falls back to the VSTest bridge, which fails on the .NET 10 SDK.

    dotnet test --solution CodeBrix.Audio.FlacWriter.slnx
    dotnet test --solution CodeBrix.Audio.FlacWriter.slnx -c Release

If `dotnet test --solution` reports zero tests (a known SDK 10.0.4xx quirk),
run the test assembly directly:

    dotnet tests/CodeBrix.Audio.FlacWriter.Tests/bin/Debug/net10.0/CodeBrix.Audio.FlacWriter.Tests.dll

The test csproj copies src/CodeBrix.Audio.FlacWriter/runtimes/** into its
output under runtimes/<rid>/native/ (None Remove/Include glob), so the tests
load exactly the committed binary through the library's own resolver -
nothing is installed on the machine. The loader probes the output folder
itself BEFORE runtimes/<rid>/native/, so a stray libcodebrix_flac.so left in
bin/ (for example a host build copied there by hand) shadows the committed one:
delete it before trusting a run.

Two independent decoders check every round trip:
  - CodeBrix.Audio.Core's FlacFileReader (pinned in the test csproj at the same
    version as the library's Core reference - raise both together), compared
    SAMPLE-EXACT with the input (float input after the same quantisation);
  - the ffmpeg / ffprobe programs, when they are on the PATH: decoded bytes
    compared exactly, "-v error" must print nothing, ffprobe must report flac
    and the right channels / rate / bit depth / duration. Without them those
    tests SKIP with the reason.
STREAMINFO is parsed in the test (FlacStreamHeader.cs) and its MD5 compared
with one computed in the test. All signals are generated in code; there are no
audio assets and none may be added without an identified licence.

No coverage collector is referenced (the family dropped coverlet.collector).


PACKAGING / PUBLISHING
======================
The csproj carries the family's canonical date-stamped version block
(1.<years since 2026>.<day of year>.<minute of day>, UTC); never hardcode a
<Version>. GeneratePackageOnBuild is on, so every build drops a fresh .nupkg in
src/CodeBrix.Audio.FlacWriter/bin/<Configuration>/.

The nupkg contains: lib/net10.0/CodeBrix.Audio.FlacWriter.dll + .xml,
README.md, AGENT-README.txt, THIRD-PARTY-NOTICES.txt, LICENSE,
icon-codebrix-128.png, and runtimes/<rid>/native/{binary, LICENSE-Flac.txt} for
every RID committed. The runtimes are packed with PackagePath="runtimes\"
(BARE - NuGet appends the recursive directory itself; spelling %(RecursiveDir)
out doubles the path). After any csproj change, unzip -l the Release nupkg and
check every runtimes/<rid>/native/ path appears once and correctly.

Do not publish until all nine RIDs are committed (see the matrix below) - a
package missing a RID fails at run time on that platform. Jeremy publishes.


NATIVE LIBRARIES
================
All native build tooling lives in flac-native-tools/ - read
flac-native-tools/README.txt before touching it. Per-RID build records
(toolchain, container digest, hashes, SIMD paths enabled, date) are in
flac-native-tools/BUILD-PROVENANCE.txt; upstream identity, tarball hash and the
licence split of the tarball are in flac-native-tools/UPSTREAM.txt.

Build matrix - which machine builds which RID:

    RID              Built where                     How
    ---------------  ------------------------------  ---------------------------
    linux-x64        this Linux dev machine          manylinux container
    linux-arm64      this Linux dev machine          manylinux container
    linux-riscv64    this Linux dev machine          manylinux container
    android-arm64    this Linux dev machine          Android NDK
    android-x64      this Linux dev machine          Android NDK
    win-x64          Jeremy, on Windows              MSVC script
    win-arm64        Jeremy, on Windows              MSVC script (cross from x64)
    osx-arm64        Jeremy, on a Mac                Xcode/clang script
    osx-x64          Jeremy, on a Mac                Xcode/clang script

Each build writes the binary plus LICENSE-Flac.txt (FLAC's COPYING.Xiph,
verbatim, composed by one generator in flac-native-tools/licenses/) and is
copied into src/CodeBrix.Audio.FlacWriter/runtimes/<rid>/native/. Only libFLAC
is compiled (static, single-threaded, no Ogg, no programs, no C++); the GPL
programs in the vendored tarball are never built. A host build made for local
testing (flac-native-tools/output/host/) is never committed into runtimes/.

THE NEVER-REACH-OUTSIDE RULE. Every build script reads only from this
repository: the vendored flac/ tree, the shim, the scripts. No downloads, no
git clones, no package installs at build time; containers run with networking
disabled. The repository must be able to rebuild every native years from now
with nothing but the toolchains named in BUILD-PROVENANCE.txt.
flac-native-tools/flac/ is never modified; a needed source change is applied
to a scratch copy by the scripts.


CODING CONVENTIONS
==================
- File layout: usings at the top (System.* first, alphabetical), one blank line,
  file-scoped namespace, one blank line, the type. No global usings, no
  ImplicitUsings, no #nullable.
- Folder = namespace (Interop/ -> CodeBrix.Audio.FlacWriter.Interop, Internal/
  -> CodeBrix.Audio.FlacWriter.Internal). The public types live at the root
  namespace. Do not add a folder/namespace called "Encoding" (it would shadow
  System.Text.Encoding inside this assembly).
- NRT is off: no `?` on reference types, no `!`. Value-type nullables are fine.
- XML doc comments on every public and protected member; no NoWarn.
- Native signatures stay blittable (pointers, ints) so LibraryImport generates
  no marshalling.
- Tests: <Class>Tests.cs, snake_case method names (Member_does_thing or
  plain_snake_case), //Arrange //Act //Assert in multi-statement bodies,
  expression-bodied single-statement tests, SilverAssertions fluent style,
  TestContext.Current.CancellationToken on any cancellable call.


PROVENANCE / VENDORED SOURCES
=============================
flac-native-tools/flac/ is the unmodified Xiph.Org FLAC release tarball; the
tarball is in flac-native-tools/upstream-downloads/. Details and the full
licence text: THIRD-PARTY-NOTICES.txt and flac-native-tools/UPSTREAM.txt. The
managed code and the shim are original to this repository.


NOTES
=====
- FlacEncoder.Flush() ENDS the stream (there is no mid-stream flush); Encode
  after Flush throws InvalidOperationException, after Dispose
  ObjectDisposedException. FlacPacketEncoder.Dispose does NOT finish.
- Verify defaults to TRUE (decode-while-encoding). CBF_E_VERIFY_MISMATCH
  surfaces as FlacEncoderException with IsVerifyMismatch and a message saying
  the output must not be kept.
- The streamable-subset rules are libFLAC's, not re-implemented here: a
  non-subset block size / bit depth with StreamableSubset on comes back as
  NativeCode -4 with NativeState ...NOT_STREAMABLE. The managed side checks
  only the absolute ranges.
- EncodeInt32 range-checks every sample before anything is encoded (an
  out-of-range value would otherwise corrupt the stream when Verify is off).
- Header + packets from FlacPacketEncoder are byte-identical to the stream-mode
  file for the same input and options; a test pins this.
- The registry writer carries a partial frame between Write calls; a partial
  frame still held at Finish is dropped.
- CodeBrix.Audio.Core is pinned in BOTH csprojs (library and tests); raise them
  together.


================================================================================
END OF MAINTAINER-README
================================================================================
