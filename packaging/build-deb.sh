#!/usr/bin/env bash
# build-deb.sh — assemble an arm64 .deb for openastroara-server.
#
# Usage:
#   packaging/build-deb.sh <publish-dir> <version> [<output-dir>]
#
# Args:
#   publish-dir  — output of `dotnet publish ... -r linux-arm64 --self-contained`.
#                  Must contain the OpenAstroAra.Server ELF executable.
#   version      — Debian version string (e.g. 0.0.1-ara.1, or 0.0.0-dev-<sha>).
#   output-dir   — where to write the resulting .deb (default: ./dist).
#
# Produces: <output-dir>/openastroara-server_<version>_arm64.deb
#
# CI uses this script after the existing publish step in server-build.
# Locally you can also use it via `dpkg-deb` (Debian/Ubuntu) or via the
# Docker buildx flow if you don't have dpkg on host.

set -euo pipefail

PUBLISH_DIR="${1:?usage: build-deb.sh <publish-dir> <version> [<output-dir>]}"
VERSION="${2:?usage: build-deb.sh <publish-dir> <version> [<output-dir>]}"
OUTPUT_DIR="${3:-./dist}"

# Resolve this script's directory (works whether invoked from repo root or
# from CI's checked-out workdir).
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SOURCE_TREE="$SCRIPT_DIR/debian"

# Validate inputs.
[ -d "$PUBLISH_DIR" ] || { echo "error: publish dir not found: $PUBLISH_DIR" >&2; exit 1; }
[ -x "$PUBLISH_DIR/OpenAstroAra.Server" ] || { echo "error: OpenAstroAra.Server ELF not found in $PUBLISH_DIR" >&2; exit 1; }
[ -d "$SOURCE_TREE/DEBIAN" ] || { echo "error: $SOURCE_TREE/DEBIAN missing — corrupted checkout?" >&2; exit 1; }

mkdir -p "$OUTPUT_DIR"

# Stage the package tree into a tempdir so we don't mutate the source.
STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT

cp -r "$SOURCE_TREE/." "$STAGE/"

# Drop the publish output into /opt/openastroara within the package tree.
mkdir -p "$STAGE/opt/openastroara"
cp -r "$PUBLISH_DIR/." "$STAGE/opt/openastroara/"
chmod 0755 "$STAGE/opt/openastroara/OpenAstroAra.Server"

# Render the control file from the template + version.
sed "s/@VERSION@/$VERSION/g" "$STAGE/DEBIAN/control.template" > "$STAGE/DEBIAN/control"
rm "$STAGE/DEBIAN/control.template"

# Set permissions per Debian policy:
#   - maintainer scripts: 0755 (executable, root:root)
#   - sudoers drop-in: 0440 (visudo requires this)
#   - everything else: 0644 / 0755 by default from cp -r
chmod 0755 "$STAGE/DEBIAN/postinst" "$STAGE/DEBIAN/prerm" "$STAGE/DEBIAN/postrm"
chmod 0440 "$STAGE/etc/sudoers.d/openastroara"

# Validate the sudoers drop-in before packaging — visudo catches typos that
# would otherwise leave the system unable to gain root after install.
if command -v visudo > /dev/null; then
    visudo -cf "$STAGE/etc/sudoers.d/openastroara" > /dev/null
else
    echo "warn: visudo not available on host; skipping sudoers validation" >&2
fi

# Validate the systemd unit. systemd-analyze on the host runs against the
# host's systemd version which may be older, so this is informational only.
if command -v systemd-analyze > /dev/null; then
    systemd-analyze verify "$STAGE/etc/systemd/system/openastroara-server.service" \
        2>&1 | grep -v 'systemd does not run with system instance' || true
fi

# Build the .deb. dpkg-deb requires GNU tar in PATH; both Debian + Ubuntu
# CI runners satisfy this out of the box.
DEB_NAME="openastroara-server_${VERSION}_arm64.deb"
dpkg-deb --build --root-owner-group "$STAGE" "$OUTPUT_DIR/$DEB_NAME"

echo "built: $OUTPUT_DIR/$DEB_NAME"
echo "size: $(du -h "$OUTPUT_DIR/$DEB_NAME" | cut -f1)"
ls -la "$OUTPUT_DIR/$DEB_NAME"
