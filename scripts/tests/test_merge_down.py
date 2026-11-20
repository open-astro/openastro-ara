#!/usr/bin/env python3
"""Tests for scripts/merge-down.py (#2093): which PRs are merge downs exempt from falsified-by.

Run from the repo root:

    python3 -m unittest discover -s scripts/tests -v

Each case builds a throwaway repository with master, stable/1.0 and a
merge-down head, and runs the script by path inside it as CI does.
"""

from __future__ import annotations

import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent.parent
SCRIPT = REPO_ROOT / "scripts" / "merge-down.py"

GIT_ENV = {
    "GIT_CONFIG_GLOBAL": os.devnull,
    "GIT_CONFIG_NOSYSTEM": "1",
    "GIT_AUTHOR_NAME": "t", "GIT_AUTHOR_EMAIL": "t@t",
    "GIT_COMMITTER_NAME": "t", "GIT_COMMITTER_EMAIL": "t@t",
}


class MergeDownTest(unittest.TestCase):
    def setUp(self):
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        self.repo = Path(tmp.name)
        self.env = {k: v for k, v in os.environ.items() if not k.startswith("GIT_")}
        self.env.update(GIT_ENV)
        self.git("init", "-q", "-b", "master")
        self.commit("a")
        self.git("branch", "stable/1.0")
        self.commit("master-only")
        self.git("checkout", "-q", "stable/1.0")
        self.commit("fix")
        self.git("remote", "add", "origin", str(self.repo))
        self.git("update-ref", "refs/remotes/origin/stable/1.0", "stable/1.0")
        self.git("checkout", "-q", "-b", "merge-down/1.0-to-master")
        self.git("merge", "-q", "--no-edit", "master")
        self.base = self.git("rev-parse", "master")

    def git(self, *args):
        r = subprocess.run(["git", "-C", str(self.repo), "-c", "commit.gpgsign=false", *args],
                           capture_output=True, text=True, env=self.env)
        self.assertEqual(r.returncode, 0, r.stderr)
        return r.stdout.strip()

    def commit(self, name, rel=None):
        path = self.repo / (rel or name)
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(name + "\n")
        self.git("add", str(path))
        self.git("commit", "-q", "-m", name)

    def exempt(self, head="merge-down/1.0-to-master", base_ref="master", same_repo="true"):
        return subprocess.run(
            [sys.executable, str(SCRIPT), "--exempt", "--head-ref", head, "--base-ref", base_ref,
             "--base-rev", self.base, "--same-repo", same_repo],
            capture_output=True, text=True, cwd=self.repo, env=self.env,
        )

    def test_a_merge_down_of_stable_commits_is_exempt(self):
        r = self.exempt()
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        self.assertIn("exempt: merge down of stable/1.0 into master", r.stdout)

    def test_a_commit_not_on_the_stable_branch_is_not_exempt(self):
        self.commit("extra")
        r = self.exempt()
        self.assertEqual(r.returncode, 1)
        self.assertIn("not on origin/stable/1.0", r.stdout)

    def test_a_test_case_written_into_the_merge_is_not_exempt(self):
        self.git("checkout", "-q", "master")
        self.commit("master-later")
        self.base = self.git("rev-parse", "master")
        self.git("checkout", "-q", "merge-down/1.0-to-master")
        self.git("merge", "-q", "--no-commit", "master")
        path = self.repo / "client/openastroara_client/test/smuggled_test.dart"
        path.parent.mkdir(parents=True)
        path.write_text("void main() { test('smuggled', () {}); }\n")
        self.git("add", str(path))
        self.git("commit", "-q", "-m", "merge master")
        r = self.exempt()
        self.assertEqual(r.returncode, 1)
        self.assertIn("changes test files beyond the automatic merge: client/openastroara_client/test/smuggled_test.dart",
                      r.stdout)

    def test_a_hand_resolved_merge_already_on_the_stable_branch_keeps_the_exemption(self):
        # A fix PR on stable/1.0 merged stable in and fixed a test file by hand;
        # that merge went through the gate on its own PR, so the merge down
        # carrying it to master is still exempt.
        self.git("checkout", "-q", "-b", "fix-2", "stable/1.0")
        self.commit("fix-2")
        self.git("checkout", "-q", "stable/1.0")
        self.commit("stable-later")
        self.git("checkout", "-q", "fix-2")
        self.git("merge", "-q", "--no-commit", "stable/1.0")
        self.commit("hand-resolved", "client/openastroara_client/test/fix_2_test.dart")
        self.git("checkout", "-q", "stable/1.0")
        self.git("merge", "-q", "--no-ff", "--no-edit", "fix-2")
        self.git("update-ref", "refs/remotes/origin/stable/1.0", "stable/1.0")
        self.git("checkout", "-q", "merge-down/1.0-to-master")
        self.git("merge", "-q", "--no-edit", "stable/1.0")
        r = self.exempt()
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        self.assertIn("exempt: merge down of stable/1.0 into master", r.stdout)

    def test_the_main_spelling_and_a_fork_head_are_not_exempt(self):
        self.assertEqual(self.exempt(head="merge-down/1.0-to-main").returncode, 1)
        self.assertEqual(self.exempt(same_repo="false").returncode, 1)
        self.assertEqual(self.exempt(base_ref="stable/1.1").returncode, 1)

    def test_self_test_passes(self):
        r = subprocess.run([sys.executable, str(SCRIPT), "--self-test"], capture_output=True, text=True)
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)


if __name__ == "__main__":
    unittest.main()
