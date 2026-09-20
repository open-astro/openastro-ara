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


def next_target(module, pubspec: Path) -> tuple[str, str]:
    """A (flutter, dart) pair guaranteed to differ from what `pubspec` pins.

    Derived, never hardcoded: these tests copy the REAL pubspec, and the whole
    point of this workflow is that it rewrites that file weekly. A literal
    target ("3.48.2") silently stops exercising the rewrite the moment the pin
    reaches that minor — and would then fail on the very bump PR the watcher
    opens, in the sanity job, for a reason unrelated to the change.
    """
    import re

    text = pubspec.read_text()
    fl = re.search(r"(?m)^[ \t]+flutter:[ \t]*'>=(\d+)\.(\d+)\.", text)
    sdk = re.search(r"(?m)^[ \t]+sdk:[ \t]*\^(\d+)\.(\d+)\.", text)
    assert fl and sdk, "copied pubspec is missing its environment constraints"
    return (
        f"{fl.group(1)}.{int(fl.group(2)) + 1}.2",
        f"{sdk.group(1)}.{int(sdk.group(2)) + 1}.1",
    )


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
        self.flutter, self.dart = next_target(self.m, self.m.PUBSPEC)
        self.addCleanup(self.tmp.cleanup)

    def test_apply_updates_both_files(self):
        changes = self.m.apply(self.flutter, self.dart)
        self.assertEqual(self.m.VERSION_FILE.read_text(), f"{self.flutter}\n")
        text = self.m.PUBSPEC.read_text()
        self.assertIn(f"  flutter: {self.m.flutter_constraint(self.flutter)}", text)
        self.assertIn(f"  sdk: {self.m.dart_constraint(self.dart)}", text)
        self.assertEqual(len(changes), 3)

    def test_apply_leaves_the_dependency_sdk_lines_alone(self):
        """'sdk: flutter' under dependencies must not be rewritten.

        This is why the edit is scoped to the environment: block — a file-wide
        substitution on '  sdk:' could hit the wrong key.
        """
        before = self.m.PUBSPEC.read_text()
        self.assertIn("    sdk: flutter", before)
        self.m.apply(self.flutter, self.dart)
        after = self.m.PUBSPEC.read_text()
        self.assertEqual(before.count("    sdk: flutter"), after.count("    sdk: flutter"))

    def test_apply_changes_exactly_two_pubspec_lines(self):
        before = self.m.PUBSPEC.read_text().splitlines()
        self.m.apply(self.flutter, self.dart)
        after = self.m.PUBSPEC.read_text().splitlines()
        self.assertEqual(len(before), len(after))
        differing = [i for i, (a, b) in enumerate(zip(before, after)) if a != b]
        self.assertEqual(len(differing), 2, f"expected 2 changed lines, got {differing}")

    def test_apply_is_idempotent(self):
        self.m.apply(self.flutter, self.dart)
        self.assertEqual(self.m.apply(self.flutter, self.dart), [])

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


