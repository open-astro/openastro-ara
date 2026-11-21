"""The run-time shape of .github/workflows/ci.yml.

Nothing runs a workflow before it is pushed, so these pin the parts a wrong edit
would break without any job going red: a shard that no runner takes, a required
context that passes with a failed shard behind it, a cache that never hits.
"""

import re
import subprocess
import sys
import tempfile
import textwrap
import unittest
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
CI = REPO_ROOT / ".github" / "workflows" / "ci.yml"


def job(text: str, job_id: str) -> str:
    """The text of one job, from its id to the next job."""
    parts = text.split(f"\n  {job_id}:\n", 1)
    if len(parts) != 2:
        raise AssertionError(f"ci.yml has no {job_id} job")
    return re.split(r"\n  [A-Za-z0-9_-]+:\n", parts[1], maxsplit=1)[0]


def steps(block: str) -> dict:
    """Step name -> step text."""
    out = {}
    for step in block.split("\n      - name: ")[1:]:
        name, _, body = step.partition("\n")
        out[name] = body
    return out


class CiRunTimeTest(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.text = CI.read_text()

    def test_a_new_push_cancels_the_run_it_replaces_but_never_a_merge_group(self):
        self.assertIn(
            "\nconcurrency:\n"
            "  group: ci-${{ github.workflow }}-${{ github.ref }}\n"
            "  cancel-in-progress: ${{ github.event_name != 'merge_group' }}\n",
            self.text,
        )

    def test_unicode_and_zizmor_run_in_the_sanity_job(self):
        sanity = job(self.text, "sanity")
        self.assertIn("python3 .github/scripts/check-unicode.py", sanity)
        self.assertIn("zizmor --offline .github/workflows/", sanity)
        for old in ("unicode", "zizmor"):
            self.assertNotIn(f"\n  {old}:\n", self.text, f"the {old} job still takes its own runner")

    def test_the_test_shards_run_every_test_file_once(self):
        for job_id in ("client-test-shard", "client-test-desktop-shard"):
            with self.subTest(job=job_id):
                self.assert_every_test_file_runs_once(job(self.text, job_id), job_id)

    def test_the_windows_shards_batch_their_files_under_the_cmd_exe_limit(self):
        # flutter is flutter.bat on Windows, and cmd.exe refuses a line over
        # 8191 chars: a 166-file shard did, in the merge queue (#1851).
        batch = re.search(r'\| xargs -0 -n (\d+) flutter test\n', job(self.text, "client-test-desktop-shard"))
        self.assertIsNotNone(batch, "the Windows/macOS shards pass the whole file list to one flutter test")
        self.assertLessEqual(int(batch[1]) * 100, 8191 - 1000, "a batch of 100-char paths would not fit cmd.exe's line")

    def assert_every_test_file_runs_once(self, block, job_id):
        shards = re.search(r"\n        shard: \[([0-9, ]+)\]\n", block)
        split = re.search(r"\| (awk -v n=(\d+) -v s=\"\$SHARD\" '[^']+')\)\n", block)
        self.assertIsNotNone(shards, f"{job_id} has no shard matrix")
        self.assertIsNotNone(split, f"{job_id} does not split the test files by shard")
        n = int(split[2])
        matrix = [int(s) for s in shards[1].split(",")]
        self.assertEqual(matrix, list(range(1, n + 1)))
        self.assertIn("SHARD: ${{ matrix.shard }}", block)
        self.assertIn(f"shard ${{{{ matrix.shard }}}}/{n}\n", block)
        self.assertRegex(
            block,
            r'\n {10}(flutter test "\$\{files\[@\]\}"|printf \'%s\\0\' "\$\{files\[@\]\}" \| xargs -0 -n \d+ flutter test)\n',
            f"{job_id} does not hand every shard file to flutter test",
        )
        # Run the workflow's own split over a file list: every file lands in exactly one shard.
        files = [f"test/f{i:02d}_test.dart" for i in range(10)]
        taken = []
        for shard in matrix:
            run = subprocess.run(
                ["bash", "-c", split[1]],
                env={"PATH": "/usr/bin:/bin", "SHARD": str(shard)},
                input="\n".join(files) + "\n",
                capture_output=True,
                text=True,
                check=True,
            )
            taken += run.stdout.split()
        self.assertEqual(sorted(taken), files)

    def test_the_desktop_test_step_runs_under_the_macos_bash(self):
        # macos-latest runs `shell: bash` with /bin/bash 3.2, which has no mapfile or readarray.
        script = steps(job(self.text, "client-test-desktop-shard"))["flutter test"].split("run: |\n", 1)[1]
        script = textwrap.dedent(script)
        self.assertNotRegex(script, r"(?m)^\s*(mapfile|readarray)\b")
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / "test").mkdir()
            files = [f"test/f{i}_test.dart" for i in range(4)]
            for f in files:
                (root / f).touch()
            (root / "bin").mkdir()
            flutter = root / "bin" / "flutter"
            flutter.write_text('#!/bin/sh\nshift\nprintf "%s\\n" "$@"\n')
            flutter.chmod(0o755)
            taken = []
            for shard in (1, 2, 3):
                run = subprocess.run(
                    ["bash", "-c", script],
                    cwd=root,
                    env={"PATH": f"{root / 'bin'}:/usr/bin:/bin", "SHARD": str(shard)},
                    capture_output=True,
                    text=True,
                )
                self.assertEqual(run.returncode, 0, run.stderr)
                taken += run.stdout.splitlines()[1:]  # after "shard N: M test files"
            self.assertEqual(sorted(taken), files)

    def test_the_ubuntu_shards_skip_only_a_docs_only_change(self):
        block = job(self.text, "client-test-shard")
        # A required context must not sit on the finer bucket (see WorkflowWiringTest).
        self.assertIn("    if: ${{ !cancelled() && needs.changes.outputs.docs_only != 'true' }}\n", block)

    def test_the_ubuntu_context_passes_only_when_every_shard_passed(self):
        block = job(self.text, "client-test-linux")
        self.assertIn("    name: Client (analyze + test) — ubuntu-latest\n", block)
        self.assertIn("    needs: [changes, client-test-shard]\n", block)
        # Without it a failed shard skips this job, and a skipped required context counts as passed.
        self.assertIn("    if: ${{ !cancelled() }}\n", block)
        step = steps(block)["Require every shard to pass"]
        self.assertIn("SHARDS: ${{ needs.client-test-shard.result }}", step)
        self.assertIn("DOCS_ONLY: ${{ needs.changes.outputs.docs_only }}", step)
        script = textwrap.dedent(step.split("        run: |\n", 1)[1])
        for shards, docs_only, passes in (
            ("success", "false", True),
            ("success", "", True),
            ("skipped", "true", True),
            ("skipped", "false", False),
            ("skipped", "", False),
            ("failure", "false", False),
            ("failure", "true", False),
            ("cancelled", "true", False),
        ):
            with self.subTest(shards=shards, docs_only=docs_only):
                run = subprocess.run(
                    ["bash", "-c", script],
                    env={"PATH": "/usr/bin:/bin", "SHARDS": shards, "DOCS_ONLY": docs_only},
                    capture_output=True,
                )
                self.assertEqual(run.returncode == 0, passes, run.stdout.decode())

    def test_the_macos_and_windows_shards_run_on_the_pull_request_not_in_the_merge_queue(self):
        # merge-gate.sh requires every check green on the PR head, so they still gate the merge; in the
        # queue they held the scarce macOS runners and set the length of every queue run (2026-11-14).
        block = job(self.text, "client-test-desktop-shard")
        self.assertIn("        os: [macos-latest, windows-latest]\n", block)
        self.assertIn("    runs-on: ${{ matrix.os }}\n", block)
        self.assertIn(
            "    if: ${{ !cancelled() && needs.changes.outputs.docs_only != 'true'"
            " && github.event_name != 'merge_group' }}\n",
            block,
        )

    def test_only_the_linux_shard_runs_flutter_analyze(self):
        # The analysis is the same on every host; the desktop shard 1 ran it again for ~40 s.
        self.assertIn("flutter analyze", steps(job(self.text, "client-test-shard")))
        self.assertNotIn("flutter analyze", steps(job(self.text, "client-test-desktop-shard")))

    def test_the_macos_and_windows_contexts_pass_only_when_every_shard_passed(self):
        block = job(self.text, "client-test")
        self.assertIn("    name: Client (analyze + test) — ${{ matrix.os }}\n", block)
        self.assertIn("        os: [macos-latest, windows-latest]\n", block)
        self.assertIn("    needs: [changes, client-test-desktop-shard]\n", block)
        # Without it a failed shard skips this job, and a skipped required context counts as passed.
        self.assertIn("    if: ${{ !cancelled() }}\n", block)
        # Only reports a result, so no macOS or Windows runner.
        self.assertIn("    runs-on: ubuntu-latest\n", block)
        step = steps(block)["Require every shard to pass"]
        self.assertIn("SHARDS: ${{ needs.client-test-desktop-shard.result }}", step)
        self.assertIn("DOCS_ONLY: ${{ needs.changes.outputs.docs_only }}", step)
        self.assertIn("EVENT_NAME: ${{ github.event_name }}", step)
        script = textwrap.dedent(step.split("        run: |\n", 1)[1])
        for shards, docs_only, event, passes in (
            ("success", "false", "merge_group", True),
            ("success", "", "push", True),
            ("success", "false", "pull_request", True),
            ("skipped", "true", "pull_request", True),
            ("skipped", "false", "merge_group", True),
            ("skipped", "false", "pull_request", False),
            ("skipped", "", "push", False),
            ("failure", "false", "merge_group", False),
            ("failure", "true", "pull_request", False),
            ("cancelled", "true", "merge_group", False),
        ):
            with self.subTest(shards=shards, docs_only=docs_only, event=event):
                run = subprocess.run(
                    ["bash", "-c", script],
                    env={"PATH": "/usr/bin:/bin", "SHARDS": shards, "DOCS_ONLY": docs_only, "EVENT_NAME": event},
                    capture_output=True,
                )
                self.assertEqual(run.returncode == 0, passes, run.stdout.decode())

    def test_the_native_builds_skip_the_merge_queue(self):
        # merge-gate.sh already requires them green on the PR head; in the queue they only held macOS runners.
        block = job(self.text, "client-build")
        self.assertIn(
            "    if: ${{ !cancelled() && github.event_name != 'merge_group'"
            " && needs.changes.outputs.docs_only != 'true' && needs.changes.outputs.client != 'false' }}\n",
            block,
        )

    def test_the_astap_build_is_skipped_on_a_cache_hit(self):
        server = steps(job(self.text, "server-build"))
        cache = server["Cache astap_cli (linux-arm64)"]
        self.assertIn("id: astap-cache\n", cache)
        self.assertIn("path: publish/arm64/astap_cli\n", cache)
        # Both inputs of the binary: the solver source and the recipe.
        self.assertIn(
            "key: astap-cli-arm64-${{ steps.astap-rev.outputs.sha }}-${{ hashFiles('scripts/build-astap-cli.sh') }}\n",
            cache,
        )
        self.assertIn('git rev-parse HEAD:third_party/astap', server["Read the ASTAP submodule commit"])
        self.assertIn(
            "if: steps.astap-cache.outputs.cache-hit != 'true'\n",
            server["Build astap_cli (ASTAP solver) for linux-arm64"],
        )
        names = list(server)
        self.assertLess(
            names.index("dotnet publish (linux-arm64, self-contained, non-AOT)"),
            names.index("Cache astap_cli (linux-arm64)"),
            "the restore must land in the publish directory after dotnet publish writes it",
        )


