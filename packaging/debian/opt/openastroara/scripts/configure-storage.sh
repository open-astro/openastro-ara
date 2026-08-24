#!/bin/sh
# §29.1.4 — the ONLY privileged storage operation the daemon can perform.
# Installed 0750 root:openastroara by the .deb; a sudoers drop-in grants the
# daemon user passwordless invocation of this exact path (no bare mount/mkfs
# rights, so this script's validation cannot be bypassed).
#
#   sudo configure-storage.sh <uuid-or-/dev/path>
#   sudo configure-storage.sh --format [--fs exfat|ext4] <uuid-or-/dev/path> [<expected-label>]
#   sudo configure-storage.sh --check <uuid>
#   sudo configure-storage.sh --eject <uuid>
#
# A /dev/ path identifies a brand-new blank disk (no filesystem, hence no
# UUID yet). An empty expected label is legal only when the disk truly has
# no label to retype (blank or unlabeled). Every fresh format is labeled
# ARA-DISK — the drive announces itself on any computer it lands on.
#
# Filesystems: exFAT is the take-the-drive-home choice (reads natively on
# Windows/macOS; §29 field workflow) and the format default; ext4 is the
# rig-resident choice (journaled, best on-Pi repair tooling). --check runs
# the matching fsck (drive briefly unmounted) — exFAT has no journal, so
# the user-triggered check is its recovery story. --eject flushes and
# unmounts for safe removal (fstab entry stays — a replug automounts).
#
# Exit codes: 0 ok · 2 uuid_not_found · 3 not_ext4 (unsupported fs) ·
#             4 label_mismatch · 5 device_busy · 6 refused (root/boot disk) ·
#             7 mkfs_failed · 8 mount_failed · 9 usage · 10 chown_failed ·
#             11 fsck_failed
set -eu

MOUNT_POINT=/media/openastroara
FSTAB=/etc/fstab
OWNER=${ARA_STORAGE_OWNER:-openastroara}
# Dev rigs run the daemon as a regular user without the .deb's service
# account — fall back to whoever invoked sudo so mounts still own correctly.
# A silent fall-through to root would leave the (non-root) daemon unable to
# write the newly mounted exFAT volume — fail loudly instead.
if ! id -u "$OWNER" >/dev/null 2>&1; then
    OWNER=${SUDO_USER:-}
    if [ -z "$OWNER" ] || ! id -u "$OWNER" >/dev/null 2>&1; then
        echo "ERROR: chown_failed no_resolvable_owner"
        exit 10
    fi
fi