class CheckModeTest(unittest.TestCase):
    """--check is what the workflow keys off: every downstream step gates on
    its step outputs, so an unemitted or inverted output silently turns the
    whole watcher into a no-op that still reports success."""

    def setUp(self):
        self.m = load_module()
        self.tmp = tempfile.TemporaryDirectory()
        root = Path(self.tmp.name)
        client = root / "client" / "openastroara_client"
        client.mkdir(parents=True)
        shutil.copy(REAL_PUBSPEC, client / "pubspec.yaml")
        self.m.REPO_ROOT = root
        self.m.CLIENT = client
        self.m.VERSION_FILE = client / ".flutter-version"
        self.m.PUBSPEC = client / "pubspec.yaml"
        self.out = root / "gh_output"
        self.out.write_text("")
        self.addCleanup(self.tmp.cleanup)

    def _run(self, pinned, latest, dart="3.14.1"):
        import os
        import sys
        from unittest import mock

        self.m.VERSION_FILE.write_text(f"{pinned}\n")
        self.m.fetch_current_stable = lambda: (latest, dart)
        saved = sys.argv
        sys.argv = ["check-flutter-release.py", "--check"]
        try:
            with mock.patch.dict(os.environ, {"GITHUB_OUTPUT": str(self.out)}):
                rc = self.m.main()
        finally:
            sys.argv = saved
        outputs = dict(
            line.split("=", 1)
            for line in self.out.read_text().splitlines()
            if "=" in line
        )
        return rc, outputs

    def test_a_newer_patch_emits_update_true_with_the_branch_name(self):
        rc, out = self._run("3.47.5", "3.48.2")
        self.assertEqual(rc, 0)
        self.assertEqual(out["update"], "true")
        self.assertEqual(out["major"], "false")
        self.assertEqual(out["latest"], "3.48.2")
        self.assertEqual(out["pinned"], "3.47.5")
        self.assertEqual(out["dart"], "3.14.1")
        self.assertEqual(out["branch"], "ci/flutter-3.48.2")

    def test_an_equal_version_emits_update_false(self):
        rc, out = self._run("3.47.5", "3.47.5")
        self.assertEqual(rc, 0)
        self.assertEqual(out["update"], "false")
        self.assertNotIn("branch", out)

    def test_an_older_stable_never_proposes_a_downgrade(self):
        rc, out = self._run("3.47.5", "3.47.4")
        self.assertEqual(rc, 0)
        self.assertEqual(out["update"], "false")

    def test_a_double_digit_patch_is_newer_than_a_single_digit_one(self):
        """3.47.10 > 3.47.9 — a lexical compare would stall the watcher here."""
        rc, out = self._run("3.47.9", "3.47.10")
        self.assertEqual(out["update"], "true")
        self.assertEqual(out["branch"], "ci/flutter-3.47.10")

    def test_a_major_bump_reports_but_does_not_propose(self):
        rc, out = self._run("3.47.5", "4.0.0", dart="4.0.0")
        self.assertEqual(rc, 0)
        self.assertEqual(out["update"], "false")
        self.assertEqual(out["major"], "true")
        self.assertEqual(out["latest"], "4.0.0")

    def test_emit_output_is_a_noop_without_github_output(self):
        import os
        from unittest import mock

        with mock.patch.dict(os.environ, {}, clear=True):
            self.m.emit_output(update="true")  # must not raise


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


