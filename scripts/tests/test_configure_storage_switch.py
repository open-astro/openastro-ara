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
# mount <mount point>: resolve it through $FSTAB like the real one does.
uuid=$(awk -v m="$1" '$2 == m { sub(/^UUID=/, "", $1); print $1 }' "$FSTAB" | tail -n1)
dev=$(awk -v u="$uuid" '$1 == u { print $2 }' "$SHIM_DIR/drives")
[ -n "$dev" ] || exit 32
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
}
NO_OPS = ("sync", "systemctl", "chown", "fsck.exfat", "e2fsck", "udevadm")


@unittest.skipIf(len(DEVICES) < 2, "needs two block nodes in /dev to stand in for the drives")
class ConfigureStorageSwitch(unittest.TestCase):
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

    def test_checking_an_ejected_store_remounts_it_from_fstab(self) -> None:
        self.mounts.write_text("")
        result = self.run_helper("--check", self.OLD[0])
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(self.mounted(), [self.OLD[1]])


if __name__ == "__main__":
    unittest.main()