class ReleaseLineCiTest(unittest.TestCase):
    """#2093: CI on the release branches, and the .deb version of a build."""

    @classmethod
    def setUpClass(cls):
        cls.text = CI.read_text()

    def test_ci_runs_on_pushes_to_master_and_every_stable_branch(self):
        push = self.text.split("\n  push:\n", 1)[1].split("\n  workflow_dispatch:", 1)[0]
        self.assertIn("\n      - master\n", push + "\n")
        self.assertIn("\n      - 'stable/**'", push)

    def test_the_sanity_job_checks_the_version_file(self):
        self.assertIn("python3 scripts/version.py --check", steps(job(self.text, "sanity"))["Version file"])

    def deb_version(self, version_file, tags=(), branch="master", release_tag_script=None, expect_fail=False):
        """Run the workflow's .deb version step in a throwaway repository; return its version output."""
        step = steps(job(self.text, "server-build"))["Compute .deb version"]
        self.assertIn("BUILD_BRANCH: ${{ github.base_ref || github.event.merge_group.base_ref || github.ref_name }}", step)
        script = textwrap.dedent(step.split("        run: |\n", 1)[1])
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / "scripts").mkdir()
            for name in ("version.py", "release-tag.py"):
                (root / "scripts" / name).write_text((REPO_ROOT / "scripts" / name).read_text())
            if release_tag_script is not None:
                (root / "scripts" / "release-tag.py").write_text(release_tag_script)
            # The nightly rule has its own tests (test_nightly_version.py); here only the hand-off.
            (root / "scripts" / "nightly-version.py").write_text(
                "import sys\nprint('nightly ' + ' '.join(sys.argv[1:]))\n"
            )
            (root / "VERSION").write_text(version_file + "\n")
            bin_dir = root / "bin"
            bin_dir.mkdir()
            (bin_dir / "python3").symlink_to(sys.executable)
            env = {
                "PATH": f"{bin_dir}:/usr/bin:/bin",
                "HOME": tmp,
                "GIT_CONFIG_GLOBAL": "/dev/null",
                "GIT_CONFIG_NOSYSTEM": "1",
                "GIT_AUTHOR_NAME": "t", "GIT_AUTHOR_EMAIL": "t@t",
                "GIT_COMMITTER_NAME": "t", "GIT_COMMITTER_EMAIL": "t@t",
            }
            for cmd in (["init", "-q"], ["add", "-A"], ["commit", "-q", "-m", "c"]):
                subprocess.run(["git", *cmd], cwd=root, env=env, check=True)
            for tag in tags:
                subprocess.run(["git", "tag", "-a", tag, "-m", tag], cwd=root, env=env, check=True)
            out = root / "github-output"
            run = subprocess.run(
                ["bash", "-c", script],
                cwd=root,
                env={**env, "GITHUB_OUTPUT": str(out), "BUILD_BRANCH": branch},
                capture_output=True,
                text=True,
            )
            if expect_fail:
                self.assertNotEqual(run.returncode, 0, run.stdout)
                return run.stdout + run.stderr
            self.assertEqual(run.returncode, 0, run.stderr)
            lines = [ln for ln in out.read_text().splitlines() if ln.startswith("version=")]
            self.assertEqual(len(lines), 1, out.read_text())
            return lines[0][len("version="):]

    def test_a_release_tag_on_head_builds_the_release_version(self):
        self.assertEqual(self.deb_version("1.0.0~beta1", tags=["v1.0.0-beta.1"]), "1.0.0~beta1")
        self.assertEqual(self.deb_version("1.0.0", tags=["phase-10.5-complete", "v1.0.0"]), "1.0.0")

    def test_a_tag_whose_version_file_disagrees_builds_a_nightly(self):
        # The NINA fork's inherited v1.10.1 looks like a release tag but holds no matching VERSION.
        self.assertEqual(self.deb_version("0.0.1", tags=["v1.10.1"]), "nightly --branch master")

    def test_a_tag_that_is_no_ara_release_shape_builds_a_nightly(self):
        # NINA's v.1.8 is refused by release-tag.py with exit 2, which only means "not ours".
        self.assertEqual(self.deb_version("0.0.1", tags=["v.1.8"]), "nightly --branch master")

    def test_a_crashing_release_tag_script_fails_the_step(self):
        out = self.deb_version("1.0.0", tags=["v1.0.0"], expect_fail=True,
                               release_tag_script="import sys\nsys.exit(1)\n")
        self.assertIn("release-tag.py failed on tag v1.0.0", out)

    def test_a_merge_group_into_a_stable_branch_stays_on_that_line(self):
        # A merge group's base arrives as refs/heads/stable/1.0; ref_name would be the queue branch.
        self.assertEqual(self.deb_version("1.0.0~beta1", branch="refs/heads/stable/1.0"),
                         "nightly --branch stable/1.0")

    def test_an_untagged_build_passes_its_branch_to_the_nightly_version(self):
        self.assertEqual(self.deb_version("1.0.0~beta2", branch="stable/1.0"), "nightly --branch stable/1.0")


if __name__ == "__main__":
    unittest.main()
