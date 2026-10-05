#!/usr/bin/env bash
# Registers an unpacked release bundle with the desktop so Wayland compositors
# can resolve the window icon (#1200/#1201). Wayland has no per-window icon
# protocol in GTK3: KWin/GNOME map the toplevel app_id
# (org.openastro.openastroara) to a .desktop file of the same name and use its
# Icon= key, so without this entry the taskbar shows the generic fallback.
# Packaged installs (#1203) ship the same files under /usr/share instead.
#
# Usage: linux/install-desktop-entry.sh [path/to/bundle]
#        (default: build/linux/x64/release/bundle next to this script's project)
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
bundle="${1:-$here/../build/linux/x64/release/bundle}"
bundle="$(cd "$bundle" && pwd)"
[ -x "$bundle/openastroara" ] || { echo "no openastroara binary in $bundle" >&2; exit 1; }
share="$bundle/share"
[ -d "$share/applications" ] || { echo "bundle has no share/ dir; rebuild with flutter build linux" >&2; exit 1; }

data="${XDG_DATA_HOME:-$HOME/.local/share}"
mkdir -p "$data/applications" "$data/icons/hicolor"
# Exec gets the absolute bundle path; the packaged entry keeps the bare name.
sed "s|^Exec=.*|Exec=$bundle/openastroara|" \
  "$share/applications/org.openastro.openastroara.desktop" \
  > "$data/applications/org.openastro.openastroara.desktop"
cp -R "$share/icons/hicolor/." "$data/icons/hicolor/"
command -v gtk-update-icon-cache >/dev/null && gtk-update-icon-cache -f -t "$data/icons/hicolor" >/dev/null 2>&1 || true
command -v update-desktop-database >/dev/null && update-desktop-database "$data/applications" >/dev/null 2>&1 || true
echo "installed $data/applications/org.openastro.openastroara.desktop (Exec=$bundle/openastroara)"
echo "relaunch the app; a running instance keeps the old icon until restarted"
