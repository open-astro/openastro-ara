#!/usr/bin/env bash
# Plate-solve check for the bundled astap_cli: solves a real frame with the D05 star database
# and checks the answer, then checks that the thread count does not change the result.
#
#   scripts/ci-astap-solve.sh <astap_cli> <d05-database-dir>
#
# The fixture (scripts/tests/fixtures/astap/ngc7000-ha-bin4.fits.gz) is a 60 s Ha frame of
# NGC 7000 (ASI2600MM on a 448 mm refractor), mean-binned 4x4 to 1562x1044 so it fits in the
# repo; 6.9"/px, 3.0 x 2.0 degrees. The expected solution below is what Han Kleijn's own
# v2026.11.01 build and the `ara` branch both give, hinted and blind, at 1 and 8 threads.
#
# A sync of open-astro/astap `ara` with Han's main (and any Dependabot bump of the submodule)
# has to keep all of this passing; OPENASTRO.md in the submodule has the fuller hand-run gate.
set -euo pipefail

ASTAP="${1:?usage: ci-astap-solve.sh <astap_cli> <d05-database-dir>}"
DB="${2:?usage: ci-astap-solve.sh <astap_cli> <d05-database-dir>}"
ASTAP="$(cd "$(dirname "$ASTAP")" && pwd)/$(basename "$ASTAP")"
DB="$(cd "$DB" && pwd)"
FIXTURE="$(cd "$(dirname "$0")" && pwd)/tests/fixtures/astap/ngc7000-ha-bin4.fits.gz"
[ -x "$ASTAP" ] || { echo "error: $ASTAP is not an executable" >&2; exit 2; }
ls "$DB"/d05_*.1476 > /dev/null 2>&1 || { echo "error: no D05 tiles (d05_*.1476) in $DB" >&2; exit 2; }

EXPECT_RA=314.038369     # CRVAL1, degrees
EXPECT_DEC=43.639075     # CRVAL2, degrees
EXPECT_SCALE=6.9014      # arcsec per pixel, from the CD matrix
TOL_CENTRE=3             # arcsec
TOL_SCALE=0.001          # relative

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
gunzip -c "$FIXTURE" > "$WORK/frame.fits"
cd "$WORK"

HINT=(-fov 2.01 -r 10)   # the frame has RA/DEC of the mount in its header
BLIND=(-fov 2.01 -r 180 -ra 10 -spd 90)

solve() { # name, then astap_cli options
    local name=$1; shift
    rm -f "$name.ini" "$name.wcs" "$name.log"
    local t0 t1
    t0=$(date +%s.%N)
    "$ASTAP" -f frame.fits -d "$DB" -log -o "$name" "$@" > "$name.out" 2>&1 || true
    t1=$(date +%s.%N)
    printf '%-12s %6.2f s  %s\n' "$name" "$(echo "$t1 - $t0" | bc)" "$(stars "$name")"
}
stars() { /usr/bin/grep -aoE '[0-9]+ stars, [0-9]+ quads' "$1.log" 2>/dev/null | tr '\n' ' ' || true; }
solution() { /usr/bin/grep -aE '^(PLTSOLVD|CRPIX|CRVAL|CD[12]_|A_|B_|AP_|BP_)' "$1.ini" 2>/dev/null || true; }
fail() { echo "✗ $*" >&2; FAILED=1; }
FAILED=0

check_answer() { # name
    if ! /usr/bin/grep -q '^PLTSOLVD=T' "$1.ini" 2>/dev/null; then
        fail "$1: not solved"; sed 's/^/    /' "$1.out" >&2 || true; return
    fi
    python3 - "$1.ini" "$EXPECT_RA" "$EXPECT_DEC" "$EXPECT_SCALE" "$TOL_CENTRE" "$TOL_SCALE" <<'EOF' || FAILED=1
import math, sys
ini, ra0, dec0, scale0, tol_c, tol_s = sys.argv[1], *map(float, sys.argv[2:])
v = dict(l.split("=", 1) for l in open(ini, encoding="latin-1") if "=" in l)
ra, dec = float(v["CRVAL1"]), float(v["CRVAL2"])
scale = math.hypot(float(v["CD1_1"]), float(v["CD2_1"])) * 3600
sep = math.degrees(math.acos(min(1.0,
    math.sin(math.radians(dec)) * math.sin(math.radians(dec0)) +
    math.cos(math.radians(dec)) * math.cos(math.radians(dec0)) * math.cos(math.radians(ra - ra0))))) * 3600
bad = sep > tol_c or abs(scale / scale0 - 1) > tol_s
print(f"{'✗' if bad else '✓'} {ini[:-4]}: centre {ra:.6f} {dec:+.6f} ({sep:.2f}\" off), {scale:.4f}\"/px")
sys.exit(1 if bad else 0)
EOF
}

same_as() { # reference, name, what is compared
    if [ "$(solution "$1")" != "$(solution "$2")" ] || [ "$(stars "$1")" != "$(stars "$2")" ]; then
        fail "$2: $3 differs from $1"
        diff <(solution "$1"; stars "$1") <(solution "$2"; stars "$2") | sed 's/^/    /' >&2 || true
    else
        echo "✓ $2: same $3 as $1"
    fi
}

echo "astap_cli: $("$ASTAP" -h 2>&1 | head -1 || true)"
echo "cores: $(nproc)"

# Hinted, the solve every capture makes: one thread, an odd split and every core.
solve hinted_1 -z 0 "${HINT[@]}" -threads 1
solve hinted_3 -z 0 "${HINT[@]}" -threads 3
solve hinted_n -z 0 "${HINT[@]}"
check_answer hinted_1
same_as hinted_1 hinted_3 solution
same_as hinted_1 hinted_n solution

# Blind: the threaded spiral search over the whole sky.
solve blind_1 -z 0 "${BLIND[@]}" -threads 1
solve blind_n -z 0 "${BLIND[@]}"
check_answer blind_n
same_as blind_1 blind_n solution

# 2x2 binning in astap_cli (threaded in `ara`): too coarse to solve this frame, but the stars
# and quads it finds must not depend on the thread count.
solve bin2_1 -z 2 "${HINT[@]}" -threads 1
solve bin2_3 -z 2 "${HINT[@]}" -threads 3
solve bin2_n -z 2 "${HINT[@]}"
[ -n "$(stars bin2_1)" ] || fail "bin2_1: no star count in the log"
same_as bin2_1 bin2_3 "stars and quads"
same_as bin2_1 bin2_n "stars and quads"

[ "$FAILED" = 0 ] || { echo "✗ astap_cli plate-solve check failed" >&2; exit 1; }
echo "✓ astap_cli plate-solve check passed"
