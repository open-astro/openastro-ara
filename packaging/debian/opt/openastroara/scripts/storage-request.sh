#!/bin/sh
# Entry point of openastroara-storage@.service: runs configure-storage.sh as
# root on the daemon's behalf, with the arguments the daemon left in
#   /run/openastroara/storage/<id>.request   (one argument per line)
# and writes
#   /run/openastroara/storage/<id>.result    (line 1: helper exit code,
#                                             then the helper's output)
# The unit always exits 0 once a result has been written — the helper's own
# exit code travels in the result file, so a refused format never leaves a
# "failed" instance behind in systemd. The exchange directory is owned by the
# daemon user and this runs as root, so root must never open a path there for
# writing: the daemon user can unlink anything in its own directory and put a
# symlink in its place, and a name that was safe when mktemp created it is not
# safe seconds later. The result is therefore built in a private root-only
# directory (mktemp -d, 0700) and only rename()d into place with `mv -T`;
# rename replaces whatever sits at the destination without following it, and
# -T keeps mv from treating a symlink-to-directory there as a target directory
# (plain `mv` stat()s the destination, follows the link, and would drop a
# root-owned file into whatever directory the link points at). The request is
# checked to be a regular file before it is read; that check is not atomic
# with the open, so a symlink swapped in between could at most make root read
# MAX_ARGS lines of some other file into configure-storage.sh's argv, which
# the helper validates and never echoes back — no wider than the argv the
# daemon user could already hand it under the old sudoers rule.
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

# Private root-only work directory on the same tmpfs as $DIR (so the final mv
# is a rename, never a copy through the destination name). Nothing the daemon
# user does can reach a path under it.
WORK=$(mktemp -d /run/openastroara-storage.XXXXXX) || exit 9
trap 'rm -rf "$WORK"' EXIT
TMP="$WORK/result"
rc=0
# Same shape the daemon's direct-sudo path sees: stdout is the result, stderr
# stands in only when stdout is blank. Merging the two would let a stray
# stderr line from an inner tool land in front of the helper's "ERROR: <code>"
# line and turn a typed failure into an opaque one. stderr still reaches the
# journal (StandardError=journal on the unit).
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
# -T: never treat $RES as a directory, even when it is a symlink to one, so
# this is always rename(2) onto the name, which replaces a planted symlink
# instead of following it. The daemon-writable directory is touched by
# nothing else.
mv -fT "$TMP" "$RES"
exit 0
