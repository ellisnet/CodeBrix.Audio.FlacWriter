flac-native-tools/patches/
================================================================================
EMPTY ON PURPOSE. As of 2026-10-10 flac-1.5.0 (libFLAC) is built exactly as
released.

If a change to the Xiph source is ever unavoidable, it goes here as a unified
diff named NNNN-flac-<what>.patch, made relative to flac-native-tools/ (so paths
start with flac/). Every build script copies flac/ and shim/ into a scratch
folder and applies every *.patch here with `patch -p1 --forward --batch` (in
name order; `git apply -p1` on Windows) to THAT copy; a patch that does not
apply fails the build. flac/ itself is never edited.
Record any patch in UPSTREAM.txt and BUILD-PROVENANCE.txt.
