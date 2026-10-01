#!/bin/sh
# Entry point of openastroara-update@.service (§33 client-pushed update, #1122):
# runs apply-update.sh as root on the daemon's behalf, with the two arguments the
# daemon left in
#   /run/openastroara/update/<id>.request   (staged .deb path, port, drain seconds)
# and writes
#   /run/openastroara/update/<id>.result    (line 1: helper exit code, then the
#                                            helper's output, which carries the
#                                            status=/from=/to=/rollback= lines)
# Same safety contract as storage-request.sh, which this mirrors: the exchange
# directory is daemon-owned and this runs as root, so root never opens a path
# there for writing — the result is built in a private root-only directory and
# rename()d into place with `mv -fT`. The request must be a regular file; the
# helper validates the staged path again before touching it. The unit always
# exits 0 once a result exists: the helper's own exit code travels in the file.
set -u

# DIR/HELPER honour an environment override so the round trip can be exercised
# outside the packaged layout (scripts/tests/test_update_request.py). The unit never sets either.
DIR=${DIR:-/run/openastroara/update}
HELPER=${HELPER:-/opt/openastroara/scripts/apply-update.sh}
MAX_ARGS=3

ID=${1:-}
case "$ID" in
    ''|*[!A-Za-z0-9-]*) echo "update-request: bad request id" >&2; exit 9 ;;
esac
REQ="$DIR/$ID.request"
RES="$DIR/$ID.result"

# Private root-only work directory, outside the daemon-owned exchange dir.
WORK=$(mktemp -d "$(dirname "$(dirname "$DIR")")/openastroara-update.XXXXXX") || exit 9
trap 'rm -rf "$WORK"' EXIT
TMP="$WORK/result"

publish() {   # publish <exit code> <output>
    { printf '%s\n' "$1"; printf '%s\n' "$2"; } > "$TMP"
    chmod 0644 "$TMP"
    mv -fT "$TMP" "$RES"
    rm -f "$REQ"
}

# A refused request still gets a result and loses its request file: left behind, it would
# read as "pending" and block every later apply until the daemon's 15 min staleness cutoff.
refuse() {
    echo "update-request: $1" >&2
    publish 9 "status=failed
ERROR: $1"
    exit 9
}

if [ -L "$REQ" ] || [ ! -f "$REQ" ]; then
    refuse "$REQ is not a regular file"
fi

n=0
while IFS= read -r line || [ -n "$line" ]; do
    n=$((n + 1))
    if [ "$n" -gt "$MAX_ARGS" ]; then
        refuse "too many arguments in $REQ"
    fi
    set -- "$@" "$line"
done < "$REQ"
shift   # drop the request id; the rest is the helper's argv
# The request stays until the result is in place: the daemon reads "request present,
# no result" as pending, and the helper restarts the daemon mid-run, so the new process
# must still see it. (rm never follows a symlink, so removing it later as root is safe.)

rc=0
out=$("$HELPER" "$@" 2>"$WORK/stderr") || rc=$?
if [ -s "$WORK/stderr" ]; then
    cat "$WORK/stderr" >&2
    [ -n "$out" ] || out=$(cat "$WORK/stderr")
fi
publish "$rc" "$out"
exit 0
