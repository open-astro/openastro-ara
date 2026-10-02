#!/bin/sh
# apply-update.sh <staged.deb> <daemon-port> [<drain-seconds>]   — runs as root (openastroara-update@.service)
#
# §33 client-pushed update (#1122). Installs a .deb the daemon staged, then proves the new
# daemon answers /healthz; if it does not, reinstalls the package that was running before.
# Prints key=value lines the daemon parses from the result file:
#   status=applied|rolled_back|failed   from=<ver>   to=<ver>   rollback=available|unavailable
# plus free-text progress lines. Exit 0 only for status=applied.
#
# Rollback copies: a .deb that was applied here is kept as $ROLLBACK_DIR/<version>.deb (root
# 0700 dir under /var/lib, deliberately OUTSIDE the daemon-owned tree: a directory the daemon
# user owns can have any child renamed and replaced with a symlink, which would steer root's
# writes). The very first push on a rig installed from apt has no copy of its own;
# /var/cache/apt/archives is tried as a fallback.
#
# The upload is copied into the root-only directory FIRST and every check runs on that copy:
# checking the daemon-writable original and copying afterwards would let the file be swapped
# in between.
#
# Tool names and directories honour environment overrides so scripts/tests can drive this
# with stubs; the unit never sets them.
set -u

STAGE_DIR=${STAGE_DIR:-/var/lib/openastroara/updates}
ROLLBACK_DIR=${ROLLBACK_DIR:-/var/lib/openastroara-rollback}
APT_CACHE=${APT_CACHE:-/var/cache/apt/archives}
DPKG=${DPKG:-dpkg}
DPKG_DEB=${DPKG_DEB:-dpkg-deb}
DPKG_QUERY=${DPKG_QUERY:-dpkg-query}
SYSTEMCTL=${SYSTEMCTL:-systemctl}
CURL=${CURL:-curl}
HEALTH_TIMEOUT=${HEALTH_TIMEOUT:-90}
PACKAGE=openastroara-server
UNIT=openastroara-server.service

DEB=${1:-}
PORT=${2:-5555}
# The daemon passes the same value it announced in server.restart_imminent.in_seconds, so the
# two cannot drift; the environment override is for tests.
DRAIN_SECONDS=${DRAIN_SECONDS:-${3:-5}}

fail() {
    echo "status=failed"
    echo "ERROR: $*"
    exit 1
}

case "$PORT" in
    ''|*[!0-9]*) fail "bad port '$PORT'" ;;
esac
case "$DRAIN_SECONDS" in
    ''|*[!0-9]*) fail "bad drain time '$DRAIN_SECONDS'" ;;
esac

# The staged path must sit directly in STAGE_DIR, be a regular file, and not a symlink: the
# daemon user owns that directory and could point a name anywhere.
case "$DEB" in
    "$STAGE_DIR"/*.deb) ;;
    *) fail "staged package must be under $STAGE_DIR" ;;
esac
case "${DEB#"$STAGE_DIR"/}" in
    */*) fail "staged package must be directly under $STAGE_DIR" ;;
esac
# The daemon user owns /var/lib/openastroara and could swap updates/ itself for a symlink
# to another directory; refuse that as well as a symlinked file.
[ -L "$STAGE_DIR" ] && fail "stage directory is a symlink"
[ -L "$DEB" ] && fail "staged package is a symlink"
[ -f "$DEB" ] || fail "staged package not found"

# Take a private copy before reading anything from it.
mkdir -p "$ROLLBACK_DIR" && chmod 0700 "$ROLLBACK_DIR" || fail "cannot create $ROLLBACK_DIR"
WORK=$(mktemp -d "$ROLLBACK_DIR/incoming.XXXXXX") || fail "cannot create a work directory"
trap 'rm -rf "$WORK"' EXIT
COPY="$WORK/upload.deb"
# -P: copy a symlink swapped in after the checks above as a link, then refuse it, rather
# than letting root's cp follow it.
cp -P "$DEB" "$COPY" || fail "could not copy the staged package"
[ -L "$COPY" ] && fail "staged package became a symlink"
# Root does not delete the original: STAGE_DIR's parent is daemon-owned, so the directory
# could be swapped for a symlink between the checks and an rm. The daemon removes its own
# upload once it reads the result (and sweeps leftovers after a day).

