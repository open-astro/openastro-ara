#!/usr/bin/env python3
"""Tests for scripts/check-adr-scope.py.

Run from the repo root:

    python3 -m unittest discover -s scripts/tests -v

Each case copies the scripts into a throwaway git repository that holds
docs/adr/ records, then runs the script by path (stdin, files or a body file)
and asserts the exit code and the printed lines.
"""

from __future__ import annotations

import os
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent.parent
SCRIPTS = REPO_ROOT / "scripts"

RECORD_A = """# A

Status: accepted

## Context

c

## Decision

d

## Rules

1. First. Scope: `lib/a/*.dart`. Checked by: review (look).
2. Second. Scope: `lib/b/**/*.dart`, `docs/x.md`. Checked by: review (look).

## Alternatives rejected

- n

## Consequences

- n

## How to reverse

r

## Links

- n
"""
RECORD_B = RECORD_A.replace("# A", "# B").replace("1. First. Scope: `lib/a/*.dart`", "1. Only. Scope: `lib/c.dart`").replace(
    "2. Second. Scope: `lib/b/**/*.dart`, `docs/x.md`. Checked by: review (look).\n", "")
OK = "✅ Approved"
ISSUES = "⚠️ Issues found"
SCOPE = "0001-a.md: rules 3\n0002-b.md: rules 1, 2\n"
REVIEW = ("## Defects\nnone\n\n## Decision records\n"
          "- 0001-a.md rule 3: pass: no gate text changed\n"
          "- `0002-b.md` rule 1: n/a: nothing changed\n"
          "- 0002-b.md rule 2: n/a: planned\n\n" + OK + "\n")


