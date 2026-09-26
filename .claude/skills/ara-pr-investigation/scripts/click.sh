#!/usr/bin/env bash
# Compile-on-demand wrapper for click.swift (a CGEvent mouse click / typer).
# Usage: click.sh <x> <y> [type <text>]   — see click.swift for details.
set -euo pipefail
SRC="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/click.swift"
# Private, user-owned cache: a predictable path under a shared /tmp could be
# pre-planted with an executable this script would then exec.
CACHE_DIR="${XDG_CACHE_HOME:-$HOME/.cache}/openastro-ara-skills"
mkdir -p "$CACHE_DIR"; chmod 700 "$CACHE_DIR"
BIN="$CACHE_DIR/click"
if [ ! -x "$BIN" ] || [ "$SRC" -nt "$BIN" ]; then swiftc -O -o "$BIN" "$SRC"; fi
exec "$BIN" "$@"
