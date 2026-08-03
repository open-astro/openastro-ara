#!/usr/bin/env bash
# §77.3 — fetch the JPL DE421 planetary ephemeris (public domain, ~16 MB) into
# External/JPLEPH at the repo root. NOVAS needs it for solar-system body
# positions (planetary pointing); without it, GetBodyPosition returns NaN and
# the daemon logs an error at startup. The Server/Test csprojs copy the file
# into their output when present. DE421 spans 1900-2050.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DEST="$ROOT/External/JPLEPH"
URL="https://ssd.jpl.nasa.gov/ftp/eph/planets/Linux/de421/lnxp1900p2053.421"
SHA256="5b3f81dd0c505925055a389431fd2bc5e0ae2fd02d85db37fa4d4fe54dfb4096"
mkdir -p "$ROOT/External"
if [ -f "$DEST" ]; then
    echo "External/JPLEPH already present."
    exit 0
fi
echo "Downloading DE421 from JPL (~16 MB)..."
curl -fsSL "$URL" -o "$DEST.part"
echo "$SHA256  $DEST.part" | shasum -a 256 -c - || {
    echo "checksum mismatch — refusing the file" >&2
    rm -f "$DEST.part"
    exit 1
}
mv "$DEST.part" "$DEST"
echo "Wrote $DEST"
