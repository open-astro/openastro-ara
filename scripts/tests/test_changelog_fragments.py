#!/usr/bin/env python3
"""Tests for scripts/changelog-fragments.py (#1812; versions and --bump #2093).

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
        self.assertRefused("a.md", "## [1.0.0] - 2026-01-01\n### Fixed\n- x\n", "no '#' or '##' heading")

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

    def test_a_dotfile_is_not_a_fragment(self):
        self.t.fragment(".DS_Store", "\0binary")
        r = self.t.run("--check")
        self.assertEqual(r.returncode, 0, r.stderr)

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
            "## [1.0.0]\n\n"
            "### Breaking changes\n- **c break**\n\n"
            "### Added\n- **a add**\n\n"
            "### Added (calibration)\n- **b cal**\n\n"
            "### Fixed\n- **a fix**\n- **b fix**\n\n"
            "### Security\n- **a sec**\n",
        )

    def test_a_paragraph_after_a_blank_line_keeps_the_blank_line(self):
        self.t.fragment("a.md", "### Fixed\n- **x**\n\nsecond paragraph\n")
        r = self.t.run("--preview")
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertEqual(r.stdout, "## [0.0.1]\n\n### Fixed\n- **x**\n\nsecond paragraph\n")

    def test_preview_consumes_nothing(self):
        p = self.t.fragment("a.md", GOOD)
        r = self.t.run("--preview", "--version", "1.0.0~beta1")
        self.assertTrue(r.stdout.startswith("## [1.0.0~beta1]\n"), r.stdout)
        self.assertTrue(p.exists())
        self.assertEqual(self.t.changelog.read_text(), "# Changelog\n")


HEADER = "# Changelog\n\nIntro.\n\n---\n\n"
HISTORY = "## Pre-release history (backfilled 2026-05-26)\n\n### [phase-1-complete] - 2026-05-01\n\n#### Added\n- old\n"


class ReleaseTest(FragmentTestCase):
    def test_the_first_release_goes_above_the_pre_release_history(self):
        self.t.changelog.write_text(HEADER + HISTORY)
        p = self.t.fragment("a.md", "### Fixed\n- **x**\n")
        r = self.t.run("--release", "1.0.0", "--date", "2026-12-01")
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertEqual(
            self.t.changelog.read_text(),
            HEADER + "## [1.0.0] - 2026-12-01\n\n### Fixed\n- **x**\n\n" + HISTORY,
        )
        self.assertFalse(p.exists())
        self.assertTrue((self.t.fragments / "README.md").exists())

    def test_the_previous_release_is_collapsed(self):
        self.t.changelog.write_text(HEADER + "## [1.0.0] - 2026-12-01\n\n### Fixed\n- **x**\n\n" + HISTORY)
        self.t.fragment("a.md", "### Added\n- **y**\n")
        r = self.t.run("--release", "1.1.0", "--date", "2026-12-20")
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertEqual(
            self.t.changelog.read_text(),
            HEADER
            + "## [1.1.0] - 2026-12-20\n\n### Added\n- **y**\n\n"
            + "<details>\n<summary><strong>[1.0.0] - 2026-12-01</strong></summary>\n\n### Fixed\n- **x**\n\n</details>\n\n"
            + HISTORY,
        )

    def test_the_third_release_collapses_only_the_second(self):
        rel1 = "<details>\n<summary><strong>[1.0.0] - 2026-12-01</strong></summary>\n\n### Fixed\n- **x**\n\n</details>\n\n"
        rel2 = "<details>\n<summary><strong>[1.1.0] - 2026-12-20</strong></summary>\n\n### Added\n- **y**\n\n</details>\n\n"
        self.t.changelog.write_text(HEADER + "## [1.1.0] - 2026-12-20\n\n### Added\n- **y**\n\n" + rel1 + HISTORY)
        self.t.fragment("a.md", "### Fixed\n- **z**\n")
        r = self.t.run("--release", "1.1.1", "--date", "2027-01-10")
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertEqual(
            self.t.changelog.read_text(),
            HEADER + "## [1.1.1] - 2027-01-10\n\n### Fixed\n- **z**\n\n" + rel2 + rel1 + HISTORY,
        )

    def test_a_collapsed_release_counts_as_released(self):
        before = HEADER + "<details>\n<summary><strong>[1.0.0] - 2026-12-01</strong></summary>\n\n### Fixed\n- **x**\n\n</details>\n\n" + HISTORY
        self.t.changelog.write_text(before)
        self.t.fragment("a.md", GOOD)
        r = self.t.run("--release", "1.0.0", "--date", "2026-12-20")
        self.assertNotEqual(r.returncode, 0)
        self.assertIn("already released", r.stderr)
        self.assertEqual(self.t.changelog.read_text(), before)

    def assertReleaseRefused(self, version: str, date: str, words: str, body: str = GOOD):
        before = HEADER + "## [1.0.0] - 2026-12-01\n\n### Fixed\n- **x**\n\n" + HISTORY
        self.t.changelog.write_text(before)
        p = self.t.fragment("a.md", body)
        r = self.t.run("--release", version, "--date", date)
        self.assertNotEqual(r.returncode, 0)
        self.assertIn(words, r.stderr)
        self.assertEqual(self.t.changelog.read_text(), before)
        self.assertTrue(p.exists())

    def test_the_retired_ara_form_is_refused(self):
        self.assertReleaseRefused("0.0.1-ara.2", "2026-12-20", "the X.Y.Z-ara.N form is retired")

    def test_a_beta_is_refused(self):
        self.assertReleaseRefused("1.0.1~beta1", "2026-12-20", "a beta is not released this way")

    def test_a_version_below_the_bump_is_refused(self):
        self.assertReleaseRefused("1.0.1", "2026-12-20", "below the proposed bump 1.1.0", body="### Added\n- **x**\n")

    def test_a_version_below_the_latest_release_is_refused(self):
        self.assertReleaseRefused("0.9.0", "2026-12-20", "not greater than the latest dated release 1.0.0")

    def test_a_malformed_date_is_refused(self):
        self.assertReleaseRefused("1.0.1", "20-12-2026", "not a YYYY-MM-DD date")

    def test_an_impossible_date_is_refused(self):
        self.assertReleaseRefused("1.0.1", "2026-02-30", "not a real date")

    def test_a_version_already_released_is_refused(self):
        self.assertReleaseRefused("1.0.0", "2026-12-20", "already released")

    def test_an_invalid_fragment_stops_the_release(self):
        self.assertReleaseRefused("1.0.1", "2026-12-20", "fix the fragments first", body="### Fixes\n- x\n")

    def test_nothing_to_release_is_refused(self):
        self.t.changelog.write_text(HEADER + HISTORY)
        r = self.t.run("--release", "1.0.0", "--date", "2026-12-01")
        self.assertEqual(r.returncode, 1)
        self.assertIn("nothing to release", r.stderr)


class BumpTest(FragmentTestCase):
    RELEASED = HEADER + "## [1.2.0] - 2026-12-01\n\n### Fixed\n- **x**\n\n" + HISTORY

    def bump(self, *args: str) -> str:
        r = self.t.run("--bump", *args)
        self.assertEqual(r.returncode, 0, r.stderr)
        return r.stdout.strip()

    def test_breaking_is_major_added_minor_anything_else_patch(self):
        self.t.changelog.write_text(self.RELEASED)
        for body, want in (("### Fixed\n- x\n", "1.2.1"), ("### Added (tests)\n- x\n", "1.2.1"),
                           ("### Added\n- x\n", "1.3.0"), ("### Breaking changes\n- x\n", "2.0.0")):
            self.t.fragment("a.md", body)
            self.assertEqual(self.bump(), want, body)

    def test_the_version_is_a_floor_and_a_beta_counts_as_its_base(self):
        self.t.changelog.write_text(self.RELEASED)
        self.t.fragment("a.md", "### Fixed\n- x\n")
        self.assertEqual(self.bump("--version", "1.5.0~beta2"), "1.5.0")
        (self.t.root / "VERSION").write_text("1.4.0\n")
        self.assertEqual(self.bump(), "1.4.0")

    def test_before_the_first_release_a_stable_version_file_is_the_start(self):
        self.t.changelog.write_text(HEADER + HISTORY)
        (self.t.root / "VERSION").write_text("0.0.1\n")
        self.t.fragment("a.md", "### Fixed\n- x\n")
        self.assertEqual(self.bump(), "0.0.2")
        self.t.fragment("a.md", "### Added\n- x\n")
        self.assertEqual(self.bump(), "0.1.0")
        self.t.fragment("a.md", "### Breaking changes\n- x\n")
        self.assertEqual(self.bump(), "1.0.0")

    def test_a_malformed_version_is_a_usage_error(self):
        self.t.fragment("a.md", GOOD)
        r = self.t.run("--bump", "--version", "0.0.1-ara.1")
        self.assertEqual(r.returncode, 2)
        self.assertIn("is not X.Y.Z or X.Y.Z~betaN", r.stderr)

    def test_is_beta_exit_codes(self):
        for v, rc in (("1.0.0~beta1", 0), ("1.0.0", 1), ("1.0.0~beta0", 2), ("0.0.1-ara.1", 2)):
            self.assertEqual(self.t.run("--is-beta", v).returncode, rc, v)

    def test_self_test_passes(self):
        r = subprocess.run([sys.executable, str(SCRIPT), "--self-test"], capture_output=True, text=True)
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertIn("changelog-fragments self-test OK", r.stdout)


if __name__ == "__main__":
    unittest.main()
