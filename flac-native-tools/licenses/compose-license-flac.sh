#!/usr/bin/env bash
# ==============================================================================================
# compose-license-flac.sh - THE generator of licenses/LICENSE-Flac.txt (one source of truth)
# ==============================================================================================
#
#   ./compose-license-flac.sh            print the composed text to stdout
#   ./compose-license-flac.sh --write    (re)write licenses/LICENSE-Flac.txt
#   ./compose-license-flac.sh --check    exit 1 unless licenses/LICENSE-Flac.txt is current AND the
#                                        compiled-source notice survey below still holds
#
# LICENSE-Flac.txt, shipped beside every codebrix_flac binary on all nine RIDs, is flac/COPYING.Xiph
# VERBATIM (libFLAC's BSD-3 licence: Josh Coalson 2000-2009, Xiph.Org Foundation 2011-2025) and
# nothing else.
#
# Why nothing else (survey of 2026-10-10, flac-1.5.0): unlike libvorbis (whose lib/lpc.c carries a
# preserved third-party notice that must travel with the binary), the sources compiled into
# codebrix_flac - src/libFLAC/*.c, src/libFLAC/include/**, include/FLAC/*.h, the include/share
# headers libFLAC includes (alloc, compat, endswap, macros, private, safe_str, win_utf8_io) and, on
# Windows only, src/share/win_utf8_io/win_utf8_io.c - name only two copyright holders, Josh Coalson
# and the Xiph.Org Foundation, under the COPYING.Xiph text. The two other notices found are
# public-domain dedications with no condition to reproduce:
#   - src/libFLAC/md5.c + src/libFLAC/include/private/md5.h: Colin Plumb's MD5, "no copyright is
#     claimed ... This code is in the public domain";
#   - src/libFLAC/include/private/bitmath.h: a de Bruijn table, "(C) Timothy B. Terriberry ...
#     CC0 (Public domain)" (and compiled only for compilers without a count-leading-zeros builtin).
# The GPL / LGPL / FDL texts in flac/ cover the flac + metaflac programs, parts of src/share and
# the documentation - none of which is compiled.
#
# --check re-runs that survey (every "Copyright" line in the compiled set must name Josh Coalson or
# the Xiph.Org Foundation, or be the BSD-3 boilerplate) so a future flac upgrade that brings in a
# new holder fails loudly here instead of shipping an incomplete licence file.
#
# Every build script (linux, android, macos, build-host.sh) runs --check before copying
# licenses/LICENSE-Flac.txt beside the binary; the Windows scripts copy the committed file (and
# check it equals flac/COPYING.Xiph). Reads only this repository.
# ==============================================================================================
set -euo pipefail

TOOLS_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
FLAC_DIR="$TOOLS_DIR/flac"
COPYING="$FLAC_DIR/COPYING.Xiph"
TARGET="$TOOLS_DIR/licenses/LICENSE-Flac.txt"

compose() {
    [ -f "$COPYING" ] || { echo "ERROR: flac/COPYING.Xiph missing" >&2; exit 1; }
    cat "$COPYING"
}

survey() {
    local files unexpected
    files=$(cd "$FLAC_DIR" && { ls src/libFLAC/*.c; find src/libFLAC/include include/FLAC -name '*.h';
             ls include/share/{alloc,compat,endswap,macros,private,safe_str,win_utf8_io}.h src/share/win_utf8_io/win_utf8_io.c; })
    unexpected=$(cd "$FLAC_DIR" && grep -H -i 'copyright' $files \
        | grep -v -E 'Josh Coalson|Xiph\.Org Foundation|above copyright|COPYRIGHT HOLDERS AND CONTRIBUTORS|no copyright is claimed' || true)
    if [ -n "$unexpected" ]; then
        echo "ERROR: a compiled libFLAC source names a copyright holder this licence file does not cover:" >&2
        echo "$unexpected" >&2
        echo "Review it and, if a notice must be preserved, compose it in here (as Vorbis does for lpc.c)." >&2
        exit 1
    fi
}

case "${1:-}" in
    "")      compose ;;
    --write) compose > "$TARGET"; echo "wrote $TARGET" ;;
    --check)
        survey
        tmp="$(mktemp)"; compose > "$tmp"
        if cmp -s "$tmp" "$TARGET"; then rm -f "$tmp"; echo "licenses/LICENSE-Flac.txt is current (compiled-source notice survey clean)"
        else rm -f "$tmp"; echo "ERROR: licenses/LICENSE-Flac.txt is stale or missing - run licenses/compose-license-flac.sh --write" >&2; exit 1; fi ;;
    *) echo "usage: $0 [--write|--check]" >&2; exit 2 ;;
esac
