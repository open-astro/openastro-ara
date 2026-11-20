#!/usr/bin/env python3
"""Tests for scripts/nightly-version.py (#2093): the .deb version of an untagged CI build.

Run from the repo root:

    python3 -m unittest discover -s scripts/tests -v
"""

from __future__ import annotations

import subprocess
import sys
import unittest
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent.parent
SCRIPT = REPO_ROOT / "scripts" / "nightly-version.py"


def run(refs: str, *args: str) -> subprocess.CompletedProcess:
    return subprocess.run(
        [sys.executable, str(SCRIPT), "--sha", "abc1234def5678", "--stamp", "202611190317", "--refs-from-stdin", *args],
        capture_output=True, text=True, input=refs,
    )


class NightlyVersionTest(unittest.TestCase):
    def assertNightly(self, refs: str, want: str, *args: str):
        r = run(refs, "--current", "0.0.1", *args)  # a stable VERSION, whatever the checkout holds
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertEqual(r.stdout, want + "~~nightly202611190317.gabc1234\n")

    def test_no_stable_line_keeps_the_bump(self):
        self.assertNightly("refs/heads/master\n", "0.0.2", "--bump", "0.0.2", "--branch", "master")

    def test_master_after_the_cut_is_past_the_stable_line(self):
        self.assertNightly("refs/remotes/origin/stable/1.0\nrefs/tags/v1.0.0-beta.1\n", "1.1.0",
                           "--bump", "1.0.0", "--branch", "master")

    def test_a_breaking_bump_beats_the_line(self):
        self.assertNightly("refs/heads/stable/1.0\n", "2.0.0", "--bump", "2.0.0", "--branch", "master")

    def test_a_stable_branch_stays_on_its_own_line(self):
        self.assertNightly("refs/heads/stable/1.0\nrefs/tags/v1.0.0\nrefs/heads/stable/1.1\n", "1.0.1",
                           "--bump", "1.0.1", "--branch", "stable/1.0")

    def test_a_beta_version_names_the_build_after_that_beta(self):
        r = run("refs/heads/stable/1.0\nrefs/tags/v1.0.0-beta.2\n", "--bump", "1.0.0", "--current", "1.0.0~beta2",
                "--branch", "stable/1.0")
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertEqual(r.stdout, "1.0.0~beta2+nightly202611190317.gabc1234\n")

    def test_a_beta_bump_is_refused(self):
        r = run("", "--bump", "1.0.0~beta1", "--branch", "master")
        self.assertEqual(r.returncode, 1)
        self.assertIn("is not X.Y.Z", r.stderr)

    def test_the_default_bump_reads_the_fragments(self):
        r = subprocess.run([sys.executable, str(SCRIPT), "--sha", "abc1234", "--stamp", "202611190317",
                            "--refs-from-stdin", "--branch", "master"], capture_output=True, text=True, input="")
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertRegex(r.stdout, r"^\d+\.\d+\.\d+(?:~~|~beta[1-9]\d*\+)nightly202611190317\.gabc1234\n$")

    def test_self_test_passes(self):
        r = subprocess.run([sys.executable, str(SCRIPT), "--self-test"], capture_output=True, text=True)
        self.assertEqual(r.returncode, 0, r.stderr)


if __name__ == "__main__":
    unittest.main()
