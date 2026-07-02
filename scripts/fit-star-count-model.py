#!/usr/bin/env python3
# NEXTGEN §3.1 slice 2 — regenerate the StarCountModel constants from the canonical
# HYG snapshot (hygdata_v40.csv.gz, sha256 8e3ff9e67445e558a759b117910850cff1b1d4d4
# 92f45f715c2ee2db3d869bac — the same digest DataManagerService pins for hyg-stars).
#
# Method (design/NEXTGEN_PLANNING.md §3.1, grid + trigger pinned in #663):
#   * Pool star counts over ALL galactic longitudes in a ±5° |b| band around each of
#     the 7 fixed latitudes {0,10,20,30,50,70,90}° (pooling averages out spiral-arm /
#     cluster clumping a smooth model shouldn't capture).
#   * Cumulative surface densities N(<m)/deg² for m ∈ {5..9}.
#   * Per-band log-linear fit of log10 N on m ∈ {5..8}; the VALIDATION GATE
#     extrapolates each fit to m=9 and requires it within a factor of 2 of the actual
#     pooled count at EVERY band — out-of-sample in the extrapolation direction.
#   * The shipped model anchors at the ACTUAL m=9 densities and extrapolates with the
#     fitted slope (strictly more accurate than the raw fit; the gate exists to prove
#     the log-linear FORM before trusting its slope beyond the data).
#
# Usage: python3 scripts/fit-star-count-model.py /path/to/hygdata_v40.csv.gz
# (The file installs via the Data Manager as {profileDir}/sky-data/hyg-stars/catalog.csv
#  — gunzip'd; this script accepts either the .csv.gz or the plain .csv.)

import csv
import gzip
import hashlib
import math
import sys

EXPECTED_SHA = "8e3ff9e67445e558a759b117910850cff1b1d4d492f45f715c2ee2db3d869bac"
NGP_RA = math.radians(192.85948)   # J2000 north galactic pole
NGP_DEC = math.radians(27.12825)
BANDS = [0, 10, 20, 30, 50, 70, 90]
MAGS = [5, 6, 7, 8, 9]
SPHERE_DEG2_PER_SR_ZONE = 20626.4806  # (180/pi)^2 * 2*pi


def band_area_deg2(latitude):
    lo, hi = max(0.0, latitude - 5), min(90.0, latitude + 5)
    # Both hemispheres of the |b| band (the b=0 band [0,5] doubled equals the
    # single [-5,+5] zone, so doubling is uniform).
    return 2 * SPHERE_DEG2_PER_SR_ZONE * (
        math.sin(math.radians(hi)) - math.sin(math.radians(lo)))


def main():
    if len(sys.argv) != 2:
        sys.exit("usage: fit-star-count-model.py <hygdata_v40.csv[.gz]>")
    path = sys.argv[1]
    opener = gzip.open if path.endswith(".gz") else open
    if path.endswith(".gz"):
        digest = hashlib.sha256(open(path, "rb").read()).hexdigest()
        if digest != EXPECTED_SHA:
            sys.exit(f"digest mismatch: {digest} != pinned {EXPECTED_SHA} — "
                     "constants must only regenerate from the canonical snapshot")

    counts = {L: {m: 0 for m in MAGS} for L in BANDS}
    with opener(path, "rt") as f:
        for row in csv.DictReader(f):
            try:
                mag = float(row["mag"])
                ra = math.radians(float(row["ra"]) * 15)   # hours -> deg -> rad
                dec = math.radians(float(row["dec"]))
            except (ValueError, KeyError):
                continue
            if row.get("proper") == "Sol" or mag < -20:
                continue
            sin_b = (math.sin(NGP_DEC) * math.sin(dec)
                     + math.cos(NGP_DEC) * math.cos(dec) * math.cos(ra - NGP_RA))
            b = abs(math.degrees(math.asin(max(-1.0, min(1.0, sin_b)))))
            for latitude in BANDS:
                if max(0.0, latitude - 5) <= b <= min(90.0, latitude + 5):
                    for m in MAGS:
                        if mag < m:
                            counts[latitude][m] += 1

    print("// Pooled densities N(<m)/deg² per band (m = 5..9):")
    gate_ok = True
    n9, slopes = [], []
    for latitude in BANDS:
        area = band_area_deg2(latitude)
        dens = {m: counts[latitude][m] / area for m in MAGS}
        print(f"//  |b|={latitude:2d}: "
              + ", ".join(f"{dens[m]:.6f}" for m in MAGS))
        xs = [5, 6, 7, 8]
        ys = [math.log10(dens[m]) for m in xs]
        mean_x, mean_y = sum(xs) / 4, sum(ys) / 4
        slope = (sum((x - mean_x) * (y - mean_y) for x, y in zip(xs, ys))
                 / sum((x - mean_x) ** 2 for x in xs))
        intercept = mean_y - slope * mean_x
        ratio = 10 ** (intercept + slope * 9) / dens[9]
        status = "OK" if 0.5 <= ratio <= 2.0 else "FAIL"
        if status == "FAIL":
            gate_ok = False
        print(f"//    fit slope {slope:.6f}, m=9 extrapolation ratio {ratio:.3f} {status}")
        n9.append(dens[9])
        slopes.append(slope)

    print("// VALIDATION GATE:", "PASS" if gate_ok else "FAIL")
    print("BandSinB  =", [round(math.sin(math.radians(L)), 6) for L in BANDS])
    print("DensityAt9 =", [round(v, 6) for v in n9])
    print("SlopePerMag =", [round(v, 6) for v in slopes])
    if not gate_ok:
        sys.exit(1)


if __name__ == "__main__":
    main()