class SupersedeSelectorTest(unittest.TestCase):
    """The supersede step's jq selectors, read out of the real workflow.

    The selectors live in a `run:` block, so nothing else executes them until
    a stable actually ships. These cases pull them straight out of
    check-flutter.yml and run them through jq, so an edit that widens the
    branch filter or reintroduces the `null` new_pr fails here instead of on
    the next live bump.
    """

    WORKFLOW = REPO_ROOT / ".github" / "workflows" / "check-flutter.yml"

    @classmethod
    def setUpClass(cls):
        # No jq guard here. Four of these cases assert on the shape of the
        # workflow text and never invoke jq at all; skipping the whole class
        # on a box without jq would silently drop them. `_jq` skips per-test
        # instead, so the shape guards stay live everywhere.
        cls.text = cls.WORKFLOW.read_text()

    def _selector(self, marker: str) -> str:
        import re

        m = re.search(r"--jq '(" + marker + r"[^']*)'", self.text)
        self.assertIsNotNone(m, f"no --jq selector matching {marker!r} in {self.WORKFLOW}")
        # `run: |` is a literal block scalar and the argument is single-quoted,
        # so what the file holds is byte-for-byte what jq is handed: the
        # workflow's `\\.` stays `\\.`, which is how a jq *string* spells the
        # regex `\.`. Unescaping it here would hand jq an invalid escape.
        return m.group(1)

    def _jq(self, selector: str, payload: str) -> str:
        """Run `selector` through every jq engine present on this machine.

        Production runs these through the gojq embedded in `gh --jq` (Go
        `regexp`); a developer box has oniguruma `jq`. The selectors are
        deliberately written to the intersection of the two -- character
        classes, `+`, anchors, escaped dots, nothing engine-specific.

        Be clear about the coverage this actually buys: ubuntu-latest, where
        CI runs this suite, ships `jq` and not `gojq`, so in CI the only
        engine exercised is the one production does NOT use. The cross-check
        fires only where someone has both installed. Closing that gap means
        installing gojq in the sanity job -- tracked in design/PORT_TODO.md,
        not done here because it edits ci.yml.
        """
        import subprocess

        outs = []
        for engine in ("jq", "gojq"):
            if shutil.which(engine) is None:
                continue
            proc = subprocess.run(
                [engine, "-r", selector],
                input=payload,
                capture_output=True,
                text=True,
                check=True,
            )
            outs.append((engine, proc.stdout.strip()))
        if not outs:
            raise unittest.SkipTest("no jq engine on PATH")
        for engine, out in outs[1:]:
            self.assertEqual(
                out, outs[0][1], f"{engine} and {outs[0][0]} disagree on {selector!r}"
            )
        return outs[0][1]

    def test_only_versioned_bump_branches_are_superseded(self):
        # Every non-match here is a branch a human could plausibly create in
        # the ci/flutter- namespace. Matching one would comment on it, close
        # it and delete its branch, so the anchor has to be the full version.
        selector = self._selector(r"\.\[\] \| select")
        payload = """[
          {"number": 1, "headRefName": "ci/flutter-3.48.0"},
          {"number": 2, "headRefName": "ci/flutter-pin-fixup"},
          {"number": 3, "headRefName": "ci/flutter-3.48.1"},
          {"number": 4, "headRefName": "fix/unrelated"},
          {"number": 5, "headRefName": "ci/flutter-3.48.0-fixup"},
          {"number": 6, "headRefName": "ci/flutter-3.x-triage"},
          {"number": 7, "headRefName": "ci/flutter-3.48"},
          {"number": 8, "headRefName": "ci/flutter-10.2.30"}
        ]"""
        self.assertEqual(self._jq(selector, payload).split(), ["1", "3", "8"])

    def test_the_selector_matches_what_the_script_actually_generates(self):
        # The one pairing that matters: the branch name check-flutter-release.py
        # emits must be superseded. Derived from the script, not retyped, so a
        # change to either side fails here rather than on the next live bump.
        import re

        src = (REPO_ROOT / "scripts" / "check-flutter-release.py").read_text()
        m = re.search(r'branch=f"(ci/flutter-\{[a-z_]+\})"', src)
        self.assertIsNotNone(m, "check-flutter-release.py no longer builds a branch= f-string")
        generated = m.group(1).replace("{latest}", "3.48.1")
        self.assertNotIn("{", generated, "unexpected placeholder in the branch template")
        selector = self._selector(r"\.\[\] \| select")
        payload = '[{"number": 1, "headRefName": "%s"}]' % generated
        self.assertEqual(self._jq(selector, payload), "1")

    def _commit_count_branches(self) -> dict:
        """The three arms of the commit-count `if`, split on CODE lines only.

        Never on the bare words `else`/`fi`: the arms are full of prose
        ("fix commits", "configured", "verified") that contains those
        substrings, and splitting on them truncates an arm so an assertion
        about what it does NOT contain passes vacuously. A shell keyword is
        a stripped line equal to the keyword, and a comment starts with `#`.
        """
        body = self.text.split("| while read -r old_pr; do", 1)[1]
        arms, current, depth = {}, None, 0
        for raw in body.splitlines():
            line = raw.strip()
            if line.startswith("#"):
                continue
            if line == 'if [ "$commits" = 1 ]; then':
                current, depth = "one", 1
                arms[current] = []
                continue
            if depth == 0:
                continue
            if line == 'elif [ "$commits" = unknown ]; then':
                current = "unknown"
                arms[current] = []
                continue
            if line == "else":
                current = "many"
                arms[current] = []
                continue
            if line == "fi":
                break
            arms[current].append(line)
        return {k: "\n".join(v) for k, v in arms.items()}

    def test_a_touched_bump_branch_is_closed_but_not_deleted(self):
        # supersede was chosen over skip-while-open so a contentious bump stays
        # visible; someone investigating a breakage pushes onto that branch, and
        # deleting it would put that work behind a Restore-branch button.
        arms = self._commit_count_branches()
        self.assertEqual(set(arms), {"one", "unknown", "many"})
        self.assertIn('gh pr close "$old_pr" --delete-branch', arms["one"])
        for name in ("unknown", "many"):
            self.assertIn('gh pr close "$old_pr"', arms[name])
            self.assertNotIn("--delete-branch", arms[name])
        # The split has to have found real content in every arm, or the
        # assertNotIn above would be vacuous.
        for name, arm in arms.items():
            self.assertIn("gh pr comment", arm, f"{name} arm looks truncated")

    def test_an_unreadable_commit_count_never_states_a_number(self):
        # `commits` is interpolated into a public comment. A numeric default
        # would post "carries 2 commits" on someone's seven-commit branch.
        self.assertIn("|| echo unknown)", self.text)
        self.assertIn("[ -n \"$commits\" ] || commits=unknown", self.text)
        arms = self._commit_count_branches()
        self.assertNotIn("${commits}", arms["unknown"])
        self.assertIn("could not be read", arms["unknown"])

    def test_the_new_pr_lookup_is_fork_scoped(self):
        # If a fork PR shared the new head-branch name, an unscoped `first`
        # could return its number and the loop would then close the PR this
        # run just opened, with --delete-branch.
        self.assertIn(
            "gh pr list --head \"$BRANCH\" --state open "
            "--json number,isCrossRepository",
            self.text,
        )
        selector = self._selector(r"first")
        payload = """[
          {"number": 9, "isCrossRepository": true},
          {"number": 4, "isCrossRepository": false}
        ]"""
        self.assertEqual(self._jq(selector, payload), "4")

    def test_fork_prs_are_not_superseded(self):
        # headRefName on a cross-repo PR is the branch name on the FORK,
        # unqualified, so a contributor's `ci/flutter-3.49.0` would otherwise
        # be commented on and closed. The namespace is only ours on origin.
        selector = self._selector(r"\.\[\] \| select")
        payload = """[
          {"number": 1, "headRefName": "ci/flutter-3.48.0", "isCrossRepository": false},
          {"number": 2, "headRefName": "ci/flutter-3.49.0", "isCrossRepository": true}
        ]"""
        self.assertEqual(self._jq(selector, payload).split(), ["1"])

    def test_every_gh_call_in_the_supersede_loop_isolates_stdin(self):
        """The loop body's gh calls inherit the `gh pr list` pipe as stdin.

        Demonstrated rather than asserted on shape: a body call that reads
        stdin drains the pipe and every PR after the first is silently left
        un-superseded. `< /dev/null` on each call is what prevents it -- and
        it has to be on the calls, not on `done`, which would starve `read`.
        """
        import subprocess
        import textwrap

        # r""" matters: without it the `\n`s below become real newlines, the
        # resulting zero-indent lines make dedent's common prefix "", and the
        # dedent silently does nothing while reading as if it does.
        script = textwrap.dedent(
            r"""
            drains_stdin() { cat > /dev/null; }
            printf '1\n2\n3\n' | while read -r old_pr; do
              echo "$old_pr"
              drains_stdin GUARD
            done
            """
        )
        unguarded = subprocess.run(
            ["bash", "-c", script.replace("GUARD", "")],
            capture_output=True, text=True, check=True,
        ).stdout.split()
        guarded = subprocess.run(
            ["bash", "-c", script.replace("GUARD", "< /dev/null")],
            capture_output=True, text=True, check=True,
        ).stdout.split()
        self.assertEqual(unguarded, ["1"], "expected the unguarded loop to lose PRs")
        self.assertEqual(guarded, ["1", "2", "3"])

        # Now the real thing: no bare `gh` call inside the loop body.
        marker = "| while read -r old_pr; do"
        body = self.text.split(marker, 1)[1].split("\n                done", 1)[0]
        calls = [
            line.strip()
            for line in body.splitlines()
            if line.strip().startswith("gh ") or "$(gh " in line
        ]
        self.assertTrue(calls, "no gh calls found in the supersede loop body")
        for call in calls:
            self.assertIn("< /dev/null", call, f"gh call does not isolate stdin: {call}")

    def test_the_supersede_list_is_not_capped_near_the_repos_pr_count(self):
        # Any finite cap reintroduces the silent-no-op; keep it well past
        # anything this repo will plausibly hold open at once.
        import re

        m = re.search(r"gh pr list --state open --limit (\d+)", self.text)
        self.assertIsNotNone(m, "the supersede list call no longer passes --limit")
        self.assertGreaterEqual(int(m.group(1)), 1000)

    def test_new_pr_selector_is_empty_when_nothing_matches(self):
        # `.[0].number` would print a literal "null" here, `[ -n ]` would pass,
        # and the loop would then close the PR the run had just opened.
        selector = self._selector(r"first")
        self.assertEqual(self._jq(selector, "[]"), "")

    def test_new_pr_selector_yields_the_number_when_one_matches(self):
        selector = self._selector(r"first")
        self.assertEqual(self._jq(selector, '[{"number": 42}]'), "42")



if __name__ == "__main__":
    unittest.main()
