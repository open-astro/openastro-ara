#!/usr/bin/env python3
"""configure-storage.sh switching the store from one drive to another.

Run from the repo root:

    python3 -m unittest discover -s scripts/tests -v

With two drives plugged in, picking the second one rewrote fstab but skipped the
mount because the first drive still held /media/openastroara — the helper said OK
and frames kept landing on the first drive. These tests drive the helper against
stand-in blkid/lsblk/findmnt/mount/umount (a mount table kept in a scratch file),
a scratch fstab and a scratch mount point, so the switch logic runs without root.
The helper only accepts real block nodes, so two of the host's /dev block devices
stand in for the drives; nothing ever touches them.
"""

from __future__ import annotations

import os
import stat
import subprocess
import tempfile
import unittest
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent.parent
HELPER = REPO_ROOT / "packaging" / "debian" / "opt" / "openastroara" / "scripts" / "configure-storage.sh"


def block_devices() -> list[str]:
    try:
        names = sorted(os.listdir("/dev"))
    except OSError:
        return []
    found = []
    for name in names:
        path = f"/dev/{name}"
        try:
            if stat.S_ISBLK(os.stat(path).st_mode):
                found.append(path)
        except OSError:
            continue
    return found


DEVICES = block_devices()
# CI runners always have block nodes (loop devices, the runner's disks); a skip there
# would hide the switch regression, so CI fails instead of skipping.
if os.environ.get("CI") and len(DEVICES) < 2:
    raise RuntimeError(f"CI host has {len(DEVICES)} block node(s) in /dev; the store-switch tests need two")

# Every stand-in reads the drive table ("uuid device fstype label" per line) and the
# mount table ("source target" per line, last line on top) from $SHIM_DIR.
SHIMS = {
    "blkid": r"""#!/bin/sh
if [ "$1" = "-U" ]; then
    awk -v u="$2" '$1 == u { print $2 }' "$SHIM_DIR/drives"
    exit 0
fi
# blkid -o value -s TAG DEVICE
tag=$4; dev=$5
case "$tag" in
    UUID) col=1 ;; TYPE) col=3 ;; LABEL) col=4 ;; *) exit 2 ;;
esac
awk -v d="$dev" -v c="$col" '$2 == d { print $c }' "$SHIM_DIR/drives"
""",
    "lsblk": r"""#!/bin/sh
case "$*" in
    *TYPE*) echo part ;;
esac
exit 0
""",
    "findmnt": r"""#!/bin/sh
# findmnt -no SOURCE <target> | findmnt -no TARGET <source>
col=$2; key=$3
case "$key" in
    /|/boot|/boot/firmware) echo /dev/shim-root; exit 0 ;;
esac
if [ "$col" = SOURCE ]; then
    out=$(awk -v k="$key" '$2 == k { print $1 }' "$SHIM_DIR/mounts")
else
    out=$(awk -v k="$key" '$1 == k { print $2 }' "$SHIM_DIR/mounts")
fi
[ -n "$out" ] || exit 1
printf '%s\n' "$out"
""",
    "mount": r"""#!/bin/sh
# mount -t <fs> -o <opts> <device> <mount point>: the copy target's explicit form.
if [ $# -gt 1 ]; then
    for a; do dev=$mp; mp=$a; done
    if grep -qx "$dev" "$SHIM_DIR/mount-fails" 2>/dev/null; then
        exit 32
    fi
    echo "$dev $mp" >> "$SHIM_DIR/mounts"
    exit 0
fi
# mount <mount point>: resolve it through $FSTAB like the real one does.
uuid=$(awk -v m="$1" '$2 == m { sub(/^UUID=/, "", $1); print $1 }' "$FSTAB" | tail -n1)
dev=$(awk -v u="$uuid" '$1 == u { print $2 }' "$SHIM_DIR/drives")
[ -n "$dev" ] || exit 32
if grep -qx "$dev" "$SHIM_DIR/mount-fails" 2>/dev/null; then
    exit 32
fi
echo "$dev $1" >> "$SHIM_DIR/mounts"
""",
    "umount": r"""#!/bin/sh
# umount <mount point or device>: refuse a busy source, else drop the top match.
src=$(awk -v k="$1" '$1 == k || $2 == k { s = $1 } END { print s }' "$SHIM_DIR/mounts")
[ -n "$src" ] || exit 32
if grep -qx "$src" "$SHIM_DIR/busy" 2>/dev/null; then
    exit 32
fi
awk -v k="$1" '{ l[NR] = $0; if ($1 == k || $2 == k) last = NR }
    END { for (i = 1; i <= NR; i++) if (i != last) print l[i] }' "$SHIM_DIR/mounts" > "$SHIM_DIR/mounts.new"
mv "$SHIM_DIR/mounts.new" "$SHIM_DIR/mounts"
""",
    "mkfs.exfat": r"""#!/bin/sh
for a; do dev=$a; done
echo "$dev" >> "$SHIM_DIR/erased"
[ ! -e "$SHIM_DIR/mkfs-fails" ]
""",
}
NO_OPS = ("sync", "systemctl", "chown", "fsck.exfat", "e2fsck", "udevadm", "wipefs", "sfdisk")


