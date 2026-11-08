#!/usr/bin/env python3
"""Tests for scripts/check-pr-template.py.

Run from the repo root:

    python3 -m unittest discover -s scripts/tests -v

Each case runs the script as CI does (subprocess, body in a file or in
PR_BODY) and asserts the exit code and the printed lines.
"""

from __future__ import annotations

import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent.parent
SCRIPT = REPO_ROOT / "scripts" / "check-pr-template.py"
REAL_TEMPLATE = REPO_ROOT / ".github" / "pull_request_template.md"

SMALL_TEMPLATE = (
    "<!-- top note -->\n## Thinking Path\n<!-- say why -->\n> - Project line\n> - [placeholder]\n\n"
    "## What Changed\n\n-\n\n## Checklist\n\n- [ ] item one\n- [ ] item two\n"
)
GOOD = (
    "## Thinking Path\n> - Project line\n> - The real reason\n\n"
    "## What Changed\n- `a.py`: did a thing\n\n## Checklist\n- [x] item one\n- [ ] item two\n"
)


class CheckPrTemplateTest(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.dir = Path(self.tmp.name)

    def run_check(self, body: str, template: str | Path | None = None):
        body_file = self.dir / "body.md"
        body_file.write_text(body, encoding="utf-8", newline="")
        cmd = [sys.executable, str(SCRIPT), "--body-file", str(body_file)]
        if template is not None:
            if not isinstance(template, Path):
                tpl = self.dir / "template.md"
                tpl.write_text(template, encoding="utf-8")
                template = tpl
            cmd += ["--template", str(template)]
        return subprocess.run(cmd, capture_output=True, text=True)

    def test_filled_body_passes(self):
        r = self.run_check(GOOD, SMALL_TEMPLATE)
        self.assertEqual(r.returncode, 0, r.stdout)
        self.assertIn("PR body follows the template.", r.stdout)

    def test_crlf_and_heading_case_are_ignored(self):
        body = GOOD.replace("## What Changed", "##  what changed ").replace("\n", "\r\n")
        self.assertEqual(self.run_check(body, SMALL_TEMPLATE).returncode, 0)

    def test_unfilled_template_fails_with_two_problems(self):
        r = self.run_check(SMALL_TEMPLATE, SMALL_TEMPLATE)
        self.assertEqual(r.returncode, 1)
        self.assertIn("(2 problem(s))", r.stdout)

    def test_empty_body_fails(self):
        r = self.run_check("", SMALL_TEMPLATE)
        self.assertEqual(r.returncode, 1)
        self.assertIn("the PR body is empty", r.stdout)

    def test_section_with_only_template_lines_fails(self):
        r = self.run_check(GOOD.replace("> - The real reason\n", ""), SMALL_TEMPLATE)
        self.assertEqual(r.returncode, 1)
        self.assertIn("section '## Thinking Path' is empty", r.stdout)

    def test_bare_marker_or_comment_only_section_fails(self):
        for filler in ("-", "<!-- x -->"):
            with self.subTest(filler=filler):
                r = self.run_check(GOOD.replace("- `a.py`: did a thing", filler), SMALL_TEMPLATE)
                self.assertEqual(r.returncode, 1)
                self.assertIn("section '## What Changed' is empty", r.stdout)

    def test_missing_section_fails(self):
        r = self.run_check(GOOD.replace("## What Changed", "## Summary"), SMALL_TEMPLATE)
        self.assertEqual(r.returncode, 1)
        self.assertIn("missing section '## What Changed'", r.stdout)

    def test_checklist_without_checkboxes_fails(self):
        r = self.run_check(GOOD.replace("- [x] item one\n- [ ] item two\n", "done\n"), SMALL_TEMPLATE)
        self.assertEqual(r.returncode, 1)
        self.assertIn("section '## Checklist' has no '- [ ]' or '- [x]' items", r.stdout)

    def test_heading_inside_a_fence_does_not_count(self):
        body = GOOD.replace("## What Changed\n- `a.py`: did a thing\n", "## Other\n```\n## What Changed\n```\n")
        r = self.run_check(body, SMALL_TEMPLATE)
        self.assertEqual(r.returncode, 1)
        self.assertIn("missing section '## What Changed'", r.stdout)

    def test_a_or_b_heading_accepts_either_path(self):
        tpl = "## Linked Issues or Issue Description\n\n-\n"
        for heading in ("Linked Issues or Issue Description", "Issue Description", "Linked Issues"):
            with self.subTest(heading=heading):
                self.assertEqual(self.run_check("## %s\nCloses #1\n" % heading, tpl).returncode, 0)
        self.assertEqual(self.run_check("## Issues\nCloses #1\n", tpl).returncode, 1)

    def test_body_from_environment(self):
        env = dict(os.environ, PR_BODY=GOOD)
        tpl = self.dir / "t.md"
        tpl.write_text(SMALL_TEMPLATE, encoding="utf-8")
        r = subprocess.run([sys.executable, str(SCRIPT), "--template", str(tpl)], capture_output=True, text=True, env=env)
        self.assertEqual(r.returncode, 0, r.stdout)

    def test_unknown_argument_is_a_usage_error(self):
        r = subprocess.run([sys.executable, str(SCRIPT), "--bogus"], capture_output=True, text=True)
        self.assertEqual(r.returncode, 2)

    def test_real_template_unfilled_fails(self):
        template = REAL_TEMPLATE.read_text(encoding="utf-8")
        r = self.run_check(template)
        self.assertEqual(r.returncode, 1)
        for heading in ("Thinking Path", "Decision records", "Verification", "Model Used"):
            self.assertIn("'## %s'" % heading, r.stdout)

    def test_old_summary_style_body_fails_the_real_template(self):
        body = "Closes #1.\n\n## Summary\n- x\n\n## Test plan\n- y\n"
        r = self.run_check(body)
        self.assertEqual(r.returncode, 1)
        self.assertIn("missing section '## Thinking Path'", r.stdout)

    def test_real_template_keeps_the_registry_checkboxes(self):
        template = REAL_TEMPLATE.read_text(encoding="utf-8")
        self.assertIn("Settings registry (section 61.4)", template)
        self.assertIn("Help registry (section 69.4)", template)
        self.assertNotIn("changelog.d", template)


if __name__ == "__main__":
    unittest.main()
