#!/usr/bin/env python3
"""#1248: configure-storage.sh's locked /etc/fstab rebuild, driven against a scratch file.

Run from the repo root:

    python3 -m unittest discover -s scripts/tests -v

Uses the helper's bench mode (`--fstab-entry <uuid> [fs]`, which runs ONLY
`ensure_fstab_entry`) with the FSTAB / FSTAB_LOCK overrides. Until now the only
evidence that concurrent rebuilds leave exactly one ARA line was a manual run on
the SBC VM. Linux-only: the lock is util-linux flock(1); macOS has neither it nor
GNU mktemp semantics the helper relies on.
"""

from __future__ import annotations

import os
import shutil
import subprocess
import sys
import tempfile
import unittest
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent.parent
HELPER = REPO_ROOT / "packaging" / "debian" / "opt" / "openastroara" / "scripts" / "configure-storage.sh"
MOUNT_POINT = "/media/openastroara"
BASE_FSTAB = (
    "# /etc/fstab: static file system information.\n"
    "PARTUUID=aaaa-01  /boot/firmware  vfat  defaults  0  2\n"
    "PARTUUID=aaaa-02  /  ext4  defaults,noatime  0  1\n"
    f"UUID=old-drive  {MOUNT_POINT}  ext4  defaults,nofail  0  2\n"
)


def linux_with_flock() -> bool:
    return sys.platform.startswith("linux") and shutil.which("flock") is not None