@unittest.skipIf(len(DEVICES) < 2, "needs two block nodes in /dev to stand in for the drives")
class HelperRig(unittest.TestCase):
    """Scratch fstab, mount table, store path and copy path; OLD is the mounted store."""

    OLD = ("aaaa-0001", DEVICES[0] if DEVICES else "")
    NEW = ("bbbb-0002", DEVICES[1] if len(DEVICES) > 1 else "")

    def setUp(self) -> None:
        self.tmp = tempfile.TemporaryDirectory()
        root = Path(self.tmp.name)
        self.shims = root / "shims"
        self.shims.mkdir()
        for name, body in SHIMS.items():
            self._shim(name, body)
        for name in NO_OPS:
            self._shim(name, "#!/bin/sh\nexit 0\n")
        self.mount_point = str(root / "store")
        os.mkdir(self.mount_point)
        self.copy_point = str(root / "copy")
        (self.shims / "drives").write_text(
            f"{self.OLD[0]} {self.OLD[1]} exfat ARA-DISK\n{self.NEW[0]} {self.NEW[1]} exfat ARA-DISK\n")
        self.fstab = root / "fstab"
        self.fstab.write_text(
            "PARTUUID=aaaa-02  /  ext4  defaults,noatime  0  1\n"
            f"UUID={self.OLD[0]}  {self.mount_point}  exfat  defaults,nofail  0  0\n")
        self.mounts = self.shims / "mounts"
        self.mounts.write_text(f"{self.OLD[1]} {self.mount_point}\n")
        self.env = dict(
            os.environ,
            PATH=f"{self.shims}:{os.environ.get('PATH', '/usr/bin:/bin')}",
            SHIM_DIR=str(self.shims),
            FSTAB=str(self.fstab),
            FSTAB_LOCK=str(root / "fstab.lock"),
            ARA_MOUNT_POINT=self.mount_point,
            ARA_COPY_POINT=self.copy_point,
            ARA_STORAGE_OWNER=os.environ.get("USER") or subprocess.check_output(["id", "-un"], text=True).strip(),
        )

    def tearDown(self) -> None:
        self.tmp.cleanup()

    def _shim(self, name: str, body: str) -> None:
        path = self.shims / name
        path.write_text(body)
        path.chmod(0o755)

    def run_helper(self, *args: str) -> subprocess.CompletedProcess[str]:
        return subprocess.run(["sh", str(HELPER), *args], capture_output=True, text=True, env=self.env)

    def mounted(self) -> list[str]:
        return [line.split()[0] for line in self.mounts.read_text().splitlines()
                if line.split()[1:] == [self.mount_point]]

    def fstab_uuid(self) -> str:
        lines = [line for line in self.fstab.read_text().splitlines() if f"  {self.mount_point}  " in line]
        self.assertEqual(len(lines), 1, lines)
        return lines[0].split()[0].removeprefix("UUID=")

    def copy_mounted(self) -> list[str]:
        return [line.split()[0] for line in self.mounts.read_text().splitlines()
                if line.split()[1:] == [self.copy_point]]


