#!/usr/bin/env bash
#
# Download + verify the pinned ASCOM Alpaca simulators (OmniSim) per playbook §14.5.1.
#
# The simulator binaries are NOT committed. This fetches the pinned GitHub release
# artifact for the current platform, verifies its SHA-256 against
# OpenAstroAra.Test/fixtures/SIMULATORS_VERSION.md, and extracts it into
# OpenAstroAra.Test/fixtures/alpaca-simulators/ (gitignored). Idempotent: a second
# run with the same pin is a no-op.
#
# Prints the absolute path to the extracted `ascom.alpaca.simulators` executable on
# stdout (everything else goes to stderr).
#
# License note: the simulators are MIT-licensed; we download (not redistribute) them.

set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
pin_file="$repo_root/OpenAstroAra.Test/fixtures/SIMULATORS_VERSION.md"
dest="$repo_root/OpenAstroAra.Test/fixtures/alpaca-simulators"
base_url="https://github.com/ASCOMInitiative/ASCOM.Alpaca.Simulators/releases/download"

[ -f "$pin_file" ] || { echo "pin file not found: $pin_file" >&2; exit 1; }

tag="$(awk -F': *' '/^Pinned release:/{print $2; exit}' "$pin_file")"
[ -n "$tag" ] || { echo "could not read 'Pinned release:' from $pin_file" >&2; exit 1; }

# Pick the release artifact for this platform.
os="$(uname -s)"; arch="$(uname -m)"
case "$os/$arch" in
  Linux/x86_64)   artifact="ascom.alpaca.simulators.linux-x64.tar.xz";     subdir="ascom.alpaca.simulators.linux-x64" ;;
  Linux/aarch64)  artifact="ascom.alpaca.simulators.linux-aarch64.tar.xz"; subdir="ascom.alpaca.simulators.linux-aarch64" ;;
  # Darwin/* (incl. Apple Silicon) -> macos-x64: upstream ships no aarch64 macOS
  # build, so the x64 artifact under Rosetta is the only/right fallback.
  Darwin/*)       artifact="ascom.alpaca.simulators.macos-x64.zip";        subdir="ascom.alpaca.simulators.macos-x64" ;;
  *) echo "unsupported platform: $os/$arch" >&2; exit 1 ;;
esac

# Expected SHA-256 is the token after "sha256:" on the artifact's line in the pin file.
expected="$(awk -v a="$artifact" 'index($0,a){for(i=1;i<=NF;i++) if($i=="sha256:"){print $(i+1); exit}}' "$pin_file")"
[ -n "$expected" ] || { echo "no sha256 for $artifact in $pin_file" >&2; exit 1; }

bin="$dest/$subdir/ascom.alpaca.simulators"
ok_marker="$dest/.ok-$tag-$artifact"

if [ -x "$bin" ] && [ -f "$ok_marker" ]; then
  echo "alpaca simulators already installed ($tag)" >&2
  echo "$bin"; exit 0
fi

mkdir -p "$dest"
tmp="$dest/$artifact"
echo "downloading $artifact ($tag)..." >&2
curl -fsSL -o "$tmp" "$base_url/$tag/$artifact"

if command -v sha256sum >/dev/null 2>&1; then
  actual="$(sha256sum "$tmp" | awk '{print $1}')"
else
  actual="$(shasum -a 256 "$tmp" | awk '{print $1}')"
fi
if [ "$actual" != "$expected" ]; then
  echo "SHA-256 mismatch for $artifact" >&2
  echo "  expected: $expected" >&2
  echo "  actual:   $actual" >&2
  rm -f "$tmp"; exit 1
fi

echo "extracting $artifact..." >&2
rm -rf "${dest:?}/${subdir:?}"  # guarded: never expand to / if a var is empty
case "$artifact" in
  *.tar.xz) tar -xf "$tmp" -C "$dest" ;;
  *.zip)    unzip -oq "$tmp" -d "$dest" ;;
esac
[ -f "$bin" ] || { echo "expected binary not found after extract: $bin" >&2; exit 1; }
chmod +x "$bin"
rm -f "$tmp"
# Clear the previous-pin marker for THIS artifact so an upgrade doesn't leave a
# stale .ok-<oldtag>-<artifact> behind. Scoped to the current artifact (any tag),
# so a re-download on a multi-platform dev box can't invalidate another platform's
# marker. Reached only on an actual (re)download — the idempotent no-op exits above.
rm -f "$dest"/.ok-*-"$artifact"
touch "$ok_marker"
echo "$bin"
