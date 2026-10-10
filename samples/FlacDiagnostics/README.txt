================================================================================
FlacDiagnostics - physical-device proof for CodeBrix.Audio.FlacWriter on Android
================================================================================

A one-screen .NET for Android app (Android 13 / API 33 and later, arm64-v8a and
x86_64). One second after launch - or when "Run FLAC diagnostics" is tapped -
it:

  1. calls CodeBrixAudioFlac.Register() and logs NativeLibraryVersion and
     NativeLibraryPath;
  2. generates tones in managed code (no recordings): 3 s of 440 Hz stereo at
     48 kHz and 2 s of 660 Hz mono at 44.1 kHz, both -6 dBFS;
  3. encodes them with FlacEncoder in stream mode to a FileStream in the app's
     FilesDir (so the STREAMINFO totals and MD5 are patched in) - the first at
     16-bit, the second at 24-bit compression level 8, verify on for both -
     and encodes the first tone again with FlacPacketEncoder (packet mode),
     checking that the packets' SampleCount values add up to the frame count,
     that FirstSample is contiguous, that the header starts with "fLaC" and a
     34-byte STREAMINFO, and that header + packets written to a third file are
     byte-identical to the stream-mode file; it logs bytes, FramesWritten,
     Duration and elapsed time, and copies each file to the app's external
     files folder so adb can pull it;
  4. decodes each file back with CodeBrix.Audio.Core's FlacFileReader and
     compares every sample with the quantised input (sample-exact), logging
     channels, sample rate, bit depth, frame counts, RMS and peak;
  5. plays the first file AUDIBLY through AudioFilePlayer on the shared output
     that CodeBrix.Audio.Android provides (volume 0.5 of a -6 dBFS tone - turn
     the device volume down first), logging start and end;
  6. ends with "FLAC-DIAG RESULT: PASS" or "FLAC-DIAG RESULT: FAIL: <reason>".

Every line is shown on screen and written to logcat with the tag FlacDiag.


HOW THE NATIVE REACHES THE APK
==============================
The sample references the library as SOURCE (a ProjectReference to
src/CodeBrix.Audio.FlacWriter). A ProjectReference does NOT carry the library's
runtimes/<rid>/native/ files into an Android build: they are pack-only items in
the library csproj, so they reach the .nupkg but not a referencing project's
output, and an APK built that way has no libcodebrix_flac.so (the first call
then fails to load the native). The sample csproj therefore names the two
Android natives itself as AndroidNativeLibrary items with Abi metadata
(arm64-v8a and x86_64), which packs them into lib/<abi>/ of the APK.

An application that consumes the PACKAGE needs none of this: a PackageReference
to CodeBrix.Audio.FlacWriter.BsdLicenseForever delivers
runtimes/android-arm64/native and runtimes/android-x64/native into lib/<abi>/
of the APK automatically.

CodeBrix.Audio.Android.ApacheLicenseForever (from nuget.org) is the ONLY audio
natives package referenced - it supplies device output and the playback
decoders. CodeBrix.Audio.Core.MitLicenseForever arrives transitively.


BUILD, INSTALL, RUN
===================
Needs the .NET 10 Android workload, an Android SDK with the API 36 platform and
a JDK the workload accepts (the csproj defaults AndroidSdkDirectory to
$HOME/Android/Sdk and JavaSdkDirectory to the Debian JDK 21 path when those
exist, as CodeBrix.Audio.Android's diagnostics app does).

    dotnet build samples/FlacDiagnostics/FlacDiagnostics.csproj -c Release

One APK carries both ABIs:
    samples/FlacDiagnostics/bin/Release/net10.0-android36.1/
        com.codebrix.audio.flacwriter.diagnostics-Signed.apk

    adb -s <serial> install -r <that apk>
    adb -s <serial> logcat -c
    adb -s <serial> shell am start -n \
        com.codebrix.audio.flacwriter.diagnostics/crc64ec483f79701ee775.MainActivity
    (wait about 10 seconds)
    adb -s <serial> logcat -d -s FlacDiag

The encoded files (the Release build is not debuggable, so run-as cannot read
FilesDir; the app copies them out):

    adb -s <serial> pull \
        /sdcard/Android/data/com.codebrix.audio.flacwriter.diagnostics/files/ .

When finished:

    adb -s <serial> shell am force-stop com.codebrix.audio.flacwriter.diagnostics