class ConfigureStorageSwitch(HelperRig):

    def test_using_a_second_drive_unmounts_the_first_and_mounts_the_second(self) -> None:
        result = self.run_helper(self.NEW[0])
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(self.mounted(), [self.NEW[1]], "only the new drive holds the store path")
        self.assertEqual(self.fstab_uuid(), self.NEW[0])

    def test_a_busy_previous_store_refuses_and_changes_nothing(self) -> None:
        (self.shims / "busy").write_text(f"{self.OLD[1]}\n")
        result = self.run_helper(self.NEW[0])
        self.assertEqual(result.returncode, 5, result.stdout + result.stderr)
        self.assertIn("ERROR: device_busy previous_store", result.stdout)
        self.assertEqual(self.mounted(), [self.OLD[1]], "the old store stays mounted")
        self.assertEqual(self.fstab_uuid(), self.OLD[0], "fstab is not repointed when the switch is refused")

    def test_reusing_the_current_store_leaves_its_mount_alone(self) -> None:
        (self.shims / "busy").write_text(f"{self.OLD[1]}\n")
        result = self.run_helper(self.OLD[0])
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(self.mounted(), [self.OLD[1]])

    def test_a_mount_that_lands_on_another_drive_is_not_ok(self) -> None:
        # fstab resolves to the old drive (a stale entry the rebuild missed): the helper must
        # not report the new drive as the store.
        self._shim("mount", f"#!/bin/sh\necho \"{self.OLD[1]} $1\" >> \"$SHIM_DIR/mounts\"\n")
        result = self.run_helper(self.NEW[0])
        self.assertEqual(result.returncode, 8, result.stdout + result.stderr)
        self.assertIn("ERROR: mount_failed wrong_device", result.stdout)
        self.assertEqual(self.fstab_uuid(), self.OLD[0], "fstab points back at the previous store")

    def test_a_new_drive_that_will_not_mount_puts_the_previous_store_back(self) -> None:
        (self.shims / "mount-fails").write_text(f"{self.NEW[1]}\n")
        result = self.run_helper(self.NEW[0])
        self.assertEqual(result.returncode, 8, result.stdout + result.stderr)
        self.assertIn("ERROR: mount_failed", result.stdout)
        self.assertEqual(self.mounted(), [self.OLD[1]], "frames never fall through to the system disk")
        self.assertEqual(self.fstab_uuid(), self.OLD[0])

    def test_a_symlinked_mount_source_is_the_same_drive(self) -> None:
        # findmnt can report a /dev/disk/by-* style link; it is still the current store.
        link = Path(self.tmp.name) / "by-id-old"
        link.symlink_to(self.OLD[1])
        self.mounts.write_text(f"{link} {self.mount_point}\n")
        (self.shims / "busy").write_text(f"{link}\n")
        result = self.run_helper(self.OLD[0])
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(self.mounted(), [str(link)], "no needless unmount of the store")

    def test_erasing_a_second_drive_refuses_a_busy_store_before_erasing(self) -> None:
        (self.shims / "busy").write_text(f"{self.OLD[1]}\n")
        result = self.run_helper("--format", "--fs", "exfat", self.NEW[0], "ARA-DISK")
        self.assertEqual(result.returncode, 5, result.stdout + result.stderr)
        self.assertIn("ERROR: device_busy previous_store", result.stdout)
        self.assertFalse((self.shims / "erased").exists(), "the confirmed disk is still intact")
        self.assertEqual(self.mounted(), [self.OLD[1]])

    def test_erasing_a_second_drive_switches_the_store_to_it(self) -> None:
        result = self.run_helper("--format", "--fs", "exfat", self.NEW[0], "ARA-DISK")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(self.mounted(), [self.NEW[1]])
        self.assertEqual(self.fstab_uuid(), self.NEW[0])

    def test_a_failed_erase_puts_the_previous_store_back(self) -> None:
        (self.shims / "mkfs-fails").write_text("")
        result = self.run_helper("--format", "--fs", "exfat", self.NEW[0], "ARA-DISK")
        self.assertEqual(result.returncode, 7, result.stdout + result.stderr)
        self.assertEqual(self.mounted(), [self.OLD[1]])
        self.assertEqual(self.fstab_uuid(), self.OLD[0])

    def test_checking_a_disk_that_is_not_the_store_does_not_switch_to_it(self) -> None:
        result = self.run_helper("--check", self.NEW[0])
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(self.mounted(), [self.OLD[1]], "the store stays put")
        self.assertEqual(self.fstab_uuid(), self.OLD[0])

    def test_checking_the_store_remounts_it(self) -> None:
        result = self.run_helper("--check", self.OLD[0])
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("checked clean", result.stdout)
        self.assertEqual(self.mounted(), [self.OLD[1]])

    def test_checking_the_store_reports_a_repair(self) -> None:
        # fsck exit 1 = errors fixed. The fstab rebuild during the remount reused the same
        # variable and turned every repair into "checked clean".
        self._shim("fsck.exfat", "#!/bin/sh\nexit 1\n")
        result = self.run_helper("--check", self.OLD[0])
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("checked repaired", result.stdout)

    def test_checking_a_disk_that_is_not_the_store_names_that_disk(self) -> None:
        result = self.run_helper("--check", self.NEW[0])
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertNotIn(self.mount_point, result.stdout, "nothing was mounted at the store path")
        self.assertIn(self.NEW[1], result.stdout)

    @unittest.skipIf(os.geteuid() == 0, "root writes through the read-only directory")
    def test_a_failed_fstab_rebuild_after_the_release_remounts_the_previous_store(self) -> None:
        # The rebuild's temp file cannot be created beside fstab: fstab itself is untouched,
        # so mounting the store path from it puts the previous drive back.
        root = Path(self.tmp.name)
        root.chmod(0o555)
        try:
            result = self.run_helper(self.NEW[0])
        finally:
            root.chmod(0o755)
        self.assertEqual(result.returncode, 8, result.stdout + result.stderr)
        self.assertIn("ERROR: fstab_tmp_failed", result.stdout)
        self.assertEqual(self.mounted(), [self.OLD[1]], "frames never fall through to the system disk")
        self.assertEqual(self.fstab_uuid(), self.OLD[0])

    def test_checking_an_ejected_store_remounts_it_from_fstab(self) -> None:
        self.mounts.write_text("")
        result = self.run_helper("--check", self.OLD[0])
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(self.mounted(), [self.OLD[1]])




