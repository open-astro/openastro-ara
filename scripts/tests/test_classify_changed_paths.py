#!/usr/bin/env python3
"""Tests for scripts/classify-changed-paths.py.

Run from the repo root:

    python3 -m unittest discover -s scripts/tests -v

This script decides which CI jobs a PR gets to skip, so the cases below are
written from the failure direction that matters: a WRONGLY SKIPPED job is
invisible (GitHub reports the context green), while a wrongly RUN job only
costs runner minutes. Every assertion that something is `False` is therefore
a claim that the path provably cannot affect that job.
"""

from __future__ import annotations

import importlib.util
import subprocess
import unittest
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent.parent
SCRIPT = REPO_ROOT / "scripts" / "classify-changed-paths.py"
CI = REPO_ROOT / ".github" / "workflows" / "ci.yml"


def load_module():
    spec = importlib.util.spec_from_file_location("classify_changed_paths", SCRIPT)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


class ClassifyTest(unittest.TestCase):
    def setUp(self):
        self.m = load_module()

    def c(self, *paths):
        return self.m.classify(list(paths))

    # --- the bucket that may never be wrong in the skip direction ----------

    def test_a_cs_file_runs_the_dotnet_jobs(self):
        self.assertTrue(self.c("OpenAstroAra.Core/Foo.cs")["dotnet"])

    def test_an_unknown_top_level_directory_runs_the_dotnet_jobs(self):
        # The point of the open fallback: a project added tomorrow must not
        # have to be registered here before the analyzer notices it.
        self.assertTrue(self.c("OpenAstroAra.Brand.New/Thing.cs")["dotnet"])
        self.assertTrue(self.c("some-new-dir/whatever.txt")["dotnet"])

    def test_an_unknown_root_file_runs_the_dotnet_jobs(self):
        self.assertTrue(self.c("NuGet.config")["dotnet"])

    def test_known_dotnet_root_files_run_the_dotnet_jobs(self):
        for f in sorted(self.m.DOTNET_ROOT_FILES):
            with self.subTest(f=f):
                self.assertTrue(self.c(f)["dotnet"], f)

    def test_vendored_native_sources_run_the_dotnet_jobs(self):
        # SOFA/NOVAS31 are compiled by build-astrometry-natives.sh and staged
        # next to the test binary; the transform tests self-Ignore without
        # them, so a change here that skipped the analyzer would be invisible.
        self.assertTrue(self.c("SOFA/src/iauAtci13.c")["dotnet"])
        self.assertTrue(self.c("NOVAS31/novas.c")["dotnet"])
        self.assertTrue(self.c("scripts/build-astrometry-natives.sh")["dotnet"])

    def test_the_simulator_pin_fixture_runs_the_dotnet_jobs(self):
        # SIMULATORS_VERSION.md is verified by SimulatorVersionPinTest in the
        # analyzer gate and re-downloaded by both Alpaca jobs. The §14.5.1
        # auto-bump PR touches this file and nothing else, so a nested-*.md
        # shortcut here would skip exactly the gates it exists to pass.
        p = "OpenAstroAra.Test/fixtures/SIMULATORS_VERSION.md"
        self.assertTrue(self.c(p)["dotnet"])
        self.assertFalse(self.c(p)["docs_only"])

    def test_the_alpaca_simulator_fetcher_runs_the_dotnet_jobs(self):
        self.assertTrue(self.c("scripts/get-alpaca-simulators.sh")["dotnet"])

    def test_the_packaging_tree_runs_the_dotnet_jobs(self):
        self.assertTrue(self.c("packaging/debian/control")["dotnet"])

    # --- the closed bucket -------------------------------------------------

    def test_only_client_paths_reach_the_client_jobs(self):
        self.assertTrue(self.c("client/openastroara_client/lib/main.dart")["client"])
        # Every client step runs with working-directory
        # client/openastroara_client and reads nothing outside client/.
        for outside in (
            "OpenAstroAra.Core/Foo.cs",
            "Directory.Build.props",
            "scripts/get-alpaca-simulators.sh",
            "packaging/debian/control",
            "some-new-dir/whatever.txt",
        ):
            with self.subTest(path=outside):
                self.assertFalse(self.c(outside)["client"], outside)

    def test_the_flutter_version_pin_reaches_the_client_jobs(self):
        p = "client/openastroara_client/.flutter-version"
        self.assertTrue(self.c(p)["client"])

    def test_a_client_change_does_not_run_the_dotnet_jobs(self):
        self.assertFalse(self.c("client/openastroara_client/lib/main.dart")["dotnet"])

    def test_the_client_lockfile_also_runs_the_dotnet_jobs(self):
        # generate-3rd-party-licenses.py reads pubspec.lock, and server-build
        # diffs its output against the committed 3rd-party-licenses.txt.
        r = self.c("client/openastroara_client/pubspec.lock")
        self.assertTrue(r["client"])
        self.assertTrue(r["dotnet"])

    def test_the_licence_generator_and_its_output_run_the_dotnet_jobs(self):
        self.assertTrue(self.c("scripts/generate-3rd-party-licenses.py")["dotnet"])
        self.assertTrue(self.c("3rd-party-licenses.txt")["dotnet"])

    # --- things that legitimately skip everything --------------------------

    def test_prose_skips_both(self):
        for p in ("design/PORT_TODO.md", "docs/x.md", ".claude/skills/y/SKILL.md",
                  "README.md", "CHANGELOG.md", "LICENSE.txt", "COPYING"):
            with self.subTest(path=p):
                r = self.c(p)
                self.assertFalse(r["dotnet"], p)
                self.assertFalse(r["client"], p)

    def test_the_repos_own_python_tooling_skips_both(self):
        for p in ("scripts/tests/test_port_driver_guards.py",
                  "scripts/check-flutter-release.py"):
            with self.subTest(path=p):
                r = self.c(p)
                self.assertFalse(r["dotnet"], p)
                self.assertFalse(r["client"], p)

    def test_another_workflow_skips_both(self):
        r = self.c(".github/workflows/check-flutter.yml")
        self.assertFalse(r["dotnet"])
        self.assertFalse(r["client"])

    def test_ci_yml_itself_runs_everything(self):
        r = self.c(".github/workflows/ci.yml")
        self.assertTrue(r["dotnet"])
        self.assertTrue(r["client"])

    # --- aggregation and fail-safe ----------------------------------------

    def test_a_mixed_diff_takes_the_union(self):
        r = self.c("design/x.md", "client/openastroara_client/lib/a.dart")
        self.assertFalse(r["docs_only"])
        self.assertTrue(r["client"])
        self.assertFalse(r["dotnet"])

    def test_one_code_file_among_many_docs_still_runs_it(self):
        paths = [f"design/{i}.md" for i in range(50)] + ["OpenAstroAra.Core/A.cs"]
        r = self.c(*paths)
        self.assertTrue(r["dotnet"])
        self.assertFalse(r["docs_only"])

    def test_an_empty_diff_runs_everything(self):
        for empty in ([], [""], ["  "]):
            with self.subTest(empty=empty):
                r = self.m.classify(empty)
                self.assertTrue(r["dotnet"])
                self.assertTrue(r["client"])
                self.assertFalse(r["docs_only"])

    def test_this_prs_own_shape_skips_the_dotnet_jobs(self):
        # The change that motivated this script: workflow + tooling + docs.
        r = self.c(
            ".github/workflows/ci.yml",
            "scripts/classify-changed-paths.py",
            "scripts/tests/test_classify_changed_paths.py",
            "design/PORT_TODO.md",
        )
        # ci.yml forces everything, which is the intended answer for a PR
        # that edits CI: it must prove the new matrix on itself.
        self.assertTrue(r["dotnet"])
        self.assertTrue(r["client"])


