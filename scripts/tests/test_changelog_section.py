#!/usr/bin/env python3
"""Tests for scripts/changelog-section.py (#2093): one CHANGELOG section as Release notes.

Run from the repo root:

    python3 -m unittest discover -s scripts/tests -v
"""

from __future__ import annotations

import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent.parent
SCRIPT = REPO_ROOT / "scripts" / "changelog-section.py"

CHANGELOG = """# Changelog

---

## [1.1.0] - 2027-01-10

### Added
- **new**

<details>
<summary><strong>[1.0.0] - 2026-12-01</strong></summary>

### Fixed
- **old**

</details>

## Pre-release history (backfilled 2026-05-26)
"""


class ChangelogSectionTest(unittest.TestCase):
    def setUp(self):
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        self.changelog = Path(tmp.name) / "CHANGELOG.md"
        self.changelog.write_text(CHANGELOG)

    def run_section(self, *args: str) -> subprocess.CompletedProcess:
        return subprocess.run([sys.executable, str(SCRIPT), *args, "--changelog", str(self.changelog)],
                              capture_output=True, text=True)

    def test_the_expanded_section_body_is_printed(self):
        r = self.run_section("1.1.0")
        self.assertEqual((r.returncode, r.stdout), (0, "### Added\n- **new**\n"), r.stderr)

    def test_a_collapsed_section_body_is_printed(self):
        r = self.run_section("1.0.0")
        self.assertEqual((r.returncode, r.stdout), (0, "### Fixed\n- **old**\n"), r.stderr)

    def test_a_missing_section_fails(self):
        r = self.run_section("1.0.0-beta.1")
        self.assertEqual(r.returncode, 1)
        self.assertIn("no CHANGELOG section for [1.0.0-beta.1]", r.stderr)

    def test_the_anchor_is_githubs_heading_slug(self):
        r = self.run_section("1.1.0", "--anchor")
        self.assertEqual((r.returncode, r.stdout), (0, "110---2027-01-10\n"), r.stderr)

    def test_self_test_passes(self):
        r = subprocess.run([sys.executable, str(SCRIPT), "--self-test"], capture_output=True, text=True)
        self.assertEqual(r.returncode, 0, r.stderr)


if __name__ == "__main__":
    unittest.main()