class ConfigureStorageCopyTarget(HelperRig):
    def test_a_second_drive_mounts_at_the_copy_path_and_leaves_the_store_alone(self) -> None:
        fstab_before = self.fstab.read_text()
        result = self.run_helper("--mount-copy", self.NEW[0])
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(result.stdout.strip(), f"OK {self.copy_point}")
        self.assertEqual(self.copy_mounted(), [self.NEW[1]])
        self.assertEqual(self.mounted(), [self.OLD[1]], "the store stays where it is")
        self.assertEqual(self.fstab.read_text(), fstab_before, "a copy target never gets an fstab line")

    def test_the_store_is_refused_as_a_copy_target(self) -> None:
        result = self.run_helper("--mount-copy", self.OLD[0])
        self.assertEqual(result.returncode, 6, result.stdout + result.stderr)
        self.assertIn("ERROR: refused is_store", result.stdout)
        self.assertEqual(self.copy_mounted(), [])

    def test_a_drive_mounted_elsewhere_is_refused(self) -> None:
        self.mounts.write_text(self.mounts.read_text() + f"{self.NEW[1]} /media/somebody\n")
        result = self.run_helper("--mount-copy", self.NEW[0])
        self.assertEqual(result.returncode, 5, result.stdout + result.stderr)
        self.assertIn("ERROR: device_busy mounted_elsewhere", result.stdout)

    def test_mounting_the_same_target_twice_is_ok(self) -> None:
        self.assertEqual(self.run_helper("--mount-copy", self.NEW[0]).returncode, 0)
        result = self.run_helper("--mount-copy", self.NEW[0])
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(self.copy_mounted(), [self.NEW[1]], "no second layer")

    def test_unmount_copy_releases_the_target(self) -> None:
        self.assertEqual(self.run_helper("--mount-copy", self.NEW[0]).returncode, 0)
        result = self.run_helper("--unmount-copy")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(self.copy_mounted(), [])
        self.assertEqual(self.mounted(), [self.OLD[1]])

    def test_unmount_copy_with_nothing_mounted_is_ok(self) -> None:
        result = self.run_helper("--unmount-copy")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_a_busy_copy_target_refuses_to_release(self) -> None:
        self.assertEqual(self.run_helper("--mount-copy", self.NEW[0]).returncode, 0)
        (self.shims / "busy").write_text(f"{self.NEW[1]}\n")
        result = self.run_helper("--unmount-copy")
        self.assertEqual(result.returncode, 5, result.stdout + result.stderr)
        self.assertIn("ERROR: device_busy copy_target", result.stdout)

    def test_unmount_copy_takes_no_arguments(self) -> None:
        self.assertEqual(self.run_helper("--unmount-copy", self.NEW[0]).returncode, 9)

    def test_a_target_that_will_not_mount_is_a_typed_failure(self) -> None:
        (self.shims / "mount-fails").write_text(f"{self.NEW[1]}\n")
        result = self.run_helper("--mount-copy", self.NEW[0])
        self.assertEqual(result.returncode, 8, result.stdout + result.stderr)
        self.assertIn("ERROR: mount_failed", result.stdout)


