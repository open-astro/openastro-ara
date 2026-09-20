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
CODEQL = REPO_ROOT / ".github" / "workflows" / "codeql.yml"


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

    def test_editorconfig_runs_the_analyzer_gate(self):
        # .editorconfig carries the dotnet_diagnostic.*.severity lines that
        # decide whether `dotnet build -c Release` under TreatWarningsAsErrors
        # passes. A PR that only flips CA1854 from suggestion to error must
        # run the analyzer, and server-build does not cover it (it compiles
        # Server+Fits only). Regression pinned from #1035's review.
        self.assertTrue(self.c(".editorconfig")["dotnet"])
        self.assertIn(".editorconfig", self.m.DOTNET_ROOT_FILES)
        self.assertNotIn(".editorconfig", self.m.INERT_ROOT_FILES)

    def test_uncheckable_root_files_run_everything(self):
        # No test can show these cannot affect a build, so they are not
        # claimed to -- for EITHER bucket. `.gitattributes` with
        # `client/** text eol=lf` rewrites client bytes at checkout, so the
        # closed-over-directories argument for `client` does not reach root
        # files.
        for f in (".gitattributes", ".gitignore", "CodeMaid.config", "md-template.html", "NuGet.config"):
            with self.subTest(f=f):
                r = self.c(f)
                self.assertTrue(r["dotnet"], f)
                self.assertTrue(r["client"], f)
                self.assertNotIn(f, self.m.INERT_ROOT_FILES)

    def test_only_client_and_root_files_reach_the_client_jobs(self):
        # Directories other than client/ never do; that is the checkable half
        # of the closed-bucket claim.
        for d in ("OpenAstroAra.Core/A.cs", "SOFA/x.c", "packaging/x", "brand-new-dir/x", "scripts/new-tool.sh"):
            with self.subTest(path=d):
                self.assertFalse(self.c(d)["client"], d)

    def test_an_unclassified_path_is_named_on_stderr(self):
        # The inline shell logged `non-docs path: $f`; a surprising skip
        # or run should still say which path caused it.
        out = subprocess.run(
            ["python3", str(SCRIPT)], input="brand-new-dir/x\nNuGet.config\n",
            capture_output=True, text=True, check=True,
        )
        self.assertIn("brand-new-dir/x", out.stderr)
        self.assertIn("NuGet.config", out.stderr)
        self.assertNotIn("brand-new-dir", out.stdout, "stderr chatter must not reach GITHUB_OUTPUT")

    def test_a_composite_action_under_github_runs_the_dotnet_jobs(self):
        # No suffix catch-all under .github/: a .yml there is not inert by
        # virtue of being YAML.
        self.assertTrue(self.c(".github/actions/setup/action.yml")["dotnet"])
        self.assertTrue(self.c(".github/some-new-thing.yml")["dotnet"])

    def test_every_sibling_workflow_on_disk_is_classified_explicitly(self):
        # The allowlist must track the directory: a new workflow file is
        # either added to GITHUB_RULES on purpose or runs the full dotnet set.
        on_disk = sorted(
            f"{p.relative_to(REPO_ROOT)}"
            for p in (REPO_ROOT / ".github" / "workflows").iterdir()
            if p.suffix in (".yml", ".yaml")
        )
        for wf in on_disk:
            with self.subTest(workflow=wf):
                self.assertIn(wf, self.m.GITHUB_RULES, f"{wf} is not classified")

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
            "Directory.Build.props",  # listed in DOTNET_ROOT_FILES, so not the open root fallback
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

    def test_the_client_lockfile_stays_client_only(self):
        # generate-3rd-party-licenses.py reads pubspec.lock and server-build
        # diffs its output -- but server-build is gated on docs_only and runs
        # for this path anyway, while no `dotnet`-gated job reads client/.
        # Every Dependabot pub bump is exactly this diff; it must not pay
        # ~1400s for the analyzer and both Alpaca jobs.
        r = self.c("client/openastroara_client/pubspec.lock")
        self.assertTrue(r["client"])
        self.assertFalse(r["dotnet"])
        # ...and the reason that is safe: server-build is not on `dotnet`.
        ci = CI.read_text()
        block = ci.split("\n  server-build:\n", 1)[1].split("\n  registry-gate:\n", 1)[0]
        self.assertNotIn("needs.changes.outputs.dotnet", block)

    def test_the_licence_generator_and_its_output_run_the_dotnet_jobs(self):
        self.assertTrue(self.c("scripts/generate-3rd-party-licenses.py")["dotnet"])
        self.assertTrue(self.c("3rd-party-licenses.txt")["dotnet"])

    def test_a_git_quoted_path_falls_through_to_code(self):
        # With core.quotePath at its default, git emits a non-ASCII path as
        # "design/\303\251.md" -- leading double quote included. That string
        # matches no prefix rule, so it must land on the open default (a lost
        # saving, never a lost job). ci.yml passes -c core.quotePath=false so
        # the classifier sees the real path instead; see the wiring test.
        r = self.c('"design/\\303\\251.md"')
        self.assertFalse(r["docs_only"])
        self.assertTrue(r["dotnet"])
        self.assertTrue(self.c("design/\u00e9.md")["docs_only"])

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

    def test_only_these_paths_may_skip_the_non_required_jobs_alone(self):
        """The state docs_only=false, dotnet=false, client=false is the one
        this PR introduced: the required contexts run, the four non-required
        ones skip. Every path that can produce it is enumerated here, so a
        classifier edit that widens the set has to add to this list and say
        why. #1035's .editorconfig defect was exactly this state, unlisted.
        """
        allowed = {
            "AUTHORS",
            ".github/workflows/check-flutter.yml",
            ".github/workflows/claude-review.yml",
            ".github/workflows/codeql.yml",
            ".github/workflows/label-trusted-authors.yml",
            ".github/dependabot.yml",
            ".github/FUNDING.yml",
            ".github/pull_request_template.md",
            ".github/scripts/check-unicode.py",
            "scripts/check-flutter-release.py",
            "scripts/check-settings-registry.mjs",
            "scripts/check-help-registry.mjs",
            "scripts/fit-star-count-model.py",
            "scripts/tests/<anything>",
        }
        probe = sorted(
            set(self.m.GITHUB_RULES)
            | set(self.m.SCRIPT_RULES)
            | self.m.INERT_ROOT_FILES
            | self.m.DOTNET_ROOT_FILES
            | {
                "scripts/tests/test_x.py",
                ".editorconfig", ".gitattributes", ".gitignore",
                "CodeMaid.config", "md-template.html", "NuGet.config",
                ".github/CODEOWNERS", ".github/actions/x/action.yml",
                "OpenAstroAra.Core/A.cs", "SOFA/x.c", "packaging/x",
                "client/openastroara_client/a.dart",
                "client/openastroara_client/pubspec.lock",
                "design/a.md", "README.md", "scripts/new-tool.sh",
            }
        )
        for path in probe:
            r = self.c(path)
            neither = not r["docs_only"] and not r["dotnet"] and not r["client"]
            key = "scripts/tests/<anything>" if path.startswith("scripts/tests/") else path
            with self.subTest(path=path):
                if neither:
                    self.assertIn(key, allowed, f"{path} skips every gated job but is not on the allowed list")
                else:
                    self.assertNotIn(key, allowed, f"{path} is listed as skip-all but no longer is")

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
    # The shell `case` that lived inline in ci.yml before this script, kept
    # verbatim so the port can be proven rather than eyeballed. If ci.yml's
    # classifier ever changes meaning again, this is the thing to update and
    # re-run, not delete. Wrapped as a function so ONE bash process can
    # evaluate every case: the corpus is O(n^2) in pairs, and one subprocess
    # per case turned 300 cases into 300 forks.
    LEGACY_SHELL = r"""
      legacy() {
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
        printf '%s\n' "$docs_only"
      }
      # One case per line on stdin, paths within a case separated by '|'.
      while IFS= read -r line; do
        legacy "$(printf '%s' "$line" | tr '|' '\n')"
      done
    """

    def legacy_docs_only(self, cases):
        """Evaluate many cases in one bash process; returns a list of bools."""
        for case in cases:
            for path in case:
                self.assertNotIn("|", path, "corpus paths must not contain the separator")
        stdin = "\n".join("|".join(case) for case in cases) + "\n"
        out = subprocess.run(
            ["bash", "-c", self.LEGACY_SHELL],
            input=stdin, capture_output=True, text=True, check=True,
        ).stdout.split("\n")
        out = [o for o in out if o]
        self.assertEqual(len(out), len(cases), "legacy shell emitted the wrong number of results")
        for o in out:
            self.assertIn(o, ("true", "false"), f"legacy shell emitted {o!r}")
        return [o == "true" for o in out]

    def test_docs_only_matches_the_shell_it_replaced(self):
        """Differential: the port must not have moved a required-context gate.

        Every path below goes through both the old inline `case` and the new
        Python, and the two must agree. This is the only assertion in the
        suite that compares against the previous implementation rather than
        against intent -- because for docs_only, the previous behaviour IS
        the intent.
        """
        import itertools

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
            ".editorconfig",
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
        # Each path alone, then every pair: docs_only is an AND across the
        # diff and the legacy loop short-circuits on its first non-docs hit.
        cases = [[p] for p in corpus] + [list(pr) for pr in itertools.combinations(corpus, 2)]
        legacy = self.legacy_docs_only(cases)
        for case, expected in zip(cases, legacy):
            with self.subTest(case=case):
                self.assertEqual(self.m.classify(case)["docs_only"], expected, " + ".join(case))

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

    def test_the_changes_job_is_continue_on_error_at_step_level(self):
        # #1024: a checkout flake or the 5-minute timeout is outside the
        # script's ERR trap. Every consumer fails safe on an empty output, so
        # the job must not fail the run either -- a red `Changed paths` only
        # hands the autonomous driver a `fail` to churn on.
        block = self.text.split("\n  changes:\n", 1)[1].split("\n  alpaca-sim-smoke:\n", 1)[0]
        # STEP level on both steps, never JOB level. Measured on probe PR
        # #1043: job-level `continue-on-error` still publishes the job's
        # check run as `failure`, so `gh pr checks` reports `fail` and the
        # driver churns; step-level publishes `success` with an annotation.
        self.assertNotRegex(block, r"\n    continue-on-error: true")
        checkout = block.split("- name: Checkout", 1)[1].split("- name: Classify", 1)[0]
        classify = block.split("- name: Classify", 1)[1]
        self.assertRegex(checkout, r"\n        continue-on-error: true")
        self.assertRegex(classify, r"\n        continue-on-error: true")
        # Each step's own timeout must sit under the job's, or a hang fails
        # the job instead of the (continued) step. The job timeout is read
        # from the job header (between `name: Changed paths` and `steps:`),
        # not "the first 4-space timeout in the block", so a reorder cannot
        # pick a different key.
        import re
        header = block.split("name: Changed paths", 1)[1].split("\n    steps:", 1)[0]
        job_t = int(re.search(r"\n    timeout-minutes: (\d+)", header).group(1))
        for name, step in (("checkout", checkout), ("classify", classify)):
            with self.subTest(step=name):
                m = re.search(r"\n        timeout-minutes: (\d+)", step)
                self.assertIsNotNone(m, f"{name} step has no timeout-minutes")
                self.assertLess(int(m.group(1)), job_t)

    def test_the_diff_disables_path_quoting(self):
        # #1024: a quoted non-ASCII path misses every prefix rule (see
        # test_a_git_quoted_path_falls_through_to_code). Safe, but wasteful,
        # and exact is cheap.
        needle = "git -c core.quotePath=false diff --no-renames --name-only"
        self.assertIn(needle, self.text)
        # The two skill recipes reproduce this classification to attribute a
        # skip; with default quoting a non-ASCII path would classify as code
        # locally while CI skipped, and the driver would Hold on a phantom.
        for recipe in (
            REPO_ROOT / ".claude" / "commands" / "pr-checker.md",
            REPO_ROOT / ".claude" / "skills" / "port-driver" / "SKILL.md",
        ):
            with self.subTest(recipe=recipe.name):
                text = recipe.read_text()
                self.assertIn(needle, text)
                # Only the fenced recipe blocks are held to this; a prose
                # mention of the old form elsewhere in the file is fine.
                fences = [
                    f for f in text.split("```")[1::2]
                    if "classify-changed-paths.py" in f
                ]
                self.assertTrue(fences, "no fenced recipe block found")
                for fence in fences:
                    self.assertNotIn("git diff --no-renames --name-only", fence)


