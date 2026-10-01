#!/usr/bin/env python3
"""#1122: update-request.sh, the root wrapper of openastroara-update@.service.

Run from the repo root:

    python3 -m unittest discover -s scripts/tests -v

Drives the real wrapper with its DIR/HELPER overrides and a stand-in helper
that echoes its argv, the way StorageExchangeRoundTripTest drives
storage-request.sh. Linux-only: the wrapper relies on GNU `mv -T`.
"""

from __future__ import annotations

import os
import stat
import subprocess
import sys
import tempfile
import time
import unittest
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent.parent
WRAPPER = REPO_ROOT / "packaging" / "debian" / "opt" / "openastroara" / "scripts" / "update-request.sh"


def gnu_mv() -> bool:
    if not sys.platform.startswith("linux"):
        return False
    return subprocess.run(["mv", "--help"], capture_output=True, text=True).stdout.find("-T") >= 0


@unittest.skipUnless(gnu_mv(), "update-request.sh needs GNU mv -T (Linux)")
class UpdateRequestWrapper(unittest.TestCase):
    def setUp(self) -> None:
        self.tmp = tempfile.TemporaryDirectory()
        root = Path(self.tmp.name)
        # two levels under root, like /run/openastroara/update, so the wrapper's private
        # work dir lands in root (outside the exchange dir)
        self.dir = root / "openastroara" / "update"
        self.dir.mkdir(parents=True)
        self.seen = root / "seen-request"
        self.helper = root / "helper.sh"
        # Records whether the request file still existed while it ran (the daemon's
        # "pending" signal), then prints its argv and the status lines.
        self.helper.write_text(
            "#!/bin/sh\n"
            f'[ -f "{self.dir}/$ID.request" ] && echo present > "{self.seen}"\n'
            'printf "argc=%s" "$#"; for a; do printf " [%s]" "$a"; done; printf "\\n"\n'
            'echo "status=$STATUS"\n'
            'exit "$CODE"\n')
        self.helper.chmod(self.helper.stat().st_mode | stat.S_IXUSR)

    def tearDown(self) -> None:
        self.tmp.cleanup()

    def run_wrapper(self, rid: str, status: str = "applied", code: int = 0) -> subprocess.CompletedProcess[str]:
        env = dict(os.environ, DIR=str(self.dir), HELPER=str(self.helper), ID=rid, STATUS=status, CODE=str(code))
        return subprocess.run(["sh", str(WRAPPER), rid], env=env, capture_output=True, text=True, timeout=30)

    def test_round_trip_passes_two_args_keeps_the_request_while_running_and_writes_the_result(self) -> None:
        (self.dir / "abc123.request").write_text("/var/lib/openastroara/updates/abc123.deb\n5555\n")
        r = self.run_wrapper("abc123", status="rolled_back", code=3)
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertEqual(self.seen.read_text().strip(), "present",
                         "the request must exist while the helper runs, or the daemon reads 404 instead of pending")
        result = (self.dir / "abc123.result").read_text().splitlines()
        self.assertEqual(result[0], "3", "line 1 is the helper's exit code")
        self.assertIn("argc=2 [/var/lib/openastroara/updates/abc123.deb] [5555]", result)
        self.assertIn("status=rolled_back", result)
        self.assertFalse((self.dir / "abc123.request").exists(), "removed once the result is in place")
        self.assertEqual(sorted(p.name for p in self.dir.iterdir()), ["abc123.result"], "no temp files left in the exchange dir")

    def test_bad_ids_symlinks_and_oversized_requests_are_refused_without_running_the_helper(self) -> None:
        (self.dir / "big.request").write_text("a\nb\nc\n")
        target = Path(self.tmp.name) / "elsewhere"
        target.write_text("x\n")
        (self.dir / "link.request").symlink_to(target)
        for rid in ["../x", "a b", "", "link", "big", "missing"]:
            with self.subTest(rid):
                r = self.run_wrapper(rid)
                self.assertEqual(r.returncode, 9, r.stderr)
                self.assertFalse(self.seen.exists(), "helper never ran")
                self.assertFalse(any(p.suffix == ".result" for p in self.dir.iterdir()))

    def test_a_planted_symlink_at_the_result_name_is_replaced_not_followed(self) -> None:
        victim = Path(self.tmp.name) / "victim"
        victim.write_text("untouched\n")
        (self.dir / "r1.result").symlink_to(victim)
        (self.dir / "r1.request").write_text("/x.deb\n5555\n")
        self.run_wrapper("r1")
        self.assertEqual(victim.read_text(), "untouched\n")
        self.assertFalse((self.dir / "r1.result").is_symlink())


if __name__ == "__main__":
    unittest.main()
