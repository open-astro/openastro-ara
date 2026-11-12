#!/usr/bin/env python3
"""The auto-queue step (scripts/auto-queue.sh) against a fake gh.

Run from the repo root:

    python3 -I -m unittest discover -s scripts/tests -v

After each review run the step finds the open PR whose head is the run's commit
and reads the bot's verdict written during that run. "✅ Approved" enables
auto-merge, which hands the PR to the merge queue; "⚠️ Issues found" disables it
and takes the PR out of the queue. These tests put a gh stub first on PATH that
answers reads from fixtures and records every call, and check which calls the
step made. The real jq does the selecting.
"""

from __future__ import annotations

import json
import os
import stat
import subprocess
import tempfile
import unittest
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent.parent
STEP = REPO_ROOT / "scripts" / "auto-queue.sh"
SHA = "a" * 40
RUN_START = "2026-11-12T10:00:00Z"

# Records each call in $FAKE_GH/calls, one per line, and answers reads from files there.
GH_STUB = r"""#!/usr/bin/env python3
import os, sys
d = os.environ["FAKE_GH"]
a = sys.argv[1:]
with open(os.path.join(d, "calls"), "a", encoding="utf-8") as f:
    f.write(" ".join(" ".join(x.split()) for x in a) + "\n")
if a[:2] == ["pr", "list"]:
    name = "prs.json"
elif a[:2] == ["pr", "merge"]:
    sys.exit(0)
elif a[:2] == ["api", "graphql"]:
    if any("mutation" in x for x in a):
        sys.exit(0)
    name = "state.json"
elif a[0] == "api" and "/comments" in a[-1]:
    name = "comments.json"
else:
    sys.exit("unexpected gh call: " + " ".join(a))
sys.stdout.write(open(os.path.join(d, name), encoding="utf-8").read())
"""


def comment(body, at="2026-11-12T10:30:00Z", login="github-actions[bot]"):
    return {"user": {"login": login}, "updated_at": at, "body": body}


APPROVED = comment("## Defects\n\nNone.\n\n## Notes\n\n- a note\n\n✅ Approved\n")
ISSUES = comment("## Defects\n\n- a defect\n\n⚠️ Issues found\n")


class AutoQueueTest(unittest.TestCase):
    def step(self, comments=(), prs=None, head=SHA, draft=False, queued=False, auto=False):
        if prs is None:
            prs = [{"number": 6, "headRefOid": "b" * 40}, {"number": 7, "headRefOid": SHA}]
        state = {"id": "PR_node7", "headRefOid": head, "isDraft": draft, "isInMergeQueue": queued,
                 "autoMergeRequest": {"enabledAt": RUN_START} if auto else None}
        with tempfile.TemporaryDirectory() as d:
            root = Path(d)
            stub = root / "bin" / "gh"
            stub.parent.mkdir()
            stub.write_text(GH_STUB, encoding="utf-8")
            stub.chmod(stub.stat().st_mode | stat.S_IEXEC)
            files = {
                "prs.json": json.dumps(prs),
                "state.json": json.dumps(state),
                "comments.json": json.dumps(list(comments)),
                "calls": "",
            }
            for name, text in files.items():
                (root / name).write_text(text, encoding="utf-8")
            env = dict(os.environ, FAKE_GH=d, PATH=f"{stub.parent}{os.pathsep}{os.environ['PATH']}")
            p = subprocess.run(["bash", str(STEP), "open-astro/x", SHA, RUN_START], env=env,
                               capture_output=True, text=True, timeout=60)
            calls = (root / "calls").read_text(encoding="utf-8").splitlines()
        self.assertEqual(p.returncode, 0, p.stdout + p.stderr)
        # Only the writes matter: reads are free.
        return [c for c in calls if c.startswith("pr merge") or "mutation" in c]

    def test_approved_on_the_head_enables_auto_merge(self):
        self.assertEqual(self.step([APPROVED]), ["pr merge 7 --repo open-astro/x --auto"])

    def test_approved_on_a_pr_already_queued_changes_nothing(self):
        self.assertEqual(self.step([APPROVED], auto=True), [])
        self.assertEqual(self.step([APPROVED], queued=True), [])

    def test_issues_found_disables_auto_merge_and_dequeues(self):
        writes = self.step([ISSUES], queued=True, auto=True)
        self.assertEqual(writes[0], "pr merge 7 --repo open-astro/x --disable-auto")
        self.assertEqual(len(writes), 2, writes)
        self.assertIn("dequeuePullRequest", writes[1])
        self.assertIn("PR_node7", writes[1])
        # Nothing to undo on a PR that was never queued.
        self.assertEqual(self.step([ISSUES]), [])

    def test_the_newest_verdict_decides(self):
        later = comment(APPROVED["body"], at="2026-11-12T10:45:00Z")
        self.assertEqual(self.step([ISSUES, later]), ["pr merge 7 --repo open-astro/x --auto"])

    def test_a_verdict_older_than_the_run_does_nothing(self):
        stale = comment(APPROVED["body"], at="2026-11-12T09:59:59Z")
        self.assertEqual(self.step([stale]), [])
        stale_issues = comment(ISSUES["body"], at="2026-11-12T09:59:59Z")
        self.assertEqual(self.step([stale_issues], queued=True, auto=True), [])

    def test_no_pr_for_the_commit_or_a_moved_head_does_nothing(self):
        self.assertEqual(self.step([APPROVED], prs=[{"number": 6, "headRefOid": "b" * 40}]), [])
        self.assertEqual(self.step([APPROVED], head="c" * 40), [])

    def test_only_the_bots_sign_off_line_is_a_verdict(self):
        self.assertEqual(self.step([comment("✅ Approved", login="someone")]), [])
        quoted = comment("The review will sign off with ✅ Approved or ⚠️ Issues found when it ends.")
        self.assertEqual(self.step([quoted]), [])
        self.assertEqual(self.step([comment("✅ Approved", login="claude[bot]")]),
                         ["pr merge 7 --repo open-astro/x --auto"])

    def test_no_verdict_or_a_draft_does_nothing(self):
        # A skipped review (the pull_request run of a fork PR) posts no comment.
        self.assertEqual(self.step([]), [])
        self.assertEqual(self.step([APPROVED], draft=True), [])


if __name__ == "__main__":
    unittest.main()
