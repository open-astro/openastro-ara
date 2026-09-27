#!/bin/sh
# Entry point of openastroara-storage@.service: runs configure-storage.sh as
# root on the daemon's behalf, with the arguments the daemon left in
#   /run/openastroara/storage/<id>.request   (one argument per line)
# and writes
#   /run/openastroara/storage/<id>.result    (line 1: helper exit code,
#                                             then the helper's output)
# The unit always exits 0 once a result has been written — the helper's own
# exit code travels in the result file, so a refused format never leaves a
# "failed" instance behind in systemd. The request directory is owned by the
# daemon user and this runs as root, so every file it writes there is created
# fresh (mktemp, O_EXCL) and the request is checked to be a regular file
# before it is read. That check is not atomic with the open: a symlink swapped
# in between could at most make root read MAX_ARGS lines of some other file
# into configure-storage.sh's argv, which the helper validates and never
# echoes back — no wider than the argv the daemon user could already hand it
# under the old sudoers rule. Nothing here writes through a daemon-supplied
# path (the result is rename()d over it).
set -u

DIR=/run/openastroara/storage
HELPER=/opt/openastroara/scripts/configure-storage.sh
MAX_ARGS=8

ID=${1:-}
case "$ID" in
    ''|*[!A-Za-z0-9-]*) echo "storage-request: bad request id" >&2; exit 9 ;;
esac
REQ="$DIR/$ID.request"
RES="$DIR/$ID.result"

if [ -L "$REQ" ] || [ ! -f "$REQ" ]; then
    echo "storage-request: $REQ is not a regular file" >&2
    exit 9
fi

# Rebuild argv from the request, one line per argument. An empty line is an
# empty argument (the format path passes "" as the confirm label for an
# unlabeled disk). More than MAX_ARGS lines is not a request the daemon ever
# writes — refuse rather than pass a runaway file to the helper.
n=0
while IFS= read -r line || [ -n "$line" ]; do
    n=$((n + 1))
    if [ "$n" -gt "$MAX_ARGS" ]; then
        echo "storage-request: too many arguments in $REQ" >&2
        exit 9
    fi
    set -- "$@" "$line"
done < "$REQ"
shift   # drop the request id; the rest is the helper's argv
rm -f "$REQ"

TMP=$(mktemp "$DIR/.$ID.XXXXXX") || exit 9
rc=0
out=$("$HELPER" "$@" 2>&1) || rc=$?
{
    printf '%s\n' "$rc"
    printf '%s\n' "$out"
} > "$TMP"
chmod 0644 "$TMP"
# rename() replaces whatever sits at $RES (even a symlink) with our file.
mv -f "$TMP" "$RES"
exit 0