@unittest.skipUnless(linux_with_flock(), "configure-storage.sh's lock needs util-linux flock (Linux)")
@unittest.skipIf(os.geteuid() == 0, "the bench mode refuses to run as root by design; see ConfigureStorageFstabRootFence")
class ConfigureStorageFstabRebuild(unittest.TestCase):
    def setUp(self) -> None:
        self.tmp = tempfile.TemporaryDirectory()
        root = Path(self.tmp.name)
        self.fstab = root / "fstab"
        self.fstab.write_text(BASE_FSTAB)
        self.lock = root / "fstab.lock"
        self.env = dict(
            os.environ,
            FSTAB=str(self.fstab),
            FSTAB_LOCK=str(self.lock),
            # The helper resolves the mount owner with `id`; the test user always resolves.
            ARA_STORAGE_OWNER=os.environ.get("USER") or subprocess.check_output(["id", "-un"], text=True).strip(),
        )

    def tearDown(self) -> None:
        self.tmp.cleanup()

    def run_entry(self, uuid: str, fs: str = "ext4", **env_overrides: str) -> subprocess.CompletedProcess[str]:
        env = dict(self.env, **env_overrides)
        return subprocess.run(["sh", str(HELPER), "--fstab-entry", uuid, fs], capture_output=True, text=True, env=env)

    def ara_lines(self) -> list[str]:
        return [line for line in self.fstab.read_text().splitlines() if f"  {MOUNT_POINT}  " in line or f" {MOUNT_POINT} " in line]

    def test_one_rebuild_replaces_the_previous_ara_line_and_keeps_the_rest(self) -> None:
        result = self.run_entry("1111-2222")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        text = self.fstab.read_text()
        self.assertIn("PARTUUID=aaaa-02  /  ext4", text, "the root entry survives the rebuild")
        self.assertIn("PARTUUID=aaaa-01  /boot/firmware", text, "the boot entry survives the rebuild")
        self.assertNotIn("old-drive", text, "the previous drive's line for the mount point is dropped")
        lines = self.ara_lines()
        self.assertEqual(len(lines), 1, lines)
        self.assertTrue(lines[0].startswith("UUID=1111-2222  "), lines[0])
        self.assertFalse(list(Path(self.tmp.name).glob("fstab.ara-*")), "no temp file left beside fstab")

    def test_six_concurrent_rebuilds_leave_exactly_one_ara_line(self) -> None:
        # The #1135 lock + per-call temp name: before them two rebuilds clobbered each other's
        # temp file, and the mount point could end up with two owners or none.
        uuids = [f"cccc-{i:04d}" for i in range(6)]
        with ThreadPoolExecutor(max_workers=6) as pool:
            results = list(pool.map(self.run_entry, uuids))
        for r in results:
            self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        lines = self.ara_lines()
        self.assertEqual(len(lines), 1, f"exactly one ARA line after six racing rebuilds: {lines}")
        self.assertTrue(any(lines[0].startswith(f"UUID={u}  ") for u in uuids), lines[0])
        text = self.fstab.read_text()
        self.assertIn("PARTUUID=aaaa-02  /  ext4", text, "the root entry survives six racing rebuilds")
        self.assertFalse(list(Path(self.tmp.name).glob("fstab.ara-*")), "every temp file was renamed or removed")

    def test_an_unopenable_lock_path_falls_back_to_the_unlocked_rebuild(self) -> None:
        # A read-only /run made `exec 9>lock` fatal under a POSIX sh: the helper died with no
        # typed line and its temp file in place. It now warns and rebuilds unlocked.
        result = self.run_entry("dddd-0001", FSTAB_LOCK=str(Path(self.tmp.name) / "no-such-dir" / "fstab.lock"))
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("WARN: fstab_lock_unavailable", result.stderr)
        self.assertEqual(len(self.ara_lines()), 1)
        self.assertFalse(list(Path(self.tmp.name).glob("fstab.ara-*")))

    def test_an_unreadable_fstab_is_a_typed_exit_8_and_leaves_the_file_alone(self) -> None:
        missing = Path(self.tmp.name) / "absent-fstab"
        result = self.run_entry("eeee-0001", FSTAB=str(missing))
        self.assertEqual(result.returncode, 8, result.stdout + result.stderr)
        self.assertIn("ERROR: fstab_", result.stdout, "a typed ERROR line, never a bare death")
        self.assertFalse(missing.exists(), "nothing is written when the source could not be read")

    def test_fstab_entry_mode_is_refused_against_the_real_fstab_or_a_shaped_uuid(self) -> None:
        # r1 finding: the mode skips blkid and the system-disk refusal, and the daemon user can
        # hand root any argv through the request file — so it must be unreachable in production.
        env = dict(self.env)
        env.pop("FSTAB")
        real = subprocess.run(["sh", str(HELPER), "--fstab-entry", "1111-2222"], capture_output=True, text=True, env=env)
        self.assertEqual(real.returncode, 9, real.stdout + real.stderr)
        self.assertIn("ERROR: usage fstab_entry_needs_scratch_fstab", real.stdout)
        explicit = self.run_entry("1111-2222", FSTAB="/etc/fstab")
        self.assertEqual(explicit.returncode, 9, "naming /etc/fstab explicitly is refused too")
        for bad in ("abcd /etc ext4 defaults 0 0 #", "abcd\t/etc", "../x", "1111;touch /tmp/x"):
            r = self.run_entry(bad)
            self.assertEqual(r.returncode, 9, f"uuid {bad!r} must be refused: {r.stdout}")
            self.assertIn("ERROR: usage fstab_entry_bad_uuid", r.stdout)
        self.assertEqual(self.fstab.read_text(), BASE_FSTAB, "nothing was written by any refused run")

    def test_a_rebuild_waits_for_an_externally_held_lock(self) -> None:
        # The six-way race above can pass by luck when the runs do not overlap; this one cannot:
        # the lock is held for ~2 s by another process, and the rebuild must not finish before it.
        import time
        self.lock.touch()
        holder = subprocess.Popen(["flock", str(self.lock), "sh", "-c", "sleep 2"])
        try:
            # Wait until the holder actually has the lock (#1353 note): a non-blocking probe
            # fails exactly while it is held, so no fixed sleep decides the race.
            deadline = time.monotonic() + 5
            while subprocess.run(["flock", "-n", str(self.lock), "true"]).returncode == 0:
                self.assertLess(time.monotonic(), deadline, "the holder never took the lock")
                time.sleep(0.02)
            started = time.monotonic()
            result = self.run_entry("1111-3333")
            elapsed = time.monotonic() - started
        finally:
            holder.wait(timeout=10)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertGreaterEqual(elapsed, 1.5, "the rebuild ran while another process held the fstab lock")
        self.assertEqual(len(self.ara_lines()), 1)

    def test_exfat_entries_carry_the_owner_uid_and_gid(self) -> None:
        result = self.run_entry("ffff-0001", "exfat")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        line = self.ara_lines()[0]
        self.assertIn("  exfat  ", line)
        self.assertIn(f"uid={os.getuid()},gid={os.getgid()}", line)


@unittest.skipUnless(linux_with_flock() and os.geteuid() == 0, "the root fence can only be observed as root")
class ConfigureStorageFstabRootFence(unittest.TestCase):
    def test_fstab_entry_mode_is_refused_as_root(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            fstab = Path(tmp) / "fstab"
            fstab.write_text(BASE_FSTAB)
            env = dict(os.environ, FSTAB=str(fstab), FSTAB_LOCK=str(Path(tmp) / "lock"), ARA_STORAGE_OWNER="root")
            r = subprocess.run(["sh", str(HELPER), "--fstab-entry", "1111-2222"], capture_output=True, text=True, env=env)
            self.assertEqual(r.returncode, 9, r.stdout + r.stderr)
            self.assertIn("fstab_entry_refused_as_root", r.stdout)
            self.assertEqual(fstab.read_text(), BASE_FSTAB)


if __name__ == "__main__":
    unittest.main()
