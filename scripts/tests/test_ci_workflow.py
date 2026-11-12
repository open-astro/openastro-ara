"""The run-time shape of .github/workflows/ci.yml.

Nothing runs a workflow before it is pushed, so these pin the parts a wrong edit
would break without any job going red: a shard that no runner takes, a required
context that passes with a failed shard behind it, a cache that never hits.
"""

import re
import subprocess
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
        self.assertIn('flutter test "${files[@]}"', block)
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

    def test_the_macos_and_windows_shards_take_no_runner_on_a_pull_request(self):
        block = job(self.text, "client-test-desktop-shard")
        self.assertIn("        os: [macos-latest, windows-latest]\n", block)
        self.assertIn("    runs-on: ${{ matrix.os }}\n", block)
        self.assertIn(
            "    if: ${{ !cancelled() && needs.changes.outputs.docs_only != 'true'"
            " && github.event_name != 'pull_request' }}\n",
            block,
        )

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
            ("skipped", "true", "merge_group", True),
            ("skipped", "false", "pull_request", True),
            ("skipped", "false", "merge_group", False),
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


if __name__ == "__main__":
    unittest.main()
