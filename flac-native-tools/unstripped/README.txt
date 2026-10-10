================================================================================
flac-native-tools/unstripped - the durable home for pre-strip binaries
================================================================================

UNLIKE output/, THIS FOLDER IS COMMITTED. It keeps the unstripped mate of every
codebrix_flac binary that is shipped in the package, so crash dumps of a
stripped release binary can be symbolised after the build machine is gone.
Nothing here is shipped, and nothing here is an input to any build or pack step.

One folder per runtime identifier, mirroring
../../src/CodeBrix.Audio.FlacWriter/runtimes/<rid>/native/:

  <rid>/libcodebrix_flac.so      pre-strip ELF with DWARF (Linux, Android)
  <rid>/libcodebrix_flac.dylib   pre-strip Mach-O (macOS; the .dSYM stays in output/)
  <rid>/codebrix_flac.pdb        the PDB of the shipped DLL (Windows)

The build scripts copy the twin here automatically when they adopt a binary
(ADOPT=1 / no -NoAdopt). Refresh SHA256SUMS after adding one:

    sha256sum */libcodebrix_flac.so */*.dylib */*.pdb > SHA256SUMS   # (whichever exist)

Verify a twin: its sha256 is the "SHA256 unstripped" (or "PDB") line of that
RID in ../BUILD-PROVENANCE.txt, and its build-id equals the shipped file's:

    readelf -n <rid>/libcodebrix_flac.so | grep 'Build ID'
    readelf -n ../../src/CodeBrix.Audio.FlacWriter/runtimes/<rid>/native/libcodebrix_flac.so | grep 'Build ID'

For Windows the link is the PDB's GUID + age: the DLL's CodeView (RSDS) record
names "codebrix_flac.pdb" with a GUID, and the .pdb carries the same GUID.
From a developer prompt:

    dumpbin /headers ../../src/CodeBrix.Audio.FlacWriter/runtimes/<rid>/native/codebrix_flac.dll | findstr RSDS

A debugger (WinDbg, Visual Studio) given this folder as a symbol path matches
the two by that GUID; nothing needs renaming.

STORED SO FAR (build-ids verified equal to the shipped twins by the gates, 2026-10-10):

  linux-x64      45242156ff9f12f1a050cb574d6e06e343e8cdc7
  linux-arm64    afb2d112637d32532445082e6ea7be98a84bf037
  linux-riscv64  7307b4f234156157e80427f4e6aaee5cc320db5c
  android-arm64  ab66ff73828fa32794fc9e1678cec1141842bfcf
  android-x64    bd397d49eb08a6c83fdddfb246718703d4f783cf

  (PDB GUIDs, from the shipped DLLs' RSDS records, 2026-10-10)
  win-x64        {8F098C49-041F-48F3-A6FD-02DEB3FFA3D8} age 1
  win-arm64      {3A9BCA01-50F6-4679-B267-1DEF16CB9A66} age 1

  osx-x64, osx-arm64: not yet built (Jeremy's Mac).