class DocsOnlyParityTest(unittest.TestCase):
    """`docs_only` gates REQUIRED contexts, so it must not have drifted.

    A wrong skip there is reported to GitHub as a pass and merges a broken PR
    with nothing red, which is why this behaviour is asserted separately from
    the two new buckets rather than re-expressed in terms of them.
    """

    def setUp(self):
        self.m = load_module()

    def test_the_original_rule_is_preserved(self):
        cases = {
            "design/a.md": True,
            "docs/a.md": True,
            ".claude/x": True,
            ".github/ISSUE_TEMPLATE/bug.md": True,
            "README.md": True,
            "LICENSE.txt": True,
            "COPYING": True,
            # Nested *.md is NOT docs -- the SIMULATORS_VERSION.md carve-out.
            "OpenAstroAra.Test/fixtures/SIMULATORS_VERSION.md": False,
            "client/openastroara_client/README.md": False,
            "OpenAstroAra.Core/A.cs": False,
            ".github/workflows/ci.yml": False,
            "scripts/tests/t.py": False,
        }
        for path, expected in cases.items():
            with self.subTest(path=path):
                self.assertEqual(self.m.is_docs_path(path), expected, path)

    # The shell `case` that lived inline in ci.yml before this script, kept
    # verbatim so the port can be proven rather than eyeballed. If ci.yml's
    # classifier ever changes meaning again, this is the thing to update and
    # re-run, not delete.
    LEGACY_SHELL = r"""
      docs_only=true
      while IFS= read -r f; do
        [ -z "$f" ] && continue
        case "$f" in
          design/*|docs/*|.claude/*|.github/ISSUE_TEMPLATE/*) ;;
          */*) docs_only=false; break ;;
          *.md|LICENSE.txt|COPYING) ;;
          *) docs_only=false; break ;;
        esac
      done <<EOF
$1
EOF
      printf %s "$docs_only"
    """

    def legacy_docs_only(self, paths) -> bool:
        out = subprocess.run(
            ["bash", "-c", self.LEGACY_SHELL, "bash", "\n".join(paths)],
            capture_output=True, text=True, check=True,
        ).stdout
        self.assertIn(out, ("true", "false"), f"legacy shell emitted {out!r}")
        return out == "true"

    def test_docs_only_matches_the_shell_it_replaced(self):
        """Differential: the port must not have moved a required-context gate.

        Every path below goes through both the old inline `case` and the new
        Python, and the two must agree. This is the only assertion in the
        suite that compares against the previous implementation rather than
        against intent -- because for docs_only, the previous behaviour IS
        the intent.
        """
        corpus = [
            "design/PORT_TODO.md",
            "design/nested/deep/file.md",
            "docs/a.md",
            ".claude/skills/x/SKILL.md",
            ".github/ISSUE_TEMPLATE/bug.md",
            ".github/workflows/ci.yml",
            ".github/workflows/check-flutter.yml",
            "README.md",
            "CHANGELOG.md",
            "LICENSE.txt",
            "COPYING",
            "AUTHORS",
            "Directory.Build.props",
            "global.json",
            "OpenAstroAra.Core/A.cs",
            "OpenAstroAra.Test/fixtures/SIMULATORS_VERSION.md",
            "client/openastroara_client/lib/main.dart",
            "client/openastroara_client/pubspec.lock",
            "scripts/tests/t.py",
            "scripts/get-alpaca-simulators.sh",
            "SOFA/src/x.c",
            "packaging/debian/control",
            "some-new-dir/whatever.txt",
            "NuGet.config",
        ]
        # Each path alone.
        for path in corpus:
            with self.subTest(path=path):
                self.assertEqual(
                    self.m.classify([path])["docs_only"],
                    self.legacy_docs_only([path]),
                    path,
                )
        # And every pair, since docs_only is an AND across the diff and the
        # legacy loop short-circuits on its first non-docs hit.
        import itertools

        for a, b in itertools.combinations(corpus, 2):
            with self.subTest(pair=(a, b)):
                self.assertEqual(
                    self.m.classify([a, b])["docs_only"],
                    self.legacy_docs_only([a, b]),
                    f"{a} + {b}",
                )

    def test_docs_only_needs_every_path_to_be_docs(self):
        self.assertTrue(self.m.classify(["design/a.md", "README.md"])["docs_only"])
        self.assertFalse(
            self.m.classify(["design/a.md", "OpenAstroAra.Core/A.cs"])["docs_only"]
        )


