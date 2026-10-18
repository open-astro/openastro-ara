#!/usr/bin/env bash
# §14e — builds the ERFA + SuperNOVAS astrometry natives from the git submodules under
# third_party/ so the cross-epoch coordinate transforms (Coordinates.Transform J2000<->JNOW),
# the rise/set bodies and the Julian-date helpers work on Linux/macOS. The managed side
# P/Invokes "erfa" and "supernovas"; the runtime probes liberfa.{so,dylib} and
# libsupernovas.{so,dylib} next to the assembly, which is where this script's output is staged.
#
# ERFA (liberfa/erfa) is the BSD-licensed, maintained fork of IAU SOFA with the same API under an
# era* prefix. SuperNOVAS (Smithsonian/SuperNOVAS) is the maintained fork of NOVAS C 3.1 with the
# same entry points (struct layouts differ — NOVAS.cs mirrors SuperNOVAS, not NOVAS 3.1).
# Both are pinned by submodule commit; Dependabot's gitsubmodule entry bumps them.
#
# Usage: scripts/build-astrometry-natives.sh <output-dir>
set -euo pipefail

OUT="${1:?usage: build-astrometry-natives.sh <output-dir>}"
mkdir -p "$OUT"

ERFA=third_party/erfa
SNOVAS=third_party/supernovas
for dir in "$ERFA/src" "$SNOVAS/src/c99"; do
    if [ ! -d "$dir" ] || [ -z "$(ls -A "$dir")" ]; then
        echo "error: $dir is empty — the astrometry sources are git submodules; run" >&2
        echo "       git submodule update --init" >&2
        exit 2
    fi
done

case "$(uname -s)" in
    Darwin) EXT="dylib"; SHARED="-dynamiclib" ;;
    *)      EXT="so";    SHARED="-shared" ;;
esac

CC="${CC:-cc}"
# Array form so every flag stays individually quoted (a future flag with a space can't break
# word-splitting silently).
CFLAGS=(-O2 -fPIC -fno-strict-aliasing)

# ERFA — every source except the t_erfa_c*.c validation drivers (they carry main()).
# erfaversion.c reads its version macros from autotools' config.h; we have no configure run,
# so take them from configure.ac the way the autotools build would (eraVersion() is what the
# daemon's boot probe logs).
ERFA_VERSION=$(sed -n 's/^AC_INIT(\[erfa\],\[\(.*\)\])/\1/p' "$ERFA/configure.ac")
SOFA_VERSION=$(sed -n 's/.*AC_DEFINE(\[SOFA_VERSION\], \[\"\(.*\)\"\].*/\1/p' "$ERFA/configure.ac")
if [ -z "$ERFA_VERSION" ] || [ -z "$SOFA_VERSION" ]; then
    echo "error: could not read the ERFA/SOFA version from $ERFA/configure.ac" >&2
    exit 2
fi
IFS=. read -r ERFA_MAJOR ERFA_MINOR ERFA_MICRO <<<"$ERFA_VERSION"
ERFA_SRC=()
for f in "$ERFA"/src/*.c; do
    case "$(basename "$f")" in t_erfa_c*.c) continue ;; esac
    ERFA_SRC+=("$f")
done
echo "building liberfa.$EXT (ERFA $ERFA_VERSION, SOFA $SOFA_VERSION)"
"$CC" "${CFLAGS[@]}" $SHARED -I"$ERFA/src" \
    "-DPACKAGE_VERSION=\"$ERFA_VERSION\"" \
    "-DPACKAGE_VERSION_MAJOR=$ERFA_MAJOR" "-DPACKAGE_VERSION_MINOR=$ERFA_MINOR" "-DPACKAGE_VERSION_MICRO=$ERFA_MICRO" \
    "-DSOFA_VERSION=\"$SOFA_VERSION\"" \
    -o "$OUT/liberfa.$EXT" "${ERFA_SRC[@]}" -lm

# SuperNOVAS — the library's own source list (config.mk SOURCES) plus the legacy JPL
# ephemeris-file reader (solsys1 + eph_manager, USER_SOLSYS=1) that NOVAS.cs's ephem_open /
# solarsystem_hp expect for External/JPLEPH. WITHOUT_CURL: no network fetch of IERS data —
# the daemon is offline-first and Dut1Table carries its own snapshot.
# The core list is read from upstream's config.mk (SOURCES = a.c b.c \ ... continued lines), so a
# submodule bump that adds or renames a file is picked up, and an unreadable list stops here
# with the cause named instead of a missing-symbol link error.
SNOVAS_SRC=()
for f in $(sed -n '/^SOURCES = /,/[^\\]$/p' "$SNOVAS/config.mk" | sed 's/^SOURCES = //; s/\\$//'); do
    [ -f "$SNOVAS/src/c99/$f" ] || { echo "error: $SNOVAS/config.mk lists $f but src/c99/$f does not exist" >&2; exit 2; }
    SNOVAS_SRC+=("$SNOVAS/src/c99/$f")
done
if [ "${#SNOVAS_SRC[@]}" -lt 10 ]; then
    echo "error: could not read the SOURCES list from $SNOVAS/config.mk (got ${#SNOVAS_SRC[@]} files)" >&2
    exit 2
fi
# Upstream bug in the legacy reader (SuperNOVAS v1.7.2, legacy/eph_manager.c interpolate()):
# the sub-interval Chebyshev time uses C99 remainder(), which rounds to nearest and goes
# negative for fractions above one half, where NOVAS 3.1 used fmod(). Any body stored in more
# than one sub-interval per record (Earth-Moon barycenter: 2 in DE421) comes back from the wrong
# half of the record — the Sun was 14° off in altitude (NovasEphemerisAgreementTest). Patched
# here into a temporary copy (never under $OUT: that is the publish dir CI ships); the grep makes
# a fixed upstream fail this script so the patch is removed rather than silently skipped.
PATCHED=$(mktemp -d)
trap 'rm -rf "$PATCHED"' EXIT
if [ "$(grep -o 'remainder(temp, 1.0)' "$SNOVAS/legacy/eph_manager.c" | wc -l | tr -d ' ')" != 1 ]; then
    echo "error: $SNOVAS/legacy/eph_manager.c no longer has the remainder() line this script patches — drop the patch" >&2
    exit 2
fi
sed 's/remainder(temp, 1.0)/fmod(temp, 1.0)/' "$SNOVAS/legacy/eph_manager.c" > "$PATCHED/eph_manager.c"
SNOVAS_SRC+=("$SNOVAS/legacy/solsys1.c" "$PATCHED/eph_manager.c")
echo "building libsupernovas.$EXT"
"$CC" "${CFLAGS[@]}" $SHARED -I"$SNOVAS/include" -I"$SNOVAS/legacy" -DWITHOUT_CURL -DUSER_SOLSYS=1 \
    -o "$OUT/libsupernovas.$EXT" "${SNOVAS_SRC[@]}" -lm

echo "built into $OUT:"
ls -la "$OUT"/liberfa."$EXT" "$OUT"/libsupernovas."$EXT"
