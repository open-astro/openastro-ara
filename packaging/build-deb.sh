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

# §36 offline-first: bundle every curated sky-data catalog as a seed copy.
# The manifest is the single source of truth for what ships (a unit test keeps
# it in lockstep with DataManagerService.Catalog); each artifact is fetched
# from its commit-pinned URL and SHA-256 verified before it enters the .deb.
SEED_MANIFEST="$SCRIPT_DIR/seed-manifest.tsv"
if [ -f "$SEED_MANIFEST" ]; then
  while IFS=$'\t' read -r pkg_id url sha; do
    [ -z "$pkg_id" ] && continue
    case "$pkg_id" in \#*) continue ;; esac
    fname="$(basename "$url")"
    dest="$STAGE/opt/openastroara/seed-data/$pkg_id"
    mkdir -p "$dest"
    echo "seed: $pkg_id <- $fname"
    curl -fsSL --retry 3 -o "$dest/$fname" "$url"
    actual="$(sha256sum "$dest/$fname" | cut -d' ' -f1)"
    if [ "$actual" != "$sha" ]; then
      echo "error: seed $pkg_id sha256 mismatch (expected $sha, got $actual)" >&2
      exit 1
    fi
  done < "$SEED_MANIFEST"
fi

# Ship the license documents per §15/§17.2: the project license + NINA
# lineage notice, and the generated third-party notices
# (scripts/generate-3rd-party-licenses.py keeps the repo-root file fresh;
# CI fails when it goes stale, and this script re-checks so a stale .deb
# can't be built locally or by a future release pipeline either). The
# Debian-Policy-12.5 `copyright` file arrives via the packaging source
# tree copy above.
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
if command -v python3 > /dev/null && command -v dotnet > /dev/null; then
    python3 "$REPO_ROOT/scripts/generate-3rd-party-licenses.py" --check \
        || { echo "error: 3rd-party-licenses.txt is stale — regenerate before packaging" >&2; exit 1; }
else
    # A pure packaging box (publish dir produced elsewhere) may lack the
    # toolchain; CI always has both, so the gate still guards every merge.
    echo "warn: python3/dotnet not available; skipping third-party-notices freshness check" >&2
fi
DOC_DIR="$STAGE/usr/share/doc/openastroara-server"
mkdir -p "$DOC_DIR"
for doc in LICENSE.txt NOTICE.md 3rd-party-licenses.txt; do
    [ -f "$REPO_ROOT/$doc" ] || { echo "error: $doc missing at repo root — required in the package" >&2; exit 1; }
    cp "$REPO_ROOT/$doc" "$DOC_DIR/"
done

# Render the control file from the template + version.
sed "s/@VERSION@/$VERSION/g" "$STAGE/DEBIAN/control.template" > "$STAGE/DEBIAN/control"
rm "$STAGE/DEBIAN/control.template"

# Normalize source/publish umasks before applying executable exceptions. `cp -r`
# otherwise leaks group-write bits from the checkout and execute bits from every
# self-contained publish file into the package.
find "$STAGE" -type d -exec chmod 0755 {} +
find "$STAGE" -type f -exec chmod 0644 {} +

# Set executable and restricted permissions per Debian policy.
chmod 0755 "$STAGE/opt/openastroara/OpenAstroAra.Server"
if [ -f "$STAGE/opt/openastroara/createdump" ]; then
    chmod 0755 "$STAGE/opt/openastroara/createdump"
fi
# Helper scripts that sudoers.d/openastroara lets the service account exec
# directly (update.sh may be absent — it ships via the §33 update flow).
if [ -f "$STAGE/opt/openastroara/update.sh" ]; then
    chmod 0755 "$STAGE/opt/openastroara/update.sh"
fi
if [ -d "$STAGE/opt/openastroara/scripts" ]; then
    find "$STAGE/opt/openastroara/scripts" -maxdepth 1 -type f -name '*.sh' \
        -exec chmod 0755 {} +
fi
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
