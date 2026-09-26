#!/usr/bin/env bash
# Capture one app's main window to a PNG on macOS, without needing Accessibility
# permission (System Events / osascript is refused for unattended shells here;
# CoreGraphics' window list is not).
# Usage: scripts/capture-window.sh <owner-name> <out.png>     e.g. "OpenAstro Ara" pr-991-tonight.png
# The Ara client's window owner is "OpenAstro Ara" (the display name), not the bundle name openastroara.
# Exit 2 if no on-screen window belongs to that owner (app not running / not launched yet).
set -euo pipefail
OWNER="${1:?owner name, e.g. openastroara}"; OUT="${2:?output png}"
# Private, user-owned cache (see click.sh for why not $TMPDIR/tmp).
CACHE_DIR="${XDG_CACHE_HOME:-$HOME/.cache}/openastro-ara-skills"
mkdir -p "$CACHE_DIR"; chmod 700 "$CACHE_DIR"
CACHE="$CACHE_DIR/winlist"
# Rebuild when this script (which embeds the Swift source) is newer than the cached binary.
if [ ! -x "$CACHE" ] || [ "${BASH_SOURCE[0]}" -nt "$CACHE" ]; then
  SRC="$CACHE_DIR/winlist.swift"
  cat > "$SRC" <<'SWIFT'
import CoreGraphics
import Foundation
let list = CGWindowListCopyWindowInfo([.optionOnScreenOnly, .excludeDesktopElements], kCGNullWindowID) as! [[String: Any]]
for w in list where (w["kCGWindowLayer"] as? Int) == 0 {
    print("\(w["kCGWindowNumber"] as! Int)\t\(w["kCGWindowOwnerName"] as? String ?? "?")\t\(w["kCGWindowName"] as? String ?? "")")
}
SWIFT
  swiftc -O -o "$CACHE" "$SRC"
  rm -f "$SRC"
fi
# No `exit` in the awk: under pipefail an early consumer exit can SIGPIPE the producer.
WID=$("$CACHE" | awk -F'\t' -v o="$OWNER" 'tolower($2)==tolower(o) && !found {print $1; found=1}')
if [ -z "$WID" ]; then echo "no on-screen window owned by '$OWNER'" >&2; "$CACHE" >&2; exit 2; fi
# Bring it front so nothing overlaps. $OWNER is the window owner's display name, which
# `open -a` does not resolve; System Events does (needs Accessibility; harmless if refused).
osascript -e "tell application \"System Events\" to set frontmost of process \"$OWNER\" to true" >/dev/null 2>&1 || true
sleep 0.5
screencapture -x -l "$WID" "$OUT"
echo "$OUT (window $WID)"