class AdrScopeTest(unittest.TestCase):
    def setUp(self):
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        self.tmp = Path(tmp.name)
        self.root = self.tmp / "repo"
        self.env = {k: v for k, v in os.environ.items() if not k.startswith("GIT_")}
        self.env.update({"GIT_CONFIG_GLOBAL": os.devnull, "GIT_CONFIG_NOSYSTEM": "1",
                         "GIT_AUTHOR_NAME": "t", "GIT_AUTHOR_EMAIL": "t@t",
                         "GIT_COMMITTER_NAME": "t", "GIT_COMMITTER_EMAIL": "t@t"})
        self.root.mkdir()
        self.git("init", "-q", "-b", "master")
        for name in ("check-adr-scope.py", "check-falsified-by.py", "adr_grammar.py"):
            self.write("scripts/" + name, (SCRIPTS / name).read_text())
        self.write("docs/adr/0001-a.md", RECORD_A)
        self.write("docs/adr/0002-b.md", RECORD_B)
        self.write("docs/adr/README.md", "# ADRs\n")
        self.commit("base")
        self.git("checkout", "-q", "-b", "pr")
        self.script = self.root / "scripts" / "check-adr-scope.py"

    def git(self, *args):
        r = subprocess.run(["git", "-C", str(self.root), "-c", "commit.gpgsign=false", *args],
                           capture_output=True, text=True, env=self.env)
        self.assertEqual(r.returncode, 0, r.stderr)
        return r.stdout

    def write(self, rel, text):
        p = self.root / rel
        p.parent.mkdir(parents=True, exist_ok=True)
        p.write_text(text, encoding="utf-8")

    def commit(self, msg="c"):
        self.git("add", "-A")
        self.git("commit", "-q", "-m", msg)

    def run_script(self, *args, stdin=""):
        return subprocess.run([sys.executable, str(self.script), *args], input=stdin, capture_output=True,
                              text=True, env=self.env, cwd=self.root)

    def body_check(self, body):
        (self.tmp / "body.md").write_text(body, encoding="utf-8")
        return self.run_script("--body-file", str(self.tmp / "body.md"))

    def files(self, scope, review):
        (self.tmp / "scope.txt").write_text(scope, encoding="utf-8")
        (self.tmp / "review.md").write_text(review, encoding="utf-8")
        return str(self.tmp / "scope.txt"), str(self.tmp / "review.md")

    # --- --list ---------------------------------------------------------------

    def test_list_prints_the_rules_each_path_falls_under(self):
        r = self.run_script("--list", stdin="lib/a/x.dart\nlib/b/deep/y.dart\nlib/c.dart\nREADME.md\n")
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertEqual(r.stdout, "0001-a.md: rules 1, 2\n0002-b.md: rules 1\n")

    def test_list_reads_a_record_the_shape_check_reads_even_without_a_zero_prefix(self):
        self.write("docs/adr/1000-c.md", RECORD_B)
        out = self.run_script("--list", stdin="lib/c.dart\n").stdout
        self.assertIn("1000-c.md: rules 1", out)

    def test_list_single_star_does_not_cross_a_directory(self):
        r = self.run_script("--list", stdin="lib/a/sub/x.dart\n")
        self.assertEqual(r.stdout, "")

    def test_list_skips_superseded_and_retired_records(self):
        self.write("docs/adr/0001-a.md", RECORD_A.replace("Status: accepted", "Status: superseded by 0002"))
        self.write("docs/adr/0002-b.md", RECORD_B.replace("Status: accepted", "Status: retired (#5)"))
        self.assertEqual(self.run_script("--list", stdin="lib/a/x.dart\nlib/c.dart\n").stdout, "")

    def test_list_keeps_a_rule_marked_allowed(self):
        self.write("docs/adr/0001-a.md", RECORD_A.replace("Checked by: review (look).\n2.", "Checked by: review (look). <!-- allowed -->\n2."))
        self.assertEqual(self.run_script("--list", stdin="lib/a/x.dart\n").stdout, "0001-a.md: rules 1\n")

    # --- the PR-body gate -------------------------------------------------------

    def test_changed_record_must_be_named(self):
        self.write("docs/adr/0001-a.md", RECORD_A + "\n- more\n")
        self.commit("edit record")
        r = self.body_check("## Decision records\n\n- None\n")
        self.assertEqual(r.returncode, 1)
        self.assertIn("decision-records: the '## Decision records' section does not name docs/adr/0001-a.md", r.stdout)
        self.assertEqual(self.body_check("## Decision records\n\n- 0001: wording\n").returncode, 0)

    def test_none_passes_when_no_record_changed(self):
        self.write("lib/a/x.dart", "x\n")
        self.commit("code")
        r = self.body_check("## Decision records\n\nNone\n")
        self.assertEqual(r.returncode, 0, r.stdout)
        self.assertIn("in scope: 0001-a.md rules 1", r.stdout)
        self.assertIn("Decision records section names every record this PR changes.", r.stdout)

    def test_missing_section_fails(self):
        self.write("lib/a/x.dart", "x\n")
        self.commit("code")
        r = self.body_check("## What Changed\n- x\n")
        self.assertEqual(r.returncode, 1)
        self.assertIn("the PR body has no '## Decision records' section", r.stdout)

    def test_issue_number_is_not_a_record_number(self):
        self.write("docs/adr/0001-a.md", RECORD_A + "\n- more\n")
        self.commit("edit record")
        self.assertEqual(self.body_check("## Decision records\n\n- from the #0001 review\n").returncode, 1)
        self.assertEqual(self.body_check("## Decision records\n\n- 0001, from the #1415 review\n").returncode, 0)

    def test_record_named_only_in_a_comment_does_not_count(self):
        self.write("docs/adr/0001-a.md", RECORD_A + "\n- more\n")
        self.commit("edit record")
        self.assertEqual(self.body_check("## Decision records\n<!-- 0001 -->\nNone\n").returncode, 1)

    def test_deleted_and_renamed_records_count_as_changed(self):
        self.git("rm", "-q", "docs/adr/0002-b.md")
        self.git("mv", "docs/adr/0001-a.md", "docs/adr/0003-c.md")
        self.commit("delete and rename")
        r = self.body_check("## Decision records\n\nNone\n")
        self.assertEqual(r.returncode, 1)
        for n in ("0001-a.md", "0002-b.md", "0003-c.md"):
            self.assertIn(n, r.stdout)

    def test_the_index_is_not_a_record(self):
        self.write("docs/adr/README.md", "# ADRs\n\n- x\n")
        self.commit("index")
        self.assertEqual(self.body_check("## Decision records\n\nNone\n").returncode, 0)

    # --- --checklist -----------------------------------------------------------

    def checklist(self, scope, review):
        return self.run_script("--checklist", *self.files(scope, review))

    def test_full_checklist_passes(self):
        r = self.checklist(SCOPE, REVIEW)
        self.assertEqual(r.returncode, 0, r.stdout)

    def test_empty_scope_needs_no_lines(self):
        self.assertEqual(self.checklist("none\n", "## Defects\nnone\n\n" + OK).returncode, 0)
        self.assertEqual(self.checklist("none (the base branch has no decision records)\n", OK).returncode, 0)

    def test_missing_verdict_fails(self):
        r = self.checklist(SCOPE, REVIEW.replace("- 0002-b.md rule 2: n/a: planned\n", ""))
        self.assertEqual(r.returncode, 1)
        self.assertIn("adr-checklist: the review has no checklist line for 0002-b.md rule 2", r.stdout)

    def test_verdict_without_a_reason_does_not_count(self):
        r = self.checklist(SCOPE, REVIEW.replace("rule 2: n/a: planned", "rule 2: n/a"))
        self.assertEqual(r.returncode, 1)
        self.assertIn("rule 2", r.stdout)

    def test_two_verdicts_for_one_rule_fail(self):
        r = self.checklist(SCOPE, REVIEW + "- 0001-a.md rule 3: defect: x\n" + OK + "\n")
        self.assertEqual(r.returncode, 1)
        self.assertIn("more than one verdict", r.stdout)

    def test_defect_needs_the_issues_found_sign_off(self):
        defect = REVIEW.replace("rule 2: n/a", "rule 2: defect")
        r = self.checklist(SCOPE, defect)
        self.assertEqual(r.returncode, 1)
        self.assertIn("marks 0002-b.md rule 2 as a defect", r.stdout)
        self.assertEqual(self.checklist(SCOPE, defect.replace(OK, ISSUES)).returncode, 0)
        for decorated in ("**%s**", "%s.", "_%s_", "  %s  "):
            with self.subTest(decorated=decorated):
                self.assertEqual(self.checklist(SCOPE, defect.replace(OK, decorated % ISSUES)).returncode, 0)

    def test_last_line_that_only_contains_the_words_is_not_the_sign_off(self):
        defect = REVIEW.replace("rule 2: n/a", "rule 2: defect").replace(OK, ISSUES + ", but " + OK)
        self.assertEqual(self.checklist(SCOPE, defect).returncode, 1)

    def test_malformed_scope_line_fails(self):
        r = self.checklist(SCOPE + "rm -rf /\n", REVIEW)
        self.assertEqual(r.returncode, 1)
        self.assertIn("not `<record file>: rules <n>`", r.stdout)

    def test_prose_that_mentions_a_rule_is_not_a_checklist_line(self):
        r = self.checklist(SCOPE, "0002-b.md rule 2 passes, and so do the rest\n" + OK)
        self.assertEqual(r.returncode, 1)

    # --- --skeleton ------------------------------------------------------------

    def test_skeleton_prints_one_line_per_rule_in_order(self):
        (self.tmp / "scope.txt").write_text(SCOPE, encoding="utf-8")
        r = self.run_script("--skeleton", str(self.tmp / "scope.txt"))
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertEqual(r.stdout.splitlines(), ["- 0001-a.md rule 3: VERDICT: reason", "- 0002-b.md rule 1: VERDICT: reason",
                                                 "- 0002-b.md rule 2: VERDICT: reason"])

    def test_skeleton_of_an_empty_scope_is_empty(self):
        (self.tmp / "scope.txt").write_text("none (the base branch has no decision records)\n", encoding="utf-8")
        r = self.run_script("--skeleton", str(self.tmp / "scope.txt"))
        self.assertEqual((r.returncode, r.stdout.strip()), (0, ""))

    def test_skeleton_of_a_malformed_scope_fails(self):
        (self.tmp / "scope.txt").write_text(SCOPE + "rm -rf /\n", encoding="utf-8")
        r = self.run_script("--skeleton", str(self.tmp / "scope.txt"))
        self.assertEqual(r.returncode, 1)
        self.assertIn("adr-skeleton:", r.stderr)

    def test_unfilled_skeleton_is_not_a_verdict(self):
        (self.tmp / "scope.txt").write_text(SCOPE, encoding="utf-8")
        skeleton = self.run_script("--skeleton", str(self.tmp / "scope.txt")).stdout
        r = self.checklist(SCOPE, skeleton + OK + "\n")
        self.assertEqual(r.returncode, 1)


if __name__ == "__main__":
    unittest.main()
