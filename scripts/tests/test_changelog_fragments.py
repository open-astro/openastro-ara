#!/usr/bin/env python3
"""Tests for scripts/changelog-fragments.py (#1812).

Run from the repo root:

    python3 -m unittest discover -s scripts/tests -v

Each case builds a throwaway tree holding a CHANGELOG.md and a changelog.d/,
runs the script by path against it, and asserts the exit code, the printed
lines and the files left behind.
"""

from __future__ import annotations

import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent.parent
SCRIPT = REPO_ROOT / "scripts" / "changelog-fragments.py"

GOOD = "### Fixed\n- **x** (issue #1)\n\n### Added (tests)\n- **y**\n  - more\n"


class Tree:
    def __init__(self, root: Path):
        self.root = root
        self.fragments = root / "changelog.d"
        self.fragments.mkdir()
        (self.fragments / "README.md").write_text("# not a fragment\n")
        self.changelog = root / "CHANGELOG.md"
        self.changelog.write_text("# Changelog\n")

    def fragment(self, name: str, body: str) -> Path:
        p = self.fragments / name
        p.write_text(body)
        return p

    def run(self, *args: str) -> subprocess.CompletedProcess:
        return subprocess.run(
            [sys.executable, str(SCRIPT), "--changelog", str(self.changelog), "--fragments", str(self.fragments), *args],
            capture_output=True, text=True,
        )


class FragmentTestCase(unittest.TestCase):
    def setUp(self):
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        self.t = Tree(Path(tmp.name))


class CheckTest(FragmentTestCase):
    def test_a_valid_fragment_passes(self):
        self.t.fragment("a-b.c-d.md", GOOD)
        r = self.t.run("--check")
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertIn("changelog fragments OK (1 file(s))", r.stdout)

    def assertRefused(self, name: str, body: str, words: str):
        self.t.fragment(name, body)
        r = self.t.run("--check")
        self.assertEqual(r.returncode, 1, r.stdout)
        self.assertIn(name, r.stderr)
        self.assertIn(words, r.stderr)

    def test_a_bad_file_name_is_refused(self):
        self.assertRefused("Bad Name.md", GOOD, "file name must match")

    def test_an_uppercase_branch_slug_gets_the_lowercase_hint(self):
        self.assertRefused("fix-Issue-12.md", GOOD, "expected 'fix-issue-12.md'")

    def test_a_version_heading_is_refused(self):
        self.assertRefused("a.md", "## [0.0.1-ara.1] - 2026-01-01\n### Fixed\n- x\n", "no '#' or '##' heading")

    def test_an_unknown_category_is_refused(self):
        self.assertRefused("a.md", "### Fixes\n- x\n", "unknown category '### Fixes'")

    def test_a_category_with_no_bullet_is_refused(self):
        self.assertRefused("a.md", "### Fixed\n\n### Added\n- x\n", "'### Fixed' has no '- ' bullet")

    def test_text_before_the_first_category_is_refused(self):
        self.assertRefused("a.md", "intro\n### Fixed\n- x\n", "text before the first")

    def test_text_before_the_first_bullet_is_refused(self):
        self.assertRefused("a.md", "### Fixed\nstray\n- x\n", "has text before its first '- ' bullet")

    def test_a_wrapped_bullet_is_refused(self):
        self.assertRefused("a.md", "### Fixed\n- **x**: one\n  two\n", "wrapped onto a second line")

    def test_an_unclosed_fence_is_refused(self):
        self.assertRefused("a.md", "### Fixed\n- x\n  ```\n  a\n", "unclosed code fence")

    def test_nested_bullets_paragraphs_and_fences_are_accepted(self):
        self.t.fragment("a.md", "### Fixed\n- x\n  - y\n\n  second paragraph\n  ```\n  code\n  ```\n")
        r = self.t.run("--check")
        self.assertEqual(r.returncode, 0, r.stderr)

    def test_the_readme_is_not_a_fragment(self):
        r = self.t.run("--check")
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertIn("(0 file(s))", r.stdout)

    def test_an_unreleased_section_in_the_changelog_is_refused(self):
        self.t.changelog.write_text("# Changelog\n\n## [Unreleased]\n\n### Fixed\n- x\n")
        r = self.t.run("--check")
        self.assertEqual(r.returncode, 1, r.stdout)
        self.assertIn("CHANGELOG.md:3: an undated '## [Unreleased]' section", r.stderr)


