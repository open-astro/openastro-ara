#!/bin/sh
# §29.1.4 — the ONLY privileged storage operation the daemon can perform.
# Installed 0750 root:openastroara by the .deb; a sudoers drop-in grants the
# daemon user passwordless invocation of this exact path (no bare mount/mkfs
# rights, so this script's validation cannot be bypassed).
#
#   sudo configure-storage.sh <uuid>
#   sudo configure-storage.sh --format <uuid> <expected-label>
#
# Exit codes: 0 ok · 2 uuid_not_found · 3 not_ext4 · 4 label_mismatch
#             5 device_busy · 6 refused (root/boot disk) · 7 mkfs_failed
#             8 mount_failed · 9 usage
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
refuse_if_system_disk() {
    dev=$1
    base=$(lsblk -no PKNAME "$dev" 2>/dev/null || true)
    [ -n "$base" ] && base="/dev/$base" || base="$dev"
    for critical in / /boot /boot/firmware; do
        holder=$(findmnt -no SOURCE "$critical" 2>/dev/null || true)
        [ -z "$holder" ] && continue
        holder_base=$(lsblk -no PKNAME "$holder" 2>/dev/null || true)
        [ -n "$holder_base" ] && holder_base="/dev/$holder_base" || holder_base="$holder"
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
    if grep -qs "[[:space:]]${MOUNT_POINT}[[:space:]]" "$FSTAB"; then
        grep -v "[[:space:]]${MOUNT_POINT}[[:space:]]" "$FSTAB" > "${FSTAB}.ara-tmp"
        cat "${FSTAB}.ara-tmp" > "$FSTAB"
        rm -f "${FSTAB}.ara-tmp"
    fi
    printf 'UUID=%s  %s  ext4  defaults,data=ordered,noatime,errors=remount-ro,nofail,x-systemd.device-timeout=10  0  2\n' \
        "$uuid" "$MOUNT_POINT" >> "$FSTAB"
    systemctl daemon-reload 2>/dev/null || true
}

mount_and_own() { # $1=uuid
    uuid=$1
    mkdir -p "$MOUNT_POINT"
    ensure_fstab_entry "$uuid"
    if ! findmnt -no SOURCE "$MOUNT_POINT" >/dev/null 2>&1; then
        if ! mount "$MOUNT_POINT" 2>/dev/null; then
            echo "ERROR: mount_failed"
            exit 8
        fi
    fi
    chown -R "$OWNER:$OWNER" "$MOUNT_POINT" 2>/dev/null || true
}

FORMAT=0
if [ "${1:-}" = "--format" ]; then
    FORMAT=1
    shift
fi
UUID=${1:-}
EXPECTED_LABEL=${2:-}
[ -n "$UUID" ] || usage

DEVICE=$(device_for_uuid "$UUID")
if [ -z "$DEVICE" ] || [ ! -b "$DEVICE" ]; then
    echo "ERROR: uuid_not_found"
    exit 2
fi
refuse_if_system_disk "$DEVICE"

if [ "$FORMAT" -eq 1 ]; then
    [ -n "$EXPECTED_LABEL" ] || usage
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
    if ! mkfs.ext4 -F -L "$EXPECTED_LABEL" "$DEVICE" >/dev/null 2>&1; then
        echo "ERROR: mkfs_failed"
        exit 7
    fi
    # mkfs assigns a NEW uuid — fstab must pin that one, not the old one.
    UUID=$(value_for "$DEVICE" UUID)
    if [ -z "$UUID" ]; then
        echo "ERROR: uuid_not_found"
        exit 2
    fi
    mount_and_own "$UUID"
    echo "OK $MOUNT_POINT $UUID"
    exit 0
fi

FS=$(value_for "$DEVICE" TYPE)
if [ "$FS" != "ext4" ]; then
    echo "ERROR: not_ext4 ${FS:-unknown}"
    exit 3
fi
mount_and_own "$UUID"
echo "OK $MOUNT_POINT"
exit 0