class CliTest(unittest.TestCase):
    def run_cli(self, stdin: str):
        return subprocess.run(
            ["python3", str(SCRIPT)],
            input=stdin,
            capture_output=True,
            text=True,
            check=True,
        ).stdout

    def test_output_is_github_output_shaped(self):
        out = self.run_cli("OpenAstroAra.Core/A.cs\n")
        self.assertEqual(
            sorted(out.split()), ["client=false", "docs_only=false", "dotnet=true"]
        )

    def test_values_are_only_ever_the_two_literals(self):
        out = self.run_cli("design/a.md\nclient/openastroara_client/a.dart\n")
        for line in out.split():
            self.assertRegex(line, r"^[a-z_]+=(true|false)$")


class WorkflowWiringTest(unittest.TestCase):
    """The outputs are useless if ci.yml does not consume them correctly."""

    @classmethod
    def setUpClass(cls):
        cls.text = CI.read_text()

    def test_the_changes_job_calls_this_script(self):
        self.assertIn("scripts/classify-changed-paths.py", self.text)

    def test_the_changes_job_exposes_every_output(self):
        for name in ("docs_only", "dotnet", "client"):
            with self.subTest(output=name):
                self.assertIn(
                    f"{name}: ${{{{ steps.classify.outputs.{name} }}}}", self.text
                )

    def test_the_gated_jobs_fail_safe_on_an_empty_output(self):
        # `!= 'false'` and never `== 'true'`: if the changes job dies, the
        # output is the empty string and the job must still run.
        for job in ("dotnet", "client"):
            needle = f"needs.changes.outputs.{job} != 'false'"
            with self.subTest(job=job):
                self.assertIn(needle, self.text)
        self.assertNotIn("needs.changes.outputs.dotnet == 'true'", self.text)
        self.assertNotIn("needs.changes.outputs.client == 'true'", self.text)

    def test_required_contexts_are_not_gated_on_the_new_buckets(self):
        """The whole safety argument for this change.

        server-build, registry-gate and the three client-test legs are
        REQUIRED contexts, where a skip counts as a pass. They stay on the
        original docs_only gate; only non-required jobs get the new buckets.
        """
        import re

        required_job_names = (
            "Server (build + cross-publish + Docker)",
            "Settings + Help registry gate",
            "Client (analyze + test)",
        )
        blocks = re.split(r"\n  (?=[a-z][a-z0-9-]*:\n)", self.text)
        for block in blocks:
            if not any(f"name: {n}" in block for n in required_job_names):
                continue
            for bucket in ("dotnet", "client"):
                self.assertNotIn(
                    f"needs.changes.outputs.{bucket}",
                    block,
                    f"a required context is gated on the new `{bucket}` bucket",
                )


if __name__ == "__main__":
    unittest.main()