field() { "$DPKG_DEB" -f "$COPY" "$1" 2>/dev/null; }
NEW_PKG=$(field Package)
NEW_VER=$(field Version)
NEW_ARCH=$(field Architecture)
[ "$NEW_PKG" = "$PACKAGE" ] || fail "package is '$NEW_PKG', not $PACKAGE"
HOST_ARCH=$("$DPKG" --print-architecture 2>/dev/null || echo "$NEW_ARCH")
[ "$NEW_ARCH" = "$HOST_ARCH" ] || fail "package is for $NEW_ARCH, host is $HOST_ARCH"
case "$NEW_VER" in
    ''|*/*|.*) fail "unusable version '$NEW_VER'" ;;
esac
OLD_VER=$("$DPKG_QUERY" -W -f='${Version}' "$PACKAGE" 2>/dev/null || true)
[ -n "$OLD_VER" ] || fail "$PACKAGE is not installed"
echo "from=$OLD_VER"
echo "to=$NEW_VER"
"$DPKG" --compare-versions "$NEW_VER" gt "$OLD_VER" || fail "$NEW_VER is not newer than installed $OLD_VER"

# Rollback copy of what is running now.
OLD_DEB="$ROLLBACK_DIR/$OLD_VER.deb"
if [ ! -f "$OLD_DEB" ]; then
    for c in "$APT_CACHE/${PACKAGE}_${OLD_VER}_${HOST_ARCH}.deb" "$APT_CACHE/${PACKAGE}_$(printf '%s' "$OLD_VER" | sed 's/:/%3a/g')_${HOST_ARCH}.deb"; do
        if [ -f "$c" ]; then cp "$c" "$OLD_DEB"; break; fi
    done
fi
if [ -f "$OLD_DEB" ]; then echo "rollback=available"; else echo "rollback=unavailable"; fi

NEW_DEB="$ROLLBACK_DIR/$NEW_VER.deb"
mv -f "$COPY" "$NEW_DEB" || fail "could not keep the new package"

# A package that failed (dpkg error, or never healthy) is no use for a later rollback and
# costs ~70 MB of SD card; drop it whenever the outcome is not "applied".
discard_new() {
    [ -n "${NEW_DEB:-}" ] && [ "$NEW_DEB" != "${OLD_DEB:-}" ] && rm -f "$NEW_DEB"
}

healthy() {
    "$CURL" -fsS -m 3 "http://127.0.0.1:$PORT/healthz" >/dev/null 2>&1
}
wait_healthy() {
    i=0
    while [ "$i" -lt "$HEALTH_TIMEOUT" ]; do
        healthy && return 0
        i=$((i + 1)); sleep 1
    done
    return 1
}

echo "draining the running daemon for ${DRAIN_SECONDS}s"
sleep "$DRAIN_SECONDS"
echo "installing $NEW_VER"
# prerm stops the unit and postinst starts it again. If dpkg fails, the rollback install
# below is followed by an explicit restart; on success see the is-active check after it.
if ! "$DPKG" -i "$NEW_DEB"; then
    echo "dpkg -i failed; reinstalling $OLD_VER"
    if [ -f "$OLD_DEB" ] && "$DPKG" -i "$OLD_DEB"; then
        "$SYSTEMCTL" restart "$UNIT" || true
        discard_new
        echo "status=rolled_back"
    else
        "$SYSTEMCTL" restart "$UNIT" || true
        echo "status=failed"
    fi
    exit 2
fi
# postinst normally starts the unit; restart only if it is not running (a hand-disabled
# unit, or a postinst that left it stopped) so a healthy start is not bounced.
"$SYSTEMCTL" is-active --quiet "$UNIT" || "$SYSTEMCTL" restart "$UNIT" || true
if wait_healthy; then
    # Keep only what a rollback can use: the version now running and the one before it.
    for f in "$ROLLBACK_DIR"/*.deb; do
        [ -f "$f" ] || continue
        case "$f" in "$NEW_DEB"|"$OLD_DEB") ;; *) rm -f "$f" ;; esac
    done
    echo "status=applied"
    exit 0
fi
echo "new daemon did not answer /healthz within ${HEALTH_TIMEOUT}s"
if [ -f "$OLD_DEB" ] && "$DPKG" -i "$OLD_DEB"; then
    "$SYSTEMCTL" restart "$UNIT" || true
    if wait_healthy; then echo "rolled back to $OLD_VER"; else echo "rollback installed but /healthz still silent"; fi
    discard_new
    echo "status=rolled_back"
    exit 3
fi
echo "status=failed"
exit 3
