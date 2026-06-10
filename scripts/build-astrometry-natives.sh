#!/usr/bin/env bash
# §14e — builds the SOFA + NOVAS31 astrometry natives from the vendored C sources so the
# cross-epoch coordinate transforms (Coordinates.Transform J2000<->JNOW) work on non-Windows
# hosts. The managed side P/Invokes "SOFAlib.dll"/"NOVAS31lib.dll"; on Linux/macOS the
# AstrometryNativeResolver maps those to the libsofa/libnovas31 binaries this script produces.
#
# Usage: scripts/build-astrometry-natives.sh <output-dir>
set -euo pipefail

OUT="${1:?usage: build-astrometry-natives.sh <output-dir>}"
mkdir -p "$OUT"

case "$(uname -s)" in
    Darwin) EXT="dylib"; SHARED="-dynamiclib" ;;
    *)      EXT="so";    SHARED="-shared" ;;
esac

CC="${CC:-cc}"
# The vendored headers hardcode `#define EXPORT __declspec(dllexport)` (NINA's Windows-DLL
# fork of the upstream sources). Define __declspec away on the command line — symbols are
# default-visible in a -fPIC shared build, so the exports survive without source edits.
CFLAGS="-O2 -fPIC -fno-strict-aliasing -D__declspec(x)="

# NOVAS 3.1 — the same source set the Windows NOVAS31.vcxproj compiles (minus the example/
# checkout driver programs, which carry main()): core + constants + nutation + the JPL
# ephemeris-file solarsystem implementation (solsys1 + eph_manager + readeph0).
NOVAS_SRC=(
    NOVAS31/NOVAS31/novas.c
    NOVAS31/NOVAS31/novascon.c
    NOVAS31/NOVAS31/nutation.c
    NOVAS31/NOVAS31/eph_manager.c
    NOVAS31/NOVAS31/readeph0.c
    NOVAS31/NOVAS31/solsys1.c
)
echo "building libnovas31.$EXT"
# shellcheck disable=SC2086
"$CC" $CFLAGS $SHARED -o "$OUT/libnovas31.$EXT" "${NOVAS_SRC[@]}" -lm

# SOFA — every release source except the t_sofa_c.c validation driver (carries main()).
echo "building libsofa.$EXT"
SOFA_SRC=()
for f in SOFA/SOFA/src/*.c; do
    [[ "$(basename "$f")" == "t_sofa_c.c" ]] && continue
    SOFA_SRC+=("$f")
done
# shellcheck disable=SC2086
"$CC" $CFLAGS $SHARED -o "$OUT/libsofa.$EXT" "${SOFA_SRC[@]}" -lm

echo "built into $OUT:"
ls -la "$OUT"/libnovas31."$EXT" "$OUT"/libsofa."$EXT"