usage() {
    echo "usage: $0 [--format] [--fs exfat|ext4] [--check] [--eject] <uuid> [<expected-label>]" >&2
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
    # A partition ALREADY mounted as the data store (under /media, e.g.
    # /media/openastroara) is a dedicated on-board store partition, not the
    # running system — allow (re)configuring it without reformatting the boot
    # volume. Refuse everything else on the boot disk (root/boot holders and
    # unmounted system-disk partitions) below.
    mnt=$(findmnt -no TARGET "$dev" 2>/dev/null || true)
    case "$mnt" in
        /media/*) return 0 ;;
    esac
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

ensure_fstab_entry() { # $1=uuid $2=fstype
    uuid=$1
    fstype=$2
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
    if [ "$fstype" = "exfat" ]; then
        # exFAT carries no Unix ownership — the mount options ARE the
        # ownership, so the daemon owns every file without any chown.
        uid=$(id -u "$OWNER")
        gid=$(id -g "$OWNER")
        printf 'UUID=%s  %s  exfat  defaults,noatime,uid=%s,gid=%s,fmask=0113,dmask=0002,nofail,x-systemd.device-timeout=10  0  0\n' \
            "$uuid" "$MOUNT_POINT" "$uid" "$gid" >> "${FSTAB}.ara-tmp"
    else
        printf 'UUID=%s  %s  ext4  defaults,data=ordered,noatime,errors=remount-ro,nofail,x-systemd.device-timeout=10  0  2\n' \
            "$uuid" "$MOUNT_POINT" >> "${FSTAB}.ara-tmp"
    fi
    chmod 644 "${FSTAB}.ara-tmp"
    sync
    mv "${FSTAB}.ara-tmp" "$FSTAB"
    systemctl daemon-reload 2>/dev/null || true
}

mount_and_own() { # $1=uuid $2=fstype $3=deep-chown (1 after mkfs, else top-level only)
    uuid=$1
    fstype=$2
    deep=${3:-0}
    mkdir -p "$MOUNT_POINT"
    ensure_fstab_entry "$uuid" "$fstype"
    if ! findmnt -no SOURCE "$MOUNT_POINT" >/dev/null 2>&1; then
        if ! mount "$MOUNT_POINT" 2>/dev/null; then
            echo "ERROR: mount_failed"
            exit 8
        fi
    fi
    # exFAT ownership comes from the mount options above; chown is not a
    # thing there (it would fail with EPERM on every file).
    if [ "$fstype" = "exfat" ]; then
        return 0
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
CHECK=0
EJECT=0
NEW_FS=exfat
while [ $# -gt 0 ]; do
    case "$1" in
        --format) FORMAT=1; shift ;;
        --check) CHECK=1; shift ;;
        --eject) EJECT=1; shift ;;
        --fs)
            NEW_FS=${2:-}
            case "$NEW_FS" in exfat|ext4) ;; *) usage ;; esac
            shift 2 ;;
        --*) usage ;;
        *) break ;;
    esac
done
[ $((FORMAT + CHECK + EJECT)) -gt 1 ] && usage
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

if [ "$EJECT" -eq 1 ]; then
    # Safe removal: flush everything, then unmount. The fstab entry stays
    # (nofail), so plugging the drive back in automounts it. A busy mount
    # (open files) refuses rather than forcing.
    if findmnt -no TARGET "$DEVICE" >/dev/null 2>&1; then
        sync
        if ! umount "$DEVICE" 2>/dev/null; then
            echo "ERROR: device_busy"
            exit 5
        fi
    fi
    echo "OK ejected"
    exit 0
fi

if [ "$CHECK" -eq 1 ]; then
    FS=$(value_for "$DEVICE" TYPE)
    case "$FS" in exfat|ext4) ;; *) echo "ERROR: not_ext4 ${FS:-unknown}"; exit 3 ;; esac
    # fsck needs the filesystem quiet — unmount, check, remount. A busy
    # mount (something holding files open) refuses rather than forcing.
    if findmnt -no TARGET "$DEVICE" >/dev/null 2>&1; then
        if ! umount "$DEVICE" 2>/dev/null; then
            echo "ERROR: device_busy"
            exit 5
        fi
    fi
    rc=0
    OKMAX=1
    if [ "$FS" = "exfat" ]; then
        fsck.exfat -y "$DEVICE" >/dev/null 2>&1 || rc=$?
    else
        # e2fsck: 1 = fixed, 2 = fixed + "reboot suggested" (root-fs
        # semantics; the store is never root) — both are successful repairs.
        OKMAX=2
        e2fsck -f -y "$DEVICE" >/dev/null 2>&1 || rc=$?
    fi
    # Exit codes up to OKMAX mean clean or repaired — a successful check.
    if [ "$rc" -gt "$OKMAX" ]; then
        echo "ERROR: fsck_failed exit=$rc"
        exit 11
    fi
    mount_and_own "$UUID" "$FS" 0
    if [ "$rc" -ge 1 ]; then
        echo "OK $MOUNT_POINT checked repaired"
    else
        echo "OK $MOUNT_POINT checked clean"
    fi
    exit 0
fi

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
    # Erasing means the WHOLE DISK: a UUID that resolved to a partition
    # (the normal case — the store lives in one) promotes to its parent so
    # the partition table itself is rebuilt, not just the filesystem inside
    # the old one. The system-disk refusal already ran against this base.
    if [ "$(lsblk -no TYPE "$DEVICE" 2>/dev/null | head -n1)" = "part" ]; then
        DEVICE=$(base_disk "$DEVICE")
    fi
    # Unmount every mounted piece of the disk before touching it.
    for m in $(lsblk -lno PATH "$DEVICE" 2>/dev/null); do
        if findmnt -no TARGET "$m" >/dev/null 2>&1; then
            if ! umount "$m" 2>/dev/null; then
                echo "ERROR: device_busy"
                exit 5
            fi
        fi
    done
    # Old filesystem/partition signatures must ALL go — a leftover backup
    # GPT at the end of the disk makes macOS/Windows call the drive
    # unreadable even though the new filesystem at sector 0 is fine.
    wipefs -a "$DEVICE" >/dev/null 2>&1 || true
    # A whole disk gets the layout every retail USB drive ships with:
    # a partition table and ONE partition, then the filesystem inside it —
    # the shape Windows and macOS expect. (Formatting the raw device —
    # "superfloppy" — is what made the take-home drive unreadable off-rig.)
    KIND=$(lsblk -no TYPE "$DEVICE" 2>/dev/null | head -n1)
    if [ "$KIND" = "disk" ]; then
        # Explicit GPT (no 2 TiB MBR ceiling) with the partition-type GUID
        # spelled out: modern sfdisk defaults unknown types to "Linux
        # filesystem", and macOS/Windows refuse to even probe a partition
        # typed Linux — the drive read as "initialize me" despite valid
        # exFAT inside. Microsoft Basic Data is what retail drives use.
        PTYPE=0FC63DAF-8483-4772-8E79-3D69D8477DE4   # Linux filesystem
        [ "$NEW_FS" = "exfat" ] && PTYPE=EBD0A0A2-B9E5-4433-87C0-68B6B72699C7  # Microsoft basic data
        if ! printf 'label: gpt\ntype=%s\n' "$PTYPE" | sfdisk --quiet --wipe always "$DEVICE" >/dev/null 2>&1; then
            echo "ERROR: mkfs_failed partition_table"
            exit 7
        fi
        udevadm settle 2>/dev/null || sleep 2
        case "$DEVICE" in
            *[0-9]) PART="${DEVICE}p1" ;;
            *) PART="${DEVICE}1" ;;
        esac
        # udev can lag creating the node on slow hubs — give it a moment.
        i=0
        while [ ! -b "$PART" ] && [ "$i" -lt 10 ]; do
            sleep 1
            i=$((i + 1))
        done
        if [ ! -b "$PART" ]; then
            echo "ERROR: mkfs_failed partition_node_missing"
            exit 7
        fi
        DEVICE=$PART
        wipefs -a "$DEVICE" >/dev/null 2>&1 || true
    fi
    if [ "$NEW_FS" = "exfat" ]; then
        mkfs.exfat -L "ARA-DISK" "$DEVICE" >/dev/null 2>&1 || { echo "ERROR: mkfs_failed"; exit 7; }
    else
        mkfs.ext4 -F -L "ARA-DISK" "$DEVICE" >/dev/null 2>&1 || { echo "ERROR: mkfs_failed"; exit 7; }
    fi
    # mkfs assigns a NEW uuid — fstab must pin that one, not the old one.
    UUID=$(value_for "$DEVICE" UUID)
    if [ -z "$UUID" ]; then
        echo "ERROR: uuid_not_found"
        exit 2
    fi
    mount_and_own "$UUID" "$NEW_FS" 1
    echo "OK $MOUNT_POINT $UUID"
    exit 0
fi

FS=$(value_for "$DEVICE" TYPE)
case "$FS" in
    exfat|ext4) ;;
    *)
        # Error code name kept for wire compatibility — semantically
        # "not a filesystem ARA can use as its store".
        echo "ERROR: not_ext4 ${FS:-unknown}"
        exit 3 ;;
esac
# The caller may have identified the disk by /dev/ path — fstab pins the
# filesystem UUID, never a device path (paths reshuffle across boots).
case "$UUID" in
    /dev/*) UUID=$(value_for "$DEVICE" UUID) ;;
esac
if [ -z "$UUID" ]; then
    echo "ERROR: uuid_not_found"
    exit 2
fi
mount_and_own "$UUID" "$FS" 0
echo "OK $MOUNT_POINT"
exit 0
