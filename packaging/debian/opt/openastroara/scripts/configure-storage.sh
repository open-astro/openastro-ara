#!/bin/sh
# §29.1.4 — the ONLY privileged storage operation the daemon can perform.
# Installed 0750 root:openastroara by the .deb; a sudoers drop-in grants the
# daemon user passwordless invocation of this exact path (no bare mount/mkfs
# rights, so this script's validation cannot be bypassed).
#
#   sudo configure-storage.sh <uuid-or-/dev/path>
#   sudo configure-storage.sh --format <uuid-or-/dev/path> [<expected-label>]
#
# A /dev/ path identifies a brand-new blank disk (no filesystem, hence no
# UUID yet). An empty expected label is legal only when the disk truly has
# no label to retype (blank or unlabeled); mkfs then labels it ARA-STORE.
#
# Exit codes: 0 ok · 2 uuid_not_found · 3 not_ext4 · 4 label_mismatch
#             5 device_busy · 6 refused (root/boot disk) · 7 mkfs_failed
#             8 mount_failed · 9 usage · 10 chown_failed
set -eu

MOUNT_POINT=/media/openastroara
FSTAB=/etc/fstab
OWNER=${ARA_STORAGE_OWNER:-openastroara}

usage() {
    echo "usage: $0 [--format] <uuid> [<expected-label>]" >&2
    exit 9
}

# Resolve a UUID to its device node; empty when unknown.
device_for_uuid() {
    blkid -U "$1" 2>/dev/null || true
}

value_for() { # $1=device $2=tag (TYPE|LABEL|UUID)
    blkid -o value -s "$2" "$1" 2>/dev/null || true
}

# Refuse anything that carries the running system: the disk holding / or
# /boot/firmware, and any of its partitions. Formatting those bricks the box.
# Walks the WHOLE parent chain (partition → md/LVM/dm-crypt → physical disk),
# not one hop, so a stacked root still resolves to its physical holder.
# Keep in lock-step with StorageDeviceService.SystemDisksAsync.
base_disk() { # deepest ancestor of a device node
    d=$1
    i=0
    while [ "$i" -lt 8 ]; do
        p=$(lsblk -no PKNAME "$d" 2>/dev/null | head -n1 || true)
        [ -z "$p" ] && break
        d="/dev/$p"
        i=$((i + 1))
    done
    echo "$d"
}

refuse_if_system_disk() {
    dev=$1
    base=$(base_disk "$dev")
    for critical in / /boot /boot/firmware; do
        holder=$(findmnt -no SOURCE "$critical" 2>/dev/null || true)
        [ -z "$holder" ] && continue
        holder_base=$(base_disk "$holder")
        if [ "$dev" = "$holder" ] || [ "$base" = "$holder_base" ]; then
            echo "ERROR: refused system_disk"
            exit 6
        fi
    done
}

ensure_fstab_entry() { # $1=uuid
    uuid=$1
    # Drop any stale line for this mount point (a previous drive) so the mount
    # point never has two owners, then add ours.
    # Build the whole new fstab in a temp file, then atomically rename it
    # into place: this runs as root on a box without a guaranteed clean
    # shutdown, and a truncate-then-write torn by power loss could leave
    # /etc/fstab half-written — including the root and boot entries.
    # grep exit 1 just means "no previous ARA line" (fine); exit >1 means the
    # read itself failed — committing that would drop every existing entry
    # and brick the next boot, so bail instead.
    rc=0
    grep -v "[[:space:]]${MOUNT_POINT}[[:space:]]" "$FSTAB" > "${FSTAB}.ara-tmp" || rc=$?
    if [ "$rc" -gt 1 ]; then
        rm -f "${FSTAB}.ara-tmp"
        echo "ERROR: fstab_unreadable"
        exit 8
    fi
    printf 'UUID=%s  %s  ext4  defaults,data=ordered,noatime,errors=remount-ro,nofail,x-systemd.device-timeout=10  0  2\n' \
        "$uuid" "$MOUNT_POINT" >> "${FSTAB}.ara-tmp"
    chmod 644 "${FSTAB}.ara-tmp"
    sync
    mv "${FSTAB}.ara-tmp" "$FSTAB"
    systemctl daemon-reload 2>/dev/null || true
}

