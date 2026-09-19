#!/usr/bin/env python3
"""Tests for scripts/check-flutter-release.py.

Run from the repo root:

    python3 -m unittest discover -s scripts/tests -v

No network: the release feed is stubbed. The --apply cases run against a
throwaway copy of the *real* pubspec.yaml, so a change to that file's shape
breaks these tests rather than silently breaking the weekly bump.
"""

from __future__ import annotations

import importlib.util
import shutil
import tempfile
import unittest
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent.parent
SCRIPT = REPO_ROOT / "scripts" / "check-flutter-release.py"
REAL_PUBSPEC = REPO_ROOT / "client" / "openastroara_client" / "pubspec.yaml"


def load_module():
    spec = importlib.util.spec_from_file_location("check_flutter_release", SCRIPT)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


class ConstraintMathTest(unittest.TestCase):
    def setUp(self):
        self.m = load_module()

    def test_flutter_constraint_spans_one_minor(self):
        self.assertEqual(self.m.flutter_constraint("3.47.5"), "'>=3.47.0 <3.48.0'")

    def test_flutter_constraint_rolls_the_minor_over_to_double_digits(self):
        # 3.9.0 must yield <3.10.0, not <3.1.0 — arithmetic, not string bump.
        self.assertEqual(self.m.flutter_constraint("3.9.0"), "'>=3.9.0 <3.10.0'")

    def test_flutter_constraint_rolls_a_hundred_over(self):
        self.assertEqual(self.m.flutter_constraint("3.99.1"), "'>=3.99.0 <3.100.0'")

    def test_dart_constraint_pins_to_the_minor(self):
        self.assertEqual(self.m.dart_constraint("3.13.4"), "^3.13.0")

    def test_versions_compare_numerically_not_lexically(self):
        # The bug this guards: "3.47.9" > "3.47.10" as strings, so a lexical
        # compare would refuse to bump from .9 to .10 and silently stall.
        self.assertGreater(
            self.m.parse_version("3.47.10"), self.m.parse_version("3.47.9")
        )
        self.assertGreater(self.m.parse_version("4.0.0"), self.m.parse_version("3.99.99"))

    def test_rejects_non_bare_versions(self):
        for bad in ("3.47", "v3.47.5", "3.47.5-beta", "", "latest"):
            with self.subTest(bad=bad), self.assertRaises(SystemExit):
                self.m.parse_version(bad)


class ApplyTest(unittest.TestCase):
    """--apply against a copy of the real pubspec."""

    def setUp(self):
        self.m = load_module()
        self.tmp = tempfile.TemporaryDirectory()
        root = Path(self.tmp.name)
        client = root / "client" / "openastroara_client"
        client.mkdir(parents=True)
        shutil.copy(REAL_PUBSPEC, client / "pubspec.yaml")
        (client / ".flutter-version").write_text("3.47.5\n")
        self.m.REPO_ROOT = root
        self.m.CLIENT = client
        self.m.VERSION_FILE = client / ".flutter-version"
        self.m.PUBSPEC = client / "pubspec.yaml"
        self.addCleanup(self.tmp.cleanup)

    def test_apply_updates_both_files(self):
        changes = self.m.apply("3.48.2", "3.14.1")
        self.assertEqual(self.m.VERSION_FILE.read_text(), "3.48.2\n")
        text = self.m.PUBSPEC.read_text()
        self.assertIn("  flutter: '>=3.48.0 <3.49.0'", text)
        self.assertIn("  sdk: ^3.14.0", text)
        self.assertEqual(len(changes), 3)

    def test_apply_leaves_the_dependency_sdk_lines_alone(self):
        """'sdk: flutter' under dependencies must not be rewritten.

        This is why the edit is scoped to the environment: block — a file-wide
        substitution on '  sdk:' could hit the wrong key.
        """
        before = self.m.PUBSPEC.read_text()
        self.assertIn("    sdk: flutter", before)
        self.m.apply("3.48.2", "3.14.1")
        after = self.m.PUBSPEC.read_text()
        self.assertEqual(before.count("    sdk: flutter"), after.count("    sdk: flutter"))

    def test_apply_changes_exactly_two_pubspec_lines(self):
        before = self.m.PUBSPEC.read_text().splitlines()
        self.m.apply("3.48.2", "3.14.1")
        after = self.m.PUBSPEC.read_text().splitlines()
        self.assertEqual(len(before), len(after))
        differing = [i for i, (a, b) in enumerate(zip(before, after)) if a != b]
        self.assertEqual(len(differing), 2, f"expected 2 changed lines, got {differing}")

    def test_apply_is_idempotent(self):
        self.m.apply("3.48.2", "3.14.1")
        self.assertEqual(self.m.apply("3.48.2", "3.14.1"), [])

    def test_missing_environment_block_is_fatal(self):
        self.m.PUBSPEC.write_text("name: openastroara\ndependencies:\n  dio: ^5.0.0\n")
        with self.assertRaises(SystemExit):
            self.m.apply("3.48.2", "3.14.1")

    def test_a_sdk_key_before_the_environment_block_is_not_rewritten(self):
        """The real guard for scoping the edit to the environment: block.

        YAML keys are unordered, so 'dependencies' may precede 'environment'.
        An unscoped '^  sdk:' substitution takes the FIRST match in the file and
        would rewrite the dependency instead — silently corrupting the pubspec
        while leaving the Dart constraint stale. With 'environment:' first (as
        in the current file) an unscoped regex happens to land correctly, so
        this ordering is what actually exercises the scoping.
        """
        self.m.PUBSPEC.write_text(
            "name: openastroara\n"
            "dependencies:\n"
            "  sdk: some_package\n"
            "  flutter: ^1.0.0\n"
            "\n"
            "environment:\n"
            "  sdk: ^3.13.0\n"
            "  flutter: '>=3.47.0 <3.48.0'\n"
        )
        self.m.apply("3.48.2", "3.14.1")
        text = self.m.PUBSPEC.read_text()
        # The environment block moved...
        self.assertIn("environment:\n  sdk: ^3.14.0\n  flutter: '>=3.48.0 <3.49.0'", text)
        # ...and the identically-indented dependency keys did not.
        self.assertIn("dependencies:\n  sdk: some_package\n  flutter: ^1.0.0", text)

    def test_environment_block_ends_at_the_next_top_level_key(self):
        """A 'flutter:' key outside environment must not be picked up."""
        self.m.PUBSPEC.write_text(
            "name: openastroara\n"
            "environment:\n"
            "  sdk: ^3.13.0\n"
            "  flutter: '>=3.47.0 <3.48.0'\n"
            "\n"
            "flutter:\n"
            "  uses-material-design: true\n"
        )
        self.m.apply("3.48.2", "3.14.1")
        text = self.m.PUBSPEC.read_text()
        self.assertIn("  flutter: '>=3.48.0 <3.49.0'", text)
        self.assertIn("flutter:\n  uses-material-design: true", text)


