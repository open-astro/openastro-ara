#!/usr/bin/env python3
"""Tests for scripts/check-issue-labels.py (#2093): one comment while an issue lacks a label group.

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
SCRIPT = REPO_ROOT / "scripts" / "check-issue-labels.py"


def load():
    spec = importlib.util.spec_from_file_location("check_issue_labels", SCRIPT)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


class FakeIssues:
    def __init__(self):
        self.calls = []

    def create_comment(self, number, text):
        self.calls.append(("create", number))

    def update_comment(self, comment_id, text):
        self.calls.append(("update", comment_id))

    def delete_comment(self, comment_id):
        self.calls.append(("delete", comment_id))


class CheckIssueLabelsTest(unittest.TestCase):
    def setUp(self):
        self.m = load()

    def test_p6_is_a_priority(self):
        self.assertEqual(self.m.missing_groups(["enhancement", "P6", "L"]), [])

    def test_triage_labels_are_no_type_priority_or_size(self):
        self.assertEqual(self.m.missing_groups(["ready-for-agent", "needs-triage"]), ["type", "priority", "size"])

    def test_a_missing_group_gets_one_comment_then_it_goes(self):
        fake = FakeIssues()
        self.m.reconcile(7, {"state": "OPEN", "labels": ["bug", "P2"]}, [], "alice", "User", fake)
        self.assertEqual(fake.calls, [("create", 7)])
        own = {"id": 3, "user": {"login": self.m.BOT_LOGIN}, "body": self.m.comment_body(["size"])}
        fake.calls.clear()
        self.m.reconcile(7, {"state": "OPEN", "labels": ["bug", "P2", "S"]}, [own], "alice", "User", fake)
        self.assertEqual(fake.calls, [("delete", 3)])

    def test_a_dependabot_issue_is_exempt(self):
        fake = FakeIssues()
        self.m.reconcile(8, {"state": "OPEN", "labels": ["dependencies"]}, [], "alice", "User", fake)
        self.m.reconcile(9, {"state": "OPEN", "labels": []}, [], "dependabot[bot]", "Bot", fake)
        self.assertEqual(fake.calls, [])

    def test_self_test_passes(self):
        r = subprocess.run([sys.executable, str(SCRIPT), "--self-test"], capture_output=True, text=True)
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)


if __name__ == "__main__":
    unittest.main()