class PreviewTest(FragmentTestCase):
    def test_categories_follow_the_release_order_and_entries_the_file_names(self):
        self.t.fragment("b.md", "### Fixed\n- **b fix**\n\n### Added (calibration)\n- **b cal**\n")
        self.t.fragment("a.md", "### Security\n- **a sec**\n\n### Fixed\n- **a fix**\n\n### Added\n- **a add**\n")
        self.t.fragment("c.md", "### Breaking changes\n- **c break**\n")
        r = self.t.run("--preview")
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertEqual(
            r.stdout,
            "## [Unreleased]\n\n"
            "### Breaking changes\n- **c break**\n\n"
            "### Added\n- **a add**\n\n"
            "### Added (calibration)\n- **b cal**\n\n"
            "### Fixed\n- **a fix**\n- **b fix**\n\n"
            "### Security\n- **a sec**\n",
        )

    def test_preview_consumes_nothing(self):
        p = self.t.fragment("a.md", GOOD)
        self.t.run("--preview", "--version", "0.0.1-ara.1")
        self.assertTrue(p.exists())
        self.assertEqual(self.t.changelog.read_text(), "# Changelog\n")


HEADER = "# Changelog\n\nIntro.\n\n---\n\n"
HISTORY = "## Pre-release history (backfilled 2026-05-26)\n\n### [phase-1-complete] - 2026-05-01\n\n#### Added\n- old\n"


class ReleaseTest(FragmentTestCase):
    def test_the_first_release_goes_above_the_pre_release_history(self):
        self.t.changelog.write_text(HEADER + HISTORY)
        p = self.t.fragment("a.md", "### Fixed\n- **x**\n")
        r = self.t.run("--release", "0.0.1-ara.1", "--date", "2026-12-01")
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertEqual(
            self.t.changelog.read_text(),
            HEADER + "## [0.0.1-ara.1] - 2026-12-01\n\n### Fixed\n- **x**\n\n" + HISTORY,
        )
        self.assertFalse(p.exists())
        self.assertTrue((self.t.fragments / "README.md").exists())

    def test_the_previous_release_is_collapsed(self):
        self.t.changelog.write_text(HEADER + "## [0.0.1-ara.1] - 2026-12-01\n\n### Fixed\n- **x**\n\n" + HISTORY)
        self.t.fragment("a.md", "### Added\n- **y**\n")
        r = self.t.run("--release", "0.0.1-ara.2", "--date", "2026-12-20")
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertEqual(
            self.t.changelog.read_text(),
            HEADER
            + "## [0.0.1-ara.2] - 2026-12-20\n\n### Added\n- **y**\n\n"
            + "<details>\n<summary><strong>[0.0.1-ara.1] - 2026-12-01</strong></summary>\n\n### Fixed\n- **x**\n\n</details>\n\n"
            + HISTORY,
        )

    def assertReleaseRefused(self, version: str, date: str, words: str, body: str = GOOD):
        before = HEADER + "## [0.0.1-ara.1] - 2026-12-01\n\n### Fixed\n- **x**\n\n" + HISTORY
        self.t.changelog.write_text(before)
        p = self.t.fragment("a.md", body)
        r = self.t.run("--release", version, "--date", date)
        self.assertNotEqual(r.returncode, 0)
        self.assertIn(words, r.stderr)
        self.assertEqual(self.t.changelog.read_text(), before)
        self.assertTrue(p.exists())

    def test_a_version_outside_the_ara_convention_is_refused(self):
        self.assertReleaseRefused("1.2.3", "2026-12-20", "not an Ara release version")

    def test_a_malformed_date_is_refused(self):
        self.assertReleaseRefused("0.0.1-ara.2", "20-12-2026", "not a YYYY-MM-DD date")

    def test_an_impossible_date_is_refused(self):
        self.assertReleaseRefused("0.0.1-ara.2", "2026-02-30", "not a real date")

    def test_a_version_already_released_is_refused(self):
        self.assertReleaseRefused("0.0.1-ara.1", "2026-12-20", "already released")

    def test_an_invalid_fragment_stops_the_release(self):
        self.assertReleaseRefused("0.0.1-ara.2", "2026-12-20", "fix the fragments first", body="### Fixes\n- x\n")

    def test_nothing_to_release_is_refused(self):
        self.t.changelog.write_text(HEADER + HISTORY)
        r = self.t.run("--release", "0.0.1-ara.1", "--date", "2026-12-01")
        self.assertEqual(r.returncode, 1)
        self.assertIn("nothing to release", r.stderr)


if __name__ == "__main__":
    unittest.main()
