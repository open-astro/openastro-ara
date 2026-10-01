#!/bin/sh
# Entry point of openastroara-update@.service (§33 client-pushed update, #1122):
# runs apply-update.sh as root on the daemon's behalf, with the two arguments the
# daemon left in
#   /run/openastroara/update/<id>.request   (line 1: staged .deb path, line 2: port)
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
MAX_ARGS=2

ID=${1:-}
case "$ID" in
    ''|*[!A-Za-z0-9-]*) echo "update-request: bad request id" >&2; exit 9 ;;
esac
REQ="$DIR/$ID.request"
RES="$DIR/$ID.result"

if [ -L "$REQ" ] || [ ! -f "$REQ" ]; then
    echo "update-request: $REQ is not a regular file" >&2
    exit 9
fi

n=0
while IFS= read -r line || [ -n "$line" ]; do
    n=$((n + 1))
    if [ "$n" -gt "$MAX_ARGS" ]; then
        echo "update-request: too many arguments in $REQ" >&2
        exit 9
    fi
    set -- "$@" "$line"
done < "$REQ"
shift   # drop the request id; the rest is the helper's argv
# The request stays until the result is in place: the daemon reads "request present,
# no result" as pending, and the helper restarts the daemon mid-run, so the new process
# must still see it. (rm never follows a symlink, so removing it later as root is safe.)

WORK=$(mktemp -d "$(dirname "$(dirname "$DIR")")/openastroara-update.XXXXXX") || exit 9
trap 'rm -rf "$WORK"' EXIT
TMP="$WORK/result"
rc=0
out=$("$HELPER" "$@" 2>"$WORK/stderr") || rc=$?
if [ -s "$WORK/stderr" ]; then
    cat "$WORK/stderr" >&2
    [ -n "$out" ] || out=$(cat "$WORK/stderr")
fi
{
    printf '%s\n' "$rc"
    printf '%s\n' "$out"
} > "$TMP"
chmod 0644 "$TMP"
mv -fT "$TMP" "$RES"
rm -f "$REQ"
exit 0