class RestoreDoesNotRemountTheRejectedDrive(HelperRig):
    @unittest.skipIf(os.geteuid() == 0, "root writes through the read-only directory")
    def test_a_restore_whose_rebuild_fails_leaves_the_rejected_drive_unmounted(self) -> None:
        # wrong_device path: the new drive's line is in fstab, then the restore's own rebuild
        # fails. Its failure branch must not mount the store path from the NEW line.
        root = Path(self.tmp.name)
        self._shim("mount", f"""#!/bin/sh
if [ ! -e "$SHIM_DIR/first-mount-done" ]; then
    touch "$SHIM_DIR/first-mount-done"
    chmod 0555 "{root}"
    echo "{self.OLD[1]} $1" >> "$SHIM_DIR/mounts"
    exit 0
fi
uuid=$(awk -v m="$1" '$2 == m {{ sub(/^UUID=/, "", $1); print $1 }}' "$FSTAB" | tail -n1)
dev=$(awk -v u="$uuid" '$1 == u {{ print $2 }}' "$SHIM_DIR/drives")
echo "$dev $1" >> "$SHIM_DIR/mounts"
""")
        try:
            result = self.run_helper(self.NEW[0])
        finally:
            root.chmod(0o755)
        self.assertEqual(result.returncode, 8, result.stdout + result.stderr)
        self.assertIn("ERROR: mount_failed wrong_device", result.stdout)
        self.assertNotIn(self.NEW[1], self.mounted(), "the rejected drive was remounted as the store")


if __name__ == "__main__":
    unittest.main()