class MajorBumpTest(unittest.TestCase):
    def setUp(self):
        self.m = load_module()

    def test_major_bump_is_refused_by_apply(self):
        """A 3.x -> 4.x jump must never be applied automatically."""
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        root = Path(tmp.name)
        client = root / "client" / "openastroara_client"
        client.mkdir(parents=True)
        shutil.copy(REAL_PUBSPEC, client / "pubspec.yaml")
        (client / ".flutter-version").write_text("3.47.5\n")
        self.m.REPO_ROOT = root
        self.m.CLIENT = client
        self.m.VERSION_FILE = client / ".flutter-version"
        self.m.PUBSPEC = client / "pubspec.yaml"
        self.m.fetch_current_stable = lambda: ("4.0.0", "4.0.0")

        import sys

        argv = sys.argv
        sys.argv = ["check-flutter-release.py", "--apply"]
        try:
            with self.assertRaises(SystemExit) as ctx:
                self.m.main()
            self.assertEqual(ctx.exception.code, 1)
        finally:
            sys.argv = argv
        # The pin must be untouched.
        self.assertEqual(self.m.VERSION_FILE.read_text(), "3.47.5\n")


class ExplicitVersionTest(unittest.TestCase):
    """--apply --version/--dart must bypass the feed entirely.

    The workflow passes the version --check already resolved, so a release
    landing between the two calls cannot write one version while the branch
    name, title and commit message cite another. Every test here makes
    fetch_current_stable() raise: if the flags stop being honoured, the code
    falls back to the feed and these fail.
    """

    def setUp(self):
        self.m = load_module()
        self.tmp = tempfile.TemporaryDirectory()
        root = Path(self.tmp.name)
        client = root / "client" / "openastroara_client"
        client.mkdir(parents=True)
        shutil.copy(REAL_PUBSPEC, client / "pubspec.yaml")
        (client / ".flutter-version").write_text("3.47.5\n")
        self.m.REPO_ROOT = root
        self.m.CLIENT = client
        self.m.VERSION_FILE = client / ".flutter-version"
        self.m.PUBSPEC = client / "pubspec.yaml"

        def explode():
            raise AssertionError("fetch_current_stable() must not be called")

        self.m.fetch_current_stable = explode
        self.addCleanup(self.tmp.cleanup)

    def _run(self, *argv):
        import sys

        saved = sys.argv
        sys.argv = ["check-flutter-release.py", *argv]
        try:
            return self.m.main()
        finally:
            sys.argv = saved

    def test_apply_with_explicit_version_never_reads_the_feed(self):
        self._run("--apply", "--version", "3.48.2", "--dart", "3.14.1")
        self.assertEqual(self.m.VERSION_FILE.read_text(), "3.48.2\n")
        text = self.m.PUBSPEC.read_text()
        self.assertIn("  flutter: '>=3.48.0 <3.49.0'", text)
        self.assertIn("  sdk: ^3.14.0", text)

    def test_version_without_dart_is_rejected(self):
        with self.assertRaises(SystemExit) as ctx:
            self._run("--apply", "--version", "3.48.2")
        self.assertEqual(ctx.exception.code, 1)
        self.assertEqual(self.m.VERSION_FILE.read_text(), "3.47.5\n")

    def test_dart_without_version_is_rejected(self):
        with self.assertRaises(SystemExit) as ctx:
            self._run("--apply", "--dart", "3.14.1")
        self.assertEqual(ctx.exception.code, 1)

    def test_check_rejects_an_explicit_version(self):
        """--check reports on the live feed; pinning it would be meaningless."""
        with self.assertRaises(SystemExit) as ctx:
            self._run("--check", "--version", "3.48.2", "--dart", "3.14.1")
        self.assertEqual(ctx.exception.code, 1)

    def test_an_explicit_major_bump_is_still_refused(self):
        with self.assertRaises(SystemExit) as ctx:
            self._run("--apply", "--version", "4.0.0", "--dart", "4.0.0")
        self.assertEqual(ctx.exception.code, 1)
        self.assertEqual(self.m.VERSION_FILE.read_text(), "3.47.5\n")

    def test_a_malformed_explicit_version_is_rejected(self):
        with self.assertRaises(SystemExit):
            self._run("--apply", "--version", "3.48", "--dart", "3.14.1")