class InertTreesTest(unittest.TestCase):
    """INERT_DIRS is a claim about the repo, so check the repo (#1024 item 2).

    `design/*`, `docs/*`, `.claude/*` are prefix-allowlisted as "no build
    input of any kind". The first .py, .json or fixture added under one of
    them would silently lose the full matrix; this fails the Sanity job at
    that moment instead. Add an extension here only with a reason it cannot
    be a build input.
    """

    PROSE_OR_IMAGE = {
        ".md",
        ".png", ".jpg", ".jpeg", ".gif", ".svg", ".webp",
    }
    # Per-prefix additions (#1044). Only the prefix named gets the extra
    # extension, so `design/` and `docs/` stay prose-only however this grows.
    # `.github/ISSUE_TEMPLATE/` may hold `.yml`: GitHub's issue-chooser
    # `config.yml` and issue-form templates are read by nothing but
    # github.com, and nothing under that directory can be a workflow or a
    # composite action (those live in workflows/ and actions/).
    EXTRA_BY_PREFIX = {
        ".github/ISSUE_TEMPLATE/": {".yml", ".yaml"},
    }

    @classmethod
    def allowed(cls, path: str) -> set:
        exts = set(cls.PROSE_OR_IMAGE)
        for prefix, extra in cls.EXTRA_BY_PREFIX.items():
            if path.startswith(prefix):
                exts |= extra
        return exts

    @classmethod
    def setUpClass(cls):
        cls.m = load_module()
        out = subprocess.run(
            ["git", "-c", "core.quotePath=false", "ls-files", "-z", "--", *cls.m.INERT_DIRS],
            cwd=REPO_ROOT, check=True, capture_output=True, text=True,
        ).stdout
        cls.files = [f for f in out.split("\0") if f]

    def test_every_inert_dir_is_tracked_and_non_empty(self):
        for prefix in self.m.INERT_DIRS:
            with self.subTest(prefix=prefix):
                self.assertTrue(any(f.startswith(prefix) for f in self.files), f"{prefix} has no tracked files")

    def test_inert_trees_hold_only_prose_or_images(self):
        offenders = sorted(
            f for f in self.files
            if Path(f).suffix.lower() not in self.allowed(f)
        )
        self.assertEqual(
            offenders, [],
            "a non-prose file under an INERT_DIRS prefix would be skipped by CI. "
            "Either classify it in scripts/classify-changed-paths.py (drop the prefix "
            "from INERT_DIRS, or add a rule for the path), or -- if the extension "
            "provably cannot be a build input -- add it to PROSE_OR_IMAGE (or to "
            "EXTRA_BY_PREFIX for one directory only) with a reason",
        )

    def test_every_extra_by_prefix_key_is_an_inert_dir(self):
        # A per-prefix allowance for a directory the classifier does not
        # treat as inert would be dead text at best and misleading at worst.
        for prefix in self.EXTRA_BY_PREFIX:
            with self.subTest(prefix=prefix):
                self.assertIn(prefix, self.m.INERT_DIRS)

    def test_the_per_prefix_allowance_does_not_leak(self):
        # `.yml` is allowed under ISSUE_TEMPLATE/ only; the same name under
        # design/ or docs/ would be a build input nobody classified.
        self.assertIn(".yml", self.allowed(".github/ISSUE_TEMPLATE/config.yml"))
        self.assertNotIn(".yml", self.allowed("design/something.yml"))
        self.assertNotIn(".yml", self.allowed("docs/something.yml"))
        self.assertNotIn(".yml", self.allowed(".claude/settings.yml"))

    def test_codeql_paths_ignore_mirrors_inert_dirs(self):
        # #1022: codeql.yml skips prose-only PRs with a workflow-level
        # paths-ignore (safe there: it is not a required context). Its list
        # must not drift from the classifier's idea of "inert".
        # Parsed, not string-split, so the order of the `on:` keys is not
        # part of what is pinned (#1044). PyYAML ships on the ubuntu runner
        # image the Sanity job uses; a missing module is a loud failure here,
        # never a silent skip.
        import yaml  # noqa: PLC0415 - fail loudly if the runner lacks it

        doc = yaml.safe_load(CODEQL.read_text())
        # YAML 1.1 reads a bare `on` as boolean True; PyYAML does exactly that.
        on = doc.get("on", doc.get(True))
        self.assertIsNotNone(on, "no `on:` block in codeql.yml")
        ignore = (on.get("pull_request") or {}).get("paths-ignore") or []
        for prefix in self.m.INERT_DIRS:
            with self.subTest(prefix=prefix):
                self.assertIn(f"{prefix}**", ignore)
        # Wider than is_docs_path on purpose: a nested *.md (the Alpaca
        # SIMULATORS_VERSION.md fixture) is a ci.yml input but never a C#
        # CodeQL input, so codeql.yml may ignore every *.md.
        self.assertIn("**.md", ignore)
        # Only pull_request is filtered: default-branch alerts come from push
        # and the weekly schedule.
        for trigger in ("push", "schedule"):
            with self.subTest(trigger=trigger):
                cfg = on.get(trigger)
                if isinstance(cfg, dict):
                    self.assertNotIn("paths", cfg)
                    self.assertNotIn("paths-ignore", cfg)


if __name__ == "__main__":
    unittest.main()
