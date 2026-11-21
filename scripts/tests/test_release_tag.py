#!/usr/bin/env python3
"""Tests for scripts/release-tag.py (#2093): a release tag mapped to VERSION.

Run from the repo root:

    python3 -m unittest discover -s scripts/tests -v
"""

from __future__ import annotations

import subprocess
import sys
import unittest
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent.parent
SCRIPT = REPO_ROOT / "scripts" / "release-tag.py"


def run(*args: str) -> subprocess.CompletedProcess:
    return subprocess.run([sys.executable, str(SCRIPT), *args], capture_output=True, text=True)


class ReleaseTagTest(unittest.TestCase):
    def test_a_beta_tag_prints_every_field(self):
        r = run("v1.0.0-beta.1")
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertEqual(
            r.stdout,
            "version=1.0.0~beta1\nsemver=1.0.0-beta.1\nprerelease=true\nnotes_name=1.0.0-beta.1.md\nstable_branch=stable/1.0\n",
        )

    def test_a_stable_tag_is_no_prerelease(self):
        r = run("v1.2.3")
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertEqual(
            r.stdout, "version=1.2.3\nsemver=1.2.3\nprerelease=false\nnotes_name=1.2.3.md\nstable_branch=stable/1.2\n"
        )

    def test_a_malformed_tag_exits_two(self):
        for bad in ("1.0.0", "v1.0", "v1.0.0-beta.0", "v1.0.0-rc.1", "v0.0.1-ara.1", "v1.0.0~beta1", "v1.5.0.0-rc1"):
            r = run(bad)
            self.assertEqual(r.returncode, 2, bad)
            self.assertIn("is not vX.Y.Z or vX.Y.Z-beta.N", r.stderr)
            self.assertEqual(r.stdout, "")

    def test_self_test_reads_the_pinned_cases(self):
        r = run("--self-test")
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertIn("release-tag self-test OK", r.stdout)
        self.assertNotIn("(0 pinned case(s))", r.stdout)


if __name__ == "__main__":
    unittest.main()