class AtomicityTest(unittest.TestCase):
    def setUp(self):
        self.m = load_module()
        self.tmp = tempfile.TemporaryDirectory()
        root = Path(self.tmp.name)
        client = root / "client" / "openastroara_client"
        client.mkdir(parents=True)
        self.m.REPO_ROOT = root
        self.m.CLIENT = client
        self.m.VERSION_FILE = client / ".flutter-version"
        self.m.PUBSPEC = client / "pubspec.yaml"
        client.mkdir(parents=True, exist_ok=True)
        (client / ".flutter-version").write_text("3.47.5\n")
        self.addCleanup(self.tmp.cleanup)

    def test_a_bad_pubspec_leaves_the_version_file_untouched(self):
        """Don't half-update the tree: someone runs --apply by hand."""
        self.m.PUBSPEC.write_text("name: openastroara\ndependencies:\n  dio: ^5.0.0\n")
        with self.assertRaises(SystemExit):
            self.m.apply("3.48.2", "3.14.1")
        self.assertEqual(self.m.VERSION_FILE.read_text(), "3.47.5\n")


class FeedParsingTest(unittest.TestCase):
    """fetch_current_stable against a stubbed feed — no network."""

    def setUp(self):
        self.m = load_module()

    def _stub(self, payload):
        """Patch urlopen for this test only.

        mock.patch.object restores on teardown — assigning directly would
        mutate the real urllib.request for the whole interpreter and silently
        swallow a genuine network call from any test module added later.
        """
        import contextlib
        import io
        import json
        from unittest import mock

        @contextlib.contextmanager
        def fake_urlopen(url, timeout=None):
            yield io.StringIO(json.dumps(payload))

        patcher = mock.patch.object(
            self.m.urllib.request, "urlopen", fake_urlopen
        )
        patcher.start()
        self.addCleanup(patcher.stop)

    def test_picks_the_release_matching_current_release_stable(self):
        self._stub(
            {
                "current_release": {"stable": "HASH_B"},
                "releases": [
                    {
                        "hash": "HASH_A",
                        "channel": "stable",
                        "version": "3.47.4",
                        "dart_sdk_version": "3.13.3",
                    },
                    {
                        "hash": "HASH_B",
                        "channel": "stable",
                        "version": "3.47.5",
                        "dart_sdk_version": "3.13.4",
                    },
                ],
            }
        )
        self.assertEqual(self.m.fetch_current_stable(), ("3.47.5", "3.13.4"))

    def test_strips_build_metadata_from_the_dart_version(self):
        self._stub(
            {
                "current_release": {"stable": "H"},
                "releases": [
                    {
                        "hash": "H",
                        "channel": "stable",
                        "version": "3.47.5",
                        "dart_sdk_version": "3.13.4 (build 3.13.4-cafebabe)",
                    }
                ],
            }
        )
        self.assertEqual(self.m.fetch_current_stable(), ("3.47.5", "3.13.4"))

    def test_a_beta_entry_sharing_the_hash_is_not_accepted(self):
        self._stub(
            {
                "current_release": {"stable": "H"},
                "releases": [
                    {
                        "hash": "H",
                        "channel": "beta",
                        "version": "3.48.0",
                        "dart_sdk_version": "3.14.0",
                    }
                ],
            }
        )
        with self.assertRaises(SystemExit):
            self.m.fetch_current_stable()

    def test_missing_current_release_is_fatal(self):
        self._stub({"releases": []})
        with self.assertRaises(SystemExit):
            self.m.fetch_current_stable()


if __name__ == "__main__":
    unittest.main()
