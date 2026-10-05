#!/usr/bin/env bash
# Registers an unpacked release bundle with the desktop so Wayland compositors
# can resolve the window icon (#1200/#1201). Wayland has no per-window icon
# protocol in GTK3: KWin/GNOME map the toplevel app_id
# (org.openastro.openastroara) to a .desktop file of the same name and use its
# Icon= key, so without this entry the taskbar shows the generic fallback.
# Packaged installs (#1203) ship the same files under /usr/share instead.
#
# Usage: linux/install-desktop-entry.sh [path/to/bundle]
#        (default: the one build/linux/<arch>/release/bundle that exists)
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
if [ $# -ge 1 ]; then
  bundle="$1"
else
  found=()
  for b in "$here"/../build/linux/*/release/bundle; do
    [ -x "$b/openastroara" ] && found+=("$b")
  done
  case ${#found[@]} in
    1) bundle="${found[0]}" ;;
    0) echo "no release bundle under $here/../build/linux; run flutter build linux --release" >&2; exit 1 ;;
    *) echo "several release bundles found; pass one:" "${found[@]}" >&2; exit 1 ;;
  esac
fi
[ -d "$bundle" ] || { echo "no such directory: $bundle" >&2; exit 1; }
bundle="$(cd "$bundle" && pwd)"
[ -x "$bundle/openastroara" ] || { echo "no openastroara binary in $bundle" >&2; exit 1; }
share="$bundle/share"
[ -d "$share/applications" ] || { echo "bundle has no share/ dir; rebuild with flutter build linux" >&2; exit 1; }

data="${XDG_DATA_HOME:-$HOME/.local/share}"
mkdir -p "$data/applications" "$data/icons/hicolor"
# Exec gets the absolute bundle path; the packaged entry keeps the bare name.
# Quoted per the desktop-entry spec so a path with spaces stays one word; the
# spec's reserved characters inside the quotes are backslash-escaped and % is
# doubled (field codes). The line reaches awk through ENVIRON, never -v, so
# awk does not reprocess the backslashes and the path is never a pattern.
exec_path="$bundle/openastroara"
# Two escaping layers per the spec: the Exec quoting layer (\\ \" \$ \`) is
# applied first, then the general string-escaping layer turns every \ into \\.
exec_path="${exec_path//\\/\\\\}"
exec_path="${exec_path//\"/\\\"}"
exec_path="${exec_path//\$/\\\$}"
exec_path="${exec_path//\`/\\\`}"
exec_path="${exec_path//\\/\\\\}"
exec_path="${exec_path//%/%%}"
ARA_EXEC_LINE="Exec=\"$exec_path\"" awk '/^Exec=/ { print ENVIRON["ARA_EXEC_LINE"]; next } { print }' \
  "$share/applications/org.openastro.openastroara.desktop" \
  > "$data/applications/org.openastro.openastroara.desktop"
fresh_icons=0
[ -d "$data/icons/hicolor/256x256" ] || fresh_icons=1
# Drop this app's icons from earlier installs first so a size that stopped
# shipping doesn't linger, then copy the current set.
find "$data/icons/hicolor" -path '*/apps/org.openastro.openastroara.*' -type f -delete 2>/dev/null || true
cp -R "$share/icons/hicolor/." "$data/icons/hicolor/"
# No gtk-update-icon-cache here: the per-user hicolor dir has no index.theme,
# so a cache file in it is useless and can shadow the plain PNG lookup.
command -v update-desktop-database >/dev/null && update-desktop-database "$data/applications" >/dev/null 2>&1 || true
echo "installed $data/applications/org.openastro.openastroara.desktop (Exec=\"$exec_path\")"
echo "relaunch the app; a running instance keeps the old icon until restarted"
if [ "$fresh_icons" = 1 ]; then
  echo "note: the icon directory was just created; a running desktop shell may have"
  echo "      cached the miss. On Plasma: systemctl --user restart plasma-plasmashell"
  echo "      (or log out and in) once; later installs don't need this."
fi
