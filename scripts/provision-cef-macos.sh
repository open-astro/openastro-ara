#!/usr/bin/env bash
#
# Provision the CEF (Chromium Embedded Framework) runtime for the webview_cef
# macOS build — the §36 Sky Atlas / Aladin Lite embed. See design/PORT_DECISIONS.md
# §36 and design/PORT_TODO.md.
#
# WHY THIS EXISTS (macOS only): the webview_cef Linux and Windows builds pull CEF
# automatically at `flutter build` time via the plugin's CMake
# (`<plugin>/third/download.cmake` → `prepare_prebuilt_files`). macOS builds
# through CocoaPods, which has NO download step (the Darwin branch in
# download.cmake is commented out), so the prebuilt CEF bundle must be placed and
# restructured by hand. This script does that idempotently.
#
# The CEF binaries are NOT committed (they're gitignored inside the plugin and
# live in the shared pub-cache). Run this once after `flutter pub get`, before the
# first `flutter build macos` / `flutter run -d macos`.
#
# What it does:
#   1. Resolves the webview_cef plugin dir (via the project's Flutter symlink,
#      falling back to a pub-cache scan).
#   2. Downloads the arch-matched prebuilt CEF bundle (pinned + SHA-256 verified
#      for arm64) and places "Chromium Embedded Framework.framework",
#      libcef_dll_wrapper.a, and the helper .apps into <plugin>/macos/third/cef/.
#   3. Restructures the framework from CEF's flat/shallow layout into a versioned
#      bundle (Versions/A + symlinks) — macOS Xcode embedding rejects the flat
#      layout with "expected Versions/Current/Resources/Info.plist".
#
# NOTE: the matching CocoaPods link-flag fix (CocoaPods mis-tokenizes the
# space-containing framework name in OTHER_LDFLAGS) lives in
# client/openastroara_client/macos/Podfile's post_install hook, not here.
#
# Idempotent: a second run with the framework already provisioned is a no-op.
# Prints the provisioned third/cef path on stdout; everything else to stderr.
#
# License note: CEF is BSD-licensed; we download (not redistribute) the prebuilt
# binaries from the upstream webview_cef release assets.
#
# Supply-chain / provenance (accepted trust decision): these prebuilt CEF bundles
# come from the hlwhl/webview_cef GitHub release assets — a third-party fork's
# rebuild of upstream Chromium/CEF, NOT an official Chromium release, so build
# provenance is not independently verifiable. This is the SAME source the
# webview_cef plugin already trusts to auto-download CEF on Linux/Windows (see the
# URLs in <plugin>/third/download.cmake); pinning macOS to it keeps all three
# desktops on one CEF supply chain. The pinned SHA-256s prevent silent asset
# swaps / MITM. The trade-off (an embedded browser engine from an unofficial
# rebuild) is accepted for the §36 Aladin embed; revisit if upstream CEF ships
# official macOS framework bundles we can point at directly.

set -euo pipefail

log() { echo "$@" >&2; }
die() { echo "error: $*" >&2; exit 1; }

[ "$(uname -s)" = "Darwin" ] || die "this script is macOS-only (Linux/Windows auto-download CEF via CMake)"

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
client_dir="$repo_root/client/openastroara_client"

# CEF pin (matches the webview_cef fork's bundled headers + the upstream README).
# The sha256 values below are of the release assets at each `cef_url` — computed
# by downloading the actual hlwhl/webview_cef prebuilt zips (tags
# prebuilt_cef_bin_mac_arm64 / prebuilt_cef_bin_mac_intel). Re-pin if the version
# is bumped: download the new asset and `shasum -a 256` it.
cef_version="103.0.12"
arch="$(uname -m)"
case "$arch" in
  arm64)
    cef_url="https://github.com/hlwhl/webview_cef/releases/download/prebuilt_cef_bin_mac_arm64/CEFbins-mac${cef_version}-arm64.zip"
    cef_sha256="8db59f979643bde7951b14d71acbed22acc04cd8edd31530b35cb874192c77df"
    ;;
  x86_64)
    cef_url="https://github.com/hlwhl/webview_cef/releases/download/prebuilt_cef_bin_mac_intel/mac${cef_version}-Intel.zip"
    cef_sha256="733e4c75ae0b4307f361c4e9647259e478f13f37b90024b15454332c325304eb"
    ;;
  *)
    die "unsupported arch '$arch' (expected arm64 or x86_64)"
    ;;
esac

# 1. Resolve the webview_cef plugin dir.
plugin_link="$client_dir/macos/Flutter/ephemeral/.symlinks/plugins/webview_cef"
if [ -e "$plugin_link" ]; then
  plugin_dir="$(cd "$plugin_link" && pwd -P)"
else
  log "Flutter symlink not found ($plugin_link); scanning pub-cache…"
  log "  (run 'flutter pub get' in $client_dir first for the most reliable resolution)"
  shopt -s nullglob
  candidates=("$HOME/.pub-cache/git/webview_cef-"*/)
  shopt -u nullglob
  [ "${#candidates[@]}" -ge 1 ] || die "no webview_cef checkout in pub-cache; run 'flutter pub get' first"
  [ "${#candidates[@]}" -eq 1 ] || die "multiple webview_cef checkouts in pub-cache; run 'flutter pub get' to pick the pinned one"
  plugin_dir="$(cd "${candidates[0]}" && pwd -P)"
