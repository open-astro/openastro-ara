#!/usr/bin/env python3
"""Regenerate client/openastroara_client/assets/catalogs/rcw.csv.

Fetches the 181-row Rodgers (numbers 1-182, 116 was never published), Campbell & Whiteoak (1960) catalogue from VizieR
(VII/216/rcw, J2000 position computed by VizieR from the B1950 original), the
Roman (1987) constellation boundaries (VI/42/data) and the SIMBAD common names
of the RCW objects, then writes the OpenNGC column layout the client parses
(same as sh2.csv). Nothing is typed in by hand: re-run it to reproduce the file.

    python3 scripts/build-rcw-catalog.py [--nebula-types-from NGC.csv]

Gum numbers ("G n") come from the catalogue's own IDs column. The NGC/IC
cross-id is SIMBAD's identifier of the same object (the VizieR IDs cell also
lists neighbours, e.g. NGC 3324 beside RCW 53 = NGC 3372). It is kept when the
OpenNGC catalogue has that row, whatever its type, and no earlier RCW row claims
it, because the client folds a row that names another row's id into that row.
An RCW object SIMBAD gives no NGC/IC falls back to the IDs cell, nebulae only.
"""
import argparse
import csv
import io
import math
import re
import sys
import urllib.parse
import urllib.request

NEBULA_TYPES = {"HII", "EmN", "Neb", "Cl+N", "RfN", "SNR"}

VIZIER = "https://vizier.cfa.harvard.edu/viz-bin/asu-tsv"
SIMBAD = "https://simbad.cfa.harvard.edu/simbad/sim-tap/sync"
HEADER = (
    "Name;Type;RA;Dec;Const;MajAx;MinAx;PosAng;B-Mag;V-Mag;J-Mag;H-Mag;K-Mag;SurfBr;Hubble;Pax;"
    "Pm-RA;Pm-Dec;RadVel;Redshift;Cstar U-Mag;Cstar B-Mag;Cstar V-Mag;M;NGC;IC;Cstar Names;"
    "Identifiers;Common names"
)


def get(url, params):
    with urllib.request.urlopen(url + "?" + urllib.parse.urlencode(params), timeout=90) as r:
        return r.read().decode("utf-8")


def rows(tsv):
    lines = [l for l in tsv.splitlines() if l and not l.startswith("#")]
    cols = lines[0].split("\t")
    # lines[1] holds units, lines[2] the dashes
    return [dict(zip(cols, l.split("\t"))) for l in lines[3:]]


def precess_to_1875(ra_deg, dec_deg):
    """J2000 -> B1875 (IAU 1976 precession), degrees."""
    t = (1875.0 - 2000.0) / 100.0  # Julian centuries from J2000, ignoring the B/J difference (< 0.1 arcsec)
    zeta = (2306.2181 * t + 0.30188 * t * t + 0.017998 * t ** 3) / 3600
    z = (2306.2181 * t + 1.09468 * t * t + 0.018203 * t ** 3) / 3600
    theta = (2004.3109 * t - 0.42665 * t * t - 0.041833 * t ** 3) / 3600
    ra, dec = math.radians(ra_deg), math.radians(dec_deg)
    zeta, z, theta = map(math.radians, (zeta, z, theta))
    a = math.cos(dec) * math.sin(ra + zeta)
    b = math.cos(theta) * math.cos(dec) * math.cos(ra + zeta) - math.sin(theta) * math.sin(dec)
    c = math.sin(theta) * math.cos(dec) * math.cos(ra + zeta) + math.cos(theta) * math.sin(dec)
    ra1 = (math.degrees(math.atan2(a, b)) + math.degrees(z)) % 360
    return ra1, math.degrees(math.asin(c))


def constellation(bounds, ra_deg, dec_deg):
    ra, dec = precess_to_1875(ra_deg, dec_deg)
    h = ra / 15.0
    for lo, up, dlo, name in bounds:  # Roman 1987: first row that contains the point
        if dlo <= dec and lo <= h < up:
            return name
    raise ValueError(f"no constellation for {ra_deg},{dec_deg}")


def sexa(value, ra):
    if ra:
        value /= 15.0
    sign = "-" if value < 0 else ("+" if not ra else "")
    value = abs(value)
    d = int(value)
    m = int((value - d) * 60)
    s = round(((value - d) * 60 - m) * 60, 2)
    if s >= 60:
        s, m = s - 60, m + 1
    if m >= 60:
        m, d = m - 60, d + 1
    return f"{sign}{d:02d}:{m:02d}:{s:05.2f}"


def expand(first, rest):
    """'6164' + ['5'] -> 6164, 6165: a later number may drop the leading digits it shares."""
    out, prev = [first], first
    for r in rest:
        if not r.isdigit():
            continue
        out.append(prev[: len(prev) - len(r)] + r if len(r) < len(prev) else r)
        prev = out[-1]
    return out


