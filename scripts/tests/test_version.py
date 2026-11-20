#!/usr/bin/env python3
"""Tests for scripts/version.py (#2093): the VERSION file and its spellings.

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
SCRIPT = REPO_ROOT / "scripts" / "version.py"
PUBSPEC = "client/openastroara_client/pubspec.yaml"
APP_VERSION_DART = "client/openastroara_client/lib/version/ara_version.dart"


def run(*args: str, stdin: str | None = None) -> subprocess.CompletedProcess:
    return subprocess.run([sys.executable, str(SCRIPT), *args], capture_output=True, text=True, input=stdin)


class Tree:
    def __init__(self, root: Path, version: str, pubspec: str, badge: str, app: str | None = None):
        """app is kAraAppVersion; by default the pubspec version before '+'."""
        self.root = root
        (root / "VERSION").write_text(version + "\n")
        (root / PUBSPEC).parent.mkdir(parents=True)
        (root / PUBSPEC).write_text("name: openastroara_client\nversion: %s\n" % pubspec)
        (root / "README.md").write_text("# OpenAstro Ara\n\n#### [%s] - 2026-12-01 · [Changelog](CHANGELOG.md)\n" % badge)
        (root / APP_VERSION_DART).parent.mkdir(parents=True)
        (root / APP_VERSION_DART).write_text(
            "library;\n\nconst String kAraAppVersion = '%s';\n" % (app if app is not None else pubspec.split("+", 1)[0]))

    def check(self) -> subprocess.CompletedProcess:
        return run("--check", "--root", str(self.root))


class SpellingTest(unittest.TestCase):
    def test_a_beta_prints_each_spelling(self):
        for flag, want in (("--semver", "1.0.0-beta.1"), ("--pubspec-base", "1.0.0-beta.1"), ("--deb", "1.0.0~beta1")):
            r = run(flag, "--file", "-", stdin="1.0.0~beta1\n")
            self.assertEqual((r.returncode, r.stdout), (0, want + "\n"), flag + r.stderr)

    def test_a_stable_version_is_the_same_in_every_spelling(self):
        for flag in ("--semver", "--pubspec-base", "--deb"):
            r = run(flag, "--file", "-", stdin="0.0.1\n")
            self.assertEqual((r.returncode, r.stdout), (0, "0.0.1\n"), flag)

    def test_is_beta_exits_zero_only_for_a_beta(self):
        self.assertEqual(run("--is-beta", "--file", "-", stdin="1.0.0~beta2\n").returncode, 0)
        self.assertEqual(run("--is-beta", "--file", "-", stdin="1.0.0\n").returncode, 1)

    def test_a_malformed_version_is_refused(self):
        for bad in ("1.0", "1.0.0~beta0", "1.0.0-beta.1", "0.0.1-ara.1", "1.0.0\n2.0.0"):
            r = run("--semver", "--file", "-", stdin=bad + "\n")
            self.assertEqual(r.returncode, 2, bad)
            self.assertIn("ERROR", r.stderr)

    def test_the_repository_version_file_is_valid(self):
        r = run("--semver")
        self.assertEqual(r.returncode, 0, r.stderr)

    def test_self_test_passes(self):
        r = run("--self-test")
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertIn("version self-test OK", r.stdout)


class CheckTest(unittest.TestCase):
    def setUp(self):
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        self.root = Path(tmp.name)

    def test_agreeing_files_pass(self):
        r = Tree(self.root, "1.0.0~beta1", "1.0.0-beta.1+38", "1.0.0-beta.1").check()
        self.assertEqual(r.returncode, 0, r.stderr)

    def test_a_pubspec_on_another_version_fails(self):
        r = Tree(self.root, "1.0.0~beta1", "0.0.1+38", "1.0.0-beta.1").check()
        self.assertEqual(r.returncode, 1)
        self.assertIn("pubspec.yaml: version 0.0.1+38", r.stderr)

    def test_a_pubspec_in_the_deb_spelling_fails(self):
        r = Tree(self.root, "1.0.0~beta1", "1.0.0~beta1+38", "1.0.0-beta.1").check()
        self.assertEqual(r.returncode, 1)
        self.assertIn("want 1.0.0-beta.1+<build>", r.stderr)

    def test_a_readme_badge_on_another_version_fails(self):
        r = Tree(self.root, "1.0.0", "1.0.0+40", "1.0.0-beta.2").check()
        self.assertEqual(r.returncode, 1)
        self.assertIn("badge says 1.0.0-beta.2", r.stderr)

    def test_a_readme_with_no_badge_fails(self):
        Tree(self.root, "1.0.0", "1.0.0+40", "1.0.0")
        (self.root / "README.md").write_text("# OpenAstro Ara\n")
        r = run("--check", "--root", str(self.root))
        self.assertEqual(r.returncode, 1)
        self.assertIn("no badge line", r.stderr)

    def test_an_app_version_constant_on_another_version_fails(self):
        # Apple platforms can drop -beta.N from the platform version, so the app's
        # constant must carry it: a stable constant on a beta VERSION fails.
        r = Tree(self.root, "1.0.0~beta1", "1.0.0-beta.1+38", "1.0.0-beta.1", app="1.0.0").check()
        self.assertEqual(r.returncode, 1)
        self.assertIn("kAraAppVersion is 1.0.0, but VERSION 1.0.0~beta1 is 1.0.0-beta.1", r.stderr)

    def test_an_app_version_file_with_no_constant_fails(self):
        Tree(self.root, "1.0.0", "1.0.0+40", "1.0.0")
        (self.root / APP_VERSION_DART).write_text("library;\n")
        r = run("--check", "--root", str(self.root))
        self.assertEqual(r.returncode, 1)
        self.assertIn("no line \"const String kAraAppVersion = '1.0.0';\"", r.stderr)

    def test_the_retired_ara_form_fails(self):
        r = Tree(self.root, "0.0.1-ara.1", "0.0.1+38", "0.0.1").check()
        self.assertEqual(r.returncode, 1)
        self.assertIn("not X.Y.Z or X.Y.Z~betaN", r.stderr)


if __name__ == "__main__":
    unittest.main()