mount_and_own() { # $1=uuid $2=deep-chown (1 after mkfs, else top-level only)
    uuid=$1
    deep=${2:-0}
    mkdir -p "$MOUNT_POINT"
    ensure_fstab_entry "$uuid"
    if ! findmnt -no SOURCE "$MOUNT_POINT" >/dev/null 2>&1; then
        if ! mount "$MOUNT_POINT" 2>/dev/null; then
            echo "ERROR: mount_failed"
            exit 8
        fi
    fi
    # Recursive chown only right after a format (empty tree). Re-walking a
    # drive already full of frames on every reconnect is pure wasted I/O —
    # everything under the root was created by the daemon and is owned right.
    # A chown failure (e.g. the daemon user missing due to install ordering)
    # must fail loudly HERE, not later as mysterious permission-denied frame
    # writes with no trail back to provisioning.
    if [ "$deep" -eq 1 ]; then
        chown -R "$OWNER:$OWNER" "$MOUNT_POINT" || { echo "ERROR: chown_failed $OWNER"; exit 10; }
    else
        chown "$OWNER:$OWNER" "$MOUNT_POINT" || { echo "ERROR: chown_failed $OWNER"; exit 10; }
    fi
}

FORMAT=0
if [ "${1:-}" = "--format" ]; then
    FORMAT=1
    shift
fi
UUID=${1:-}
EXPECTED_LABEL=${2:-}
[ -n "$UUID" ] || usage

case "$UUID" in
    /dev/*) DEVICE=$UUID ;;
    *) DEVICE=$(device_for_uuid "$UUID") ;;
esac
if [ -z "$DEVICE" ] || [ ! -b "$DEVICE" ]; then
    echo "ERROR: uuid_not_found"
    exit 2
fi
refuse_if_system_disk "$DEVICE"

if [ "$FORMAT" -eq 1 ]; then
    # TOCTOU note: $DEVICE was validated above; an unplug/replug between the
    # check and mkfs could in principle hand the node to a different disk.
    # That needs physical access mid-operation and the label re-check below
    # narrows it further — accepted residual risk for a headless rig.
    # Empty expected label only matches a disk that truly has none — the
    # retype-to-confirm gate stays real for every labeled drive.
    ACTUAL_LABEL=$(value_for "$DEVICE" LABEL)
    if [ "$ACTUAL_LABEL" != "$EXPECTED_LABEL" ]; then
        echo "ERROR: label_mismatch ${ACTUAL_LABEL:-<none>}"
        exit 4
    fi
    if findmnt -no TARGET "$DEVICE" >/dev/null 2>&1; then
        if ! umount "$DEVICE" 2>/dev/null; then
            echo "ERROR: device_busy"
            exit 5
        fi
    fi
    if ! mkfs.ext4 -F -L "${EXPECTED_LABEL:-ARA-STORE}" "$DEVICE" >/dev/null 2>&1; then
        echo "ERROR: mkfs_failed"
        exit 7
    fi
    # mkfs assigns a NEW uuid — fstab must pin that one, not the old one.
    UUID=$(value_for "$DEVICE" UUID)
    if [ -z "$UUID" ]; then
        echo "ERROR: uuid_not_found"
        exit 2
    fi
    mount_and_own "$UUID" 1
    echo "OK $MOUNT_POINT $UUID"
    exit 0
fi

FS=$(value_for "$DEVICE" TYPE)
if [ "$FS" != "ext4" ]; then
    echo "ERROR: not_ext4 ${FS:-unknown}"
    exit 3
fi
# The caller may have identified the disk by /dev/ path — fstab pins the
# filesystem UUID, never a device path (paths reshuffle across boots).
case "$UUID" in
    /dev/*) UUID=$(value_for "$DEVICE" UUID) ;;
esac
if [ -z "$UUID" ]; then
    echo "ERROR: uuid_not_found"
    exit 2
fi
mount_and_own "$UUID"
echo "OK $MOUNT_POINT"
exit 0