def parse_ids(ids):
    """(ngc/ic designations, gum designations) from one IDs cell; uncertain ids are dropped."""
    gum = []
    for m in re.finditer(r"(?<![A-Za-z])G(\d+[a-z]?(?:,[0-9a-z]+)*)", ids):
        first, *rest = m.group(1).split(",")
        digits = re.match(r"\d+", first).group(0)
        gum.append("Gum " + first)
        for n in expand(digits, [r for r in rest if r.isdigit()])[1:]:
            gum.append("Gum " + n)
    cleaned = re.sub(r"\(M\d+\)", "", ids)
    cleaned = re.sub(r"\([^)]*\)\??", "", cleaned)  # (NGC2409?) and (IC1283,4): uncertain
    cleaned = re.sub(r"(?:Part of|Near) (?:NGC|IC)\d+", "", cleaned)
    ngc = []
    for m in re.finditer(r"(NGC|IC)(\d+)((?:,\s*\d+)*)(?!\?)", cleaned):
        nums = expand(m.group(2), [x.strip() for x in m.group(3).split(",") if x.strip()])
        ngc += [(m.group(1), n) for n in nums]
    return ngc, gum


def openngc_types(path):
    types = {}
    with open(path, encoding="utf-8") as f:
        for r in csv.DictReader(f, delimiter=";"):
            types[r["Name"]] = r["Type"]
    return types


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument(
        "--nebula-types-from",
        default="client/openastroara_client/assets/catalogs/NGC.csv",
    )
    ap.add_argument("--out", default="client/openastroara_client/assets/catalogs/rcw.csv")
    args = ap.parse_args()

    cat = rows(get(VIZIER, {"-source": "VII/216/rcw", "-out.max": "1000", "-out.add": "_RAJ,_DEJ", "-oc.form": "d"}))
    bounds = [
        (float(r["RA_low"]), float(r["RA_up"]), float(r["DE_low"]), r["const"].strip())
        for r in rows(get(VIZIER, {"-source": "VI/42/data", "-out.max": "2000"}))
    ]
    names = {}
    simbad = get(
        SIMBAD,
        {
            "request": "doQuery",
            "lang": "adql",
            "format": "tsv",
            "query": "select i1.id as rcw, i2.id as nm from ident as i1 join ident as i2 on i1.oidref=i2.oidref "
            "where i1.id like 'RCW %' and i2.id like 'NAME %'",
        },
    )
    for line in simbad.splitlines()[1:]:
        rcw, nm = (x.strip().strip('"') for x in line.split("\t"))
        nm = re.sub(r"^NAME (the )?", "", nm)
        names.setdefault(int(rcw.split()[1]), []).append(nm)
    links = {}  # RCW number -> SIMBAD NGC/IC designations of the same object
    simbad = get(
        SIMBAD,
        {
            "request": "doQuery",
            "lang": "adql",
            "format": "tsv",
            "query": "select i1.id as rcw, i2.id as nm from ident as i1 join ident as i2 on i1.oidref=i2.oidref "
            "where i1.id like 'RCW %' and (i2.id like 'NGC %' or i2.id like 'IC %')",
        },
    )
    for line in simbad.splitlines()[1:]:
        rcw, nm = (x.strip().strip('"') for x in line.split("\t"))
        kind, num = nm.split()
        links.setdefault(int(re.match(r"\d+", rcw.split()[1]).group(0)), []).append((kind, num))
    simbad_keys = {f"{k}{int(v):04d}" for ids in links.values() for k, v in ids}
    types = openngc_types(args.nebula_types_from)

    # The published tables hold 181 rows: numbers 1-182 without 116 (VizieR VII/216/rcw, "181 rows").
    if sorted(int(r["RCW"]) for r in cat) != [n for n in range(1, 183) if n != 116]:
        sys.exit(f"expected RCW 1-182 without 116, VizieR returned {len(cat)} rows")
    claimed, out, dropped = set(), [], []
    for r in cat:
        n = int(r["RCW"])
        ra, dec = float(r["_RAJ2000"]), float(r["_DEJ2000"])
        ngc, gum = parse_ids(r["IDs"])
        kept = None
        if n in links:
            cand = sorted(links[n], key=lambda x: (x[0] != "NGC", int(x[1])))
            ok = lambda key: key in types
        else:  # SIMBAD lists no NGC/IC for it: the IDs cell, nebulae only, never another row's SIMBAD id
            cand = ngc
            ok = lambda key: types.get(key) in NEBULA_TYPES and key not in simbad_keys
        for kind, num in cand:
            key = f"{kind}{int(num):04d}"
            if kept is None and ok(key) and key not in claimed:
                kept = (kind, num, key)
            else:
                dropped.append((n, kind + num, types.get(key), key in claimed))
        ident = list(gum)
        ngc_col = ic_col = ""
        if kept:
            claimed.add(kept[2])
            ident.append(f"{kept[0]} {kept[1]}")
            ngc_col, ic_col = (str(int(kept[1])), "") if kept[0] == "NGC" else ("", str(int(kept[1])))
        maj = int(r["MajAxis"])
        out.append(
            ";".join(
                [f"RCW {n}", "HII", sexa(ra, True), sexa(dec, False), constellation(bounds, ra, dec), str(maj) if maj else ""]
                + [""] * 18
                + [ngc_col, ic_col, "", ",".join(ident), ",".join(names.get(n, []))]
            )
        )
    with open(args.out, "w", encoding="utf-8", newline="\n") as f:
        f.write(HEADER + "\n" + "\n".join(out) + "\n")
    for d in dropped:
        print("not cross-linked (RCW, id, OpenNGC type, claimed):", d, file=sys.stderr)
    print(f"wrote {len(out)} rows to {args.out}")


if __name__ == "__main__":
    main()
