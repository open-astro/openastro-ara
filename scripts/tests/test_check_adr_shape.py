#!/usr/bin/env python3
"""Tests for scripts/check-adr-shape.py (and the grammar it shares, adr_grammar.py).

Run from the repo root:

    python3 -m unittest discover -s scripts/tests -v

Each case builds a throwaway git repository holding docs/adr/ records and the
files their Scope globs name, then runs the script by path and asserts the
exit code and the printed `adr-shape:` lines.
"""

from __future__ import annotations

import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent.parent
SCRIPT = REPO_ROOT / "scripts" / "check-adr-shape.py"

RECORD = """# Title

Status: accepted

## Context

Why.

## Decision

What.

## Rules

1. The cache never drops a row. Scope: `lib/cache/*.dart`. Checked by: the cache test "never drops".
2. A fetch ignores the total. Scope: `lib/**/*.dart`, `lib/other.dart`. Checked by: review (a fetch whose condition reads total).

## Alternatives rejected

- None.

## Consequences

- Some.

## How to reverse

Drop it.

## Links

- none
"""
README = "# ADRs\n\n- [Title](0001-title.md)\n"


class AdrShapeTest(unittest.TestCase):
    def setUp(self):
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        self.root = Path(tmp.name)
        self.env = {k: v for k, v in os.environ.items() if not k.startswith("GIT_")}
        subprocess.run(["git", "init", "-q", str(self.root)], check=True, env=self.env)
        for f in ("lib/cache/a.dart", "lib/other.dart"):
            self.write(f, "x\n")
        self.write("docs/adr/0001-title.md", RECORD)
        self.write("docs/adr/README.md", README)

    def write(self, rel, text):
        p = self.root / rel
        p.parent.mkdir(parents=True, exist_ok=True)
        p.write_text(text, encoding="utf-8")

    def run_check(self):
        subprocess.run(["git", "-C", str(self.root), "add", "-A"], check=True, env=self.env)
        return subprocess.run([sys.executable, str(SCRIPT), "--root", str(self.root)], capture_output=True, text=True)

    def record(self, old, new):
        self.assertIn(old, RECORD)
        self.write("docs/adr/0001-title.md", RECORD.replace(old, new))

    def assert_fails(self, fragment):
        r = self.run_check()
        self.assertEqual(r.returncode, 1, r.stdout)
        self.assertIn(fragment, r.stdout)

    def test_valid_record_passes(self):
        r = self.run_check()
        self.assertEqual(r.returncode, 0, r.stdout)
        self.assertIn("adr-shape: OK (1 record(s))", r.stdout)

    def test_the_real_tree_passes(self):
        r = subprocess.run([sys.executable, str(SCRIPT)], capture_output=True, text=True, cwd=REPO_ROOT)
        self.assertEqual(r.returncode, 0, r.stdout)

    def test_wrong_status_fails(self):
        self.record("Status: accepted", "Status: draft")
        self.assert_fails("line 3 is not `Status: proposed`")

    def test_superseded_and_retired_need_no_rules(self):
        two = RECORD.replace("Status: accepted", "Status: superseded by 0002")
        no_rules = two[:two.index("## Rules")] + two[two.index("## Alternatives rejected"):]
        self.write("docs/adr/0001-title.md", no_rules)
        self.write("docs/adr/0002-next.md", RECORD)
        self.write("docs/adr/README.md", README + "- [Next](0002-next.md)\n")
        r = self.run_check()
        self.assertEqual(r.returncode, 0, r.stdout)

    def test_superseded_by_a_missing_record_fails(self):
        self.record("Status: accepted", "Status: superseded by 0009")
        self.assert_fails("superseded by 0009, which does not exist")

    def test_section_out_of_order_fails(self):
        swapped = RECORD.replace("## Context\n\nWhy.\n\n## Decision\n\nWhat.\n", "## Decision\n\nWhat.\n\n## Context\n\nWhy.\n")
        self.write("docs/adr/0001-title.md", swapped)
        self.assert_fails("sections are out of order")

    def test_unknown_section_fails(self):
        self.record("## Links", "## Notes\n\nx\n\n## Links")
        self.assert_fails("unknown section `## Notes`")

    def test_missing_section_fails(self):
        self.record("## Consequences\n\n- Some.\n\n", "")
        self.assert_fails("missing section `## Consequences`")

    def test_missing_how_to_reverse_fails(self):
        self.record("## How to reverse\n\nDrop it.\n\n", "")
        self.assert_fails("missing section `## How to reverse`")

    def test_bad_rule_line_fails(self):
        self.record("Checked by: the cache test \"never drops\".", "Checked by")
        self.assert_fails("rule line does not end")

    def test_glob_matching_no_file_fails(self):
        self.record("`lib/cache/*.dart`", "`lib/gone/*.dart`")
        self.assert_fails("scope `lib/gone/*.dart` matches no tracked file")

    def test_single_star_does_not_cross_a_directory(self):
        self.record("`lib/cache/*.dart`", "`lib/*.dart`")
        r = self.run_check()
        self.assertEqual(r.returncode, 0, r.stdout)  # lib/other.dart matches
        self.record("`lib/cache/*.dart`", "`lib/*/a.dart`")
        self.assertEqual(self.run_check().returncode, 0)
        self.record("`lib/cache/*.dart`", "`lib/*/*/a.dart`")
        self.assert_fails("matches no tracked file")

    def test_double_star_alone_fails(self):
        self.record("`lib/cache/*.dart`", "`**`")
        self.assert_fails("scope `**` is not allowed")

    def test_bare_review_fails(self):
        self.record("review (a fetch whose condition reads total)", "review")
        self.assert_fails("bare `Checked by: review`")

    def test_exception_word_needs_the_allowed_marker(self):
        self.record("A fetch ignores the total.", "A fetch ignores the total unless it is zero.")
        self.assert_fails("rule 2 says `unless`")
        self.record("A fetch ignores the total.", "A fetch ignores the total unless it is zero.")
        text = (self.root / "docs/adr/0001-title.md").read_text()
        self.write("docs/adr/0001-title.md", text.replace("total).\n", "total). <!-- allowed -->\n"))
        r = self.run_check()
        self.assertEqual(r.returncode, 0, r.stdout)

    def test_rule_out_of_sequence_fails(self):
        self.record("2. A fetch", "3. A fetch")
        self.assert_fails("rule 3 is out of sequence (expected 2)")

    def test_index_missing_a_record_fails(self):
        self.write("docs/adr/README.md", "# ADRs\n")
        self.assert_fails("not indexed: 0001-title.md")

    def test_index_naming_an_absent_record_fails(self):
        self.write("docs/adr/README.md", README + "- [Gone](0002-gone.md)\n")
        self.assert_fails("indexed but absent: 0002-gone.md")

    def test_bad_file_name_fails(self):
        self.write("docs/adr/1-bad.md", RECORD)
        self.assert_fails("the name is not NNNN-<lowercase-slug>.md")


if __name__ == "__main__":
    unittest.main()
