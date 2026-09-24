#!/usr/bin/env bash
# Compile-on-demand wrapper for click.swift (a CGEvent mouse click / typer).
# Usage: click.sh <x> <y> [type <text>]   — see click.swift for details.
set -euo pipefail
SRC="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/click.swift"
BIN="${TMPDIR:-/tmp}/ara-click"
if [ ! -x "$BIN" ] || [ "$SRC" -nt "$BIN" ]; then swiftc -O -o "$BIN" "$SRC"; fi
exec "$BIN" "$@"