fi

cef_dir="$plugin_dir/macos/third/cef"
framework="$cef_dir/Chromium Embedded Framework.framework"
[ -d "$cef_dir/include" ] || die "unexpected plugin layout: $cef_dir/include missing"

# 2. Idempotency check.
if [ -f "$framework/Versions/Current/Resources/Info.plist" ] && [ -f "$cef_dir/libcef_dll_wrapper.a" ]; then
  log "CEF already provisioned (versioned framework + wrapper present)."
  echo "$cef_dir"
  exit 0
fi

log "Provisioning CEF $cef_version ($arch) into: $cef_dir"

tmp_dir="$(mktemp -d)"
trap 'rm -rf "$tmp_dir"' EXIT
zip_path="$tmp_dir/cef.zip"

log "Downloading $cef_url"
curl -fL --retry 3 -o "$zip_path" "$cef_url" || die "download failed"

# Every supported arch above pins a checksum; an empty pin means a new arch was
# added without one, which must hard-fail rather than download unverified.
[ -n "$cef_sha256" ] || die "no pinned sha256 for arch '$arch' — add one to the case block above"
got="$(shasum -a 256 "$zip_path" | awk '{print $1}')"
[ "$got" = "$cef_sha256" ] || die "sha256 mismatch: expected $cef_sha256, got $got"
log "sha256 verified."

log "Extracting…"
unzip -q "$zip_path" -d "$tmp_dir/extract"
rm -rf "$tmp_dir/extract/__MACOSX"  # drop AppleDouble shadow tree (./_* mirrors)

# Locate the dir that actually contains the framework (bundle subdir name varies by arch).
# -print -quit stops at the first match in-process: avoids a `… | head -1` pipe that,
# under `set -o pipefail`, would SIGPIPE find (exit 141) and abort the script.
src_fw="$(find "$tmp_dir/extract" -maxdepth 3 -type d -name "Chromium Embedded Framework.framework" -not -path "*__MACOSX*" -print -quit)"
[ -n "$src_fw" ] || die "framework not found in archive"
src_dir="$(dirname "$src_fw")"
[ -f "$src_dir/libcef_dll_wrapper.a" ] || die "libcef_dll_wrapper.a not found alongside framework"

log "Placing framework + wrapper + helper apps…"
# Clear any partial/stale copy (keep include/).
rm -rf "$framework" "$cef_dir/libcef_dll_wrapper.a" "$cef_dir"/*.app
ditto "$src_fw" "$framework"
cp "$src_dir/libcef_dll_wrapper.a" "$cef_dir/"
for app in "$src_dir"/*.app; do
  [ -e "$app" ] || continue
  ditto "$app" "$cef_dir/$(basename "$app")"
done

# 3. Restructure flat framework → versioned bundle (if not already versioned).
# Note on partial-run recovery: if the script is killed mid-restructure, the
# idempotency check above (Versions/Current/Resources/Info.plist) still fails on
# the next run, so it re-downloads and the `rm -rf "$framework"` below wipes the
# half-built bundle before rebuilding it cleanly. No manual cleanup needed.
if [ ! -d "$framework/Versions" ]; then
  log "Restructuring framework into a versioned bundle…"
  rm -f "$framework/Info.plist"          # remove any stray root plist
  # Drop the flat-layout signature; we deliberately don't re-sign here — Flutter's
  # build re-signs embedded frameworks at `flutter build macos` time. (If a future
  # Gatekeeper/Xcode policy needs a valid signature pre-build, codesign here.)
  rm -rf "$framework/_CodeSignature"
  # The flat CEF 103.0.12 bundle contains exactly these three payload entries at
  # the framework root; verified for this pinned release. Guard against a future
  # pin bump silently adding top-level dirs (e.g. a Headers/) that the three `mv`
  # calls would orphan at the flat root — fail clearly now rather than letting
  # Xcode's versioned-layout validation surface it as a cryptic build error.
  # (Runs before the mkdir below so it only sees the flat payload, not Versions/.)
  while IFS= read -r entry; do
    case "$entry" in
      "Chromium Embedded Framework"|"Resources"|"Libraries") ;;
      "") ;;
      *) die "unexpected top-level entry in flat CEF framework: '$entry' — update the restructure payload list in $(basename "$0")" ;;
    esac
  done < <(ls "$framework")
  mkdir -p "$framework/Versions/A"
  mv "$framework/Chromium Embedded Framework" "$framework/Versions/A/Chromium Embedded Framework"
  mv "$framework/Resources" "$framework/Versions/A/Resources"
  mv "$framework/Libraries" "$framework/Versions/A/Libraries"
  ln -s "A" "$framework/Versions/Current"
  ln -s "Versions/Current/Chromium Embedded Framework" "$framework/Chromium Embedded Framework"
  ln -s "Versions/Current/Resources" "$framework/Resources"
  ln -s "Versions/Current/Libraries" "$framework/Libraries"
fi

[ -f "$framework/Versions/Current/Resources/Info.plist" ] || die "post-restructure validation failed: Info.plist not reachable"

log "Done. CEF provisioned for the macOS webview_cef build."
echo "$cef_dir"
