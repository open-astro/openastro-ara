#!/usr/bin/env python3
"""Tests for scripts/close-linked-issues.py (#2093): issues closed by a PR merged into stable/**.

Run from the repo root:

    python3 -m unittest discover -s scripts/tests -v
"""

from __future__ import annotations

import importlib.util
import subprocess
import sys
import unittest
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent.parent
SCRIPT = REPO_ROOT / "scripts" / "close-linked-issues.py"
REPO = "open-astro/openastro-ara"


def load():
    spec = importlib.util.spec_from_file_location("close_linked_issues", SCRIPT)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


class CloseLinkedIssuesTest(unittest.TestCase):
    def setUp(self):
        self.m = load()

    def test_closing_keywords_name_issues_of_this_repository(self):
        body = "Fixes #12, resolves open-astro/openastro-ara#13 and closes https://github.com/open-astro/openastro-ara/issues/14"
        self.assertEqual(self.m.parse_closing_issues(body, REPO), [12, 13, 14])

    def test_references_and_other_repositories_are_ignored(self):
        body = "Refs #1, see #2, closes open-astro/AlpacaBridge#3, fixes https://github.com/open-astro/openastro-ara/pull/4"
        self.assertEqual(self.m.parse_closing_issues(body, REPO), [])

    def test_a_dry_run_with_no_keyword_calls_nothing(self):
        r = subprocess.run([sys.executable, str(SCRIPT), "--repo", REPO, "--pr", "5", "--base", "stable/1.0", "--dry-run"],
                           capture_output=True, text=True, env={"PR_BODY": "Refs #2093", "PATH": "/usr/bin:/bin"})
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertIn("nothing to close", r.stdout)

    def test_self_test_passes(self):
        r = subprocess.run([sys.executable, str(SCRIPT), "--self-test"], capture_output=True, text=True)
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)



class CloseLinkedIssuesWorkflowTest(unittest.TestCase):
    WORKFLOW = REPO_ROOT / ".github" / "workflows" / "close-linked-issues.yml"

    def test_the_job_runs_the_script_from_the_merged_stable_commit(self):
        # master has no scripts/close-linked-issues.py until the first merge down,
        # so a checkout of the default branch could not run it.
        text = self.WORKFLOW.read_text(encoding="utf-8")
        self.assertIn("ref: ${{ github.event.pull_request.merge_commit_sha }}", text)
        self.assertNotIn("default_branch", text)
        self.assertIn("if: github.event.pull_request.merged == true", text)


if __name__ == "__main__":
    unittest.main()
