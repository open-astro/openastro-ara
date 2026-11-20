"""The checks in .github/workflows/release.yml (#2093).

Nothing runs a release workflow before a tag is pushed, and a wrong check there
publishes a Release nobody can take back quietly. These run the workflow's own
shell steps against throwaway repositories and files.
"""

import re
import subprocess
import sys
import tempfile
import textwrap
import unittest
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
RELEASE = REPO_ROOT / ".github" / "workflows" / "release.yml"

GIT_ENV = {
    "GIT_CONFIG_GLOBAL": "/dev/null",
    "GIT_CONFIG_NOSYSTEM": "1",
    "GIT_AUTHOR_NAME": "t",
    "GIT_AUTHOR_EMAIL": "t@t",
    "GIT_COMMITTER_NAME": "t",
    "GIT_COMMITTER_EMAIL": "t@t",
}


def job(text: str, job_id: str) -> str:
    parts = text.split(f"\n  {job_id}:\n", 1)
    if len(parts) != 2:
        raise AssertionError(f"release.yml has no {job_id} job")
    return re.split(r"\n  [A-Za-z0-9_-]+:\n", parts[1], maxsplit=1)[0]


def steps(block: str) -> dict:
    out = {}
    for step in block.split("\n      - name: ")[1:]:
        name, _, body = step.partition("\n")
        out[name] = body
    return out


def script(step: str) -> str:
    return textwrap.dedent(step.split("        run: |\n", 1)[1])


class ReleaseWorkflowTest(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.text = RELEASE.read_text()
        cls.build = steps(job(cls.text, "build"))

    def run_step(self, name, cwd, env):
        bin_dir = Path(cwd) / ".bin"
        bin_dir.mkdir(exist_ok=True)
        if not (bin_dir / "python3").exists():
            (bin_dir / "python3").symlink_to(sys.executable)
        return subprocess.run(
            ["bash", "-c", script(self.build[name])],
            cwd=cwd,
            env={"PATH": f"{bin_dir}:/usr/bin:/bin", "HOME": str(cwd), **GIT_ENV, **env},
            capture_output=True,
            text=True,
        )

    def test_release_runs_on_release_tags_and_a_dry_run_dispatch(self):
        on = self.text.split("\non:\n", 1)[1].split("\npermissions:", 1)[0]
        self.assertIn('      - "v[0-9]+.[0-9]+.[0-9]+"\n', on)
        self.assertIn('      - "v[0-9]+.[0-9]+.[0-9]+-beta.[0-9]+"\n', on)
        self.assertIn("  workflow_dispatch:\n    inputs:\n      tag:\n", on)

    def test_only_the_publish_job_can_write_and_only_on_a_tag_push(self):
        self.assertIn("\npermissions: {}\n", self.text)
        build = job(self.text, "build")
        publish = job(self.text, "publish")
        self.assertIn("    permissions:\n      contents: read\n", build)
        self.assertNotRegex(build, r"(?m)^\s+[a-z-]+: write")
        self.assertIn("    if: github.event_name == 'push'\n", publish)
        self.assertIn("      contents: write", publish)
        # The publish job runs no repository code: no checkout, only build's artifact.
        self.assertNotIn("actions/checkout", publish)
        for job_id in ("build", "publish"):
            self.assertNotRegex(job(self.text, job_id), r"(?m)^\s+(id-token|attestations|packages): write")

    def git_repo(self, root):
        def git(*args):
            return subprocess.run(["git", *args], cwd=root, env={"PATH": "/usr/bin:/bin", "HOME": str(root), **GIT_ENV},
                                  check=True, capture_output=True, text=True).stdout.strip()

        git("init", "-q", "-b", "master")
        git("commit", "-q", "--allow-empty", "-m", "base")
        return git

    def test_a_beta_tag_must_sit_on_its_own_stable_branch(self):
        with tempfile.TemporaryDirectory() as tmp:
            git = self.git_repo(tmp)
            git("checkout", "-q", "-b", "stable/1.0")
            git("commit", "-q", "--allow-empty", "-m", "beta")
            on_stable = git("rev-parse", "HEAD")
            git("update-ref", "refs/remotes/origin/stable/1.0", on_stable)
            git("checkout", "-q", "master")
            git("commit", "-q", "--allow-empty", "-m", "master only")
            master_only = git("rev-parse", "HEAD")
            git("update-ref", "refs/remotes/origin/stable/1.1", master_only)
            env = {"RELEASE_TAG": "v1.0.0-beta.1", "STABLE_BRANCH": "stable/1.0"}

            git("checkout", "-q", "--detach", on_stable)
            r = self.run_step("Require a beta tag on its own stable branch", tmp, env)
            self.assertEqual(r.returncode, 0, r.stderr)

            # On master, and so on the newer stable/1.1 cut from it, but not on stable/1.0.
            git("checkout", "-q", "--detach", master_only)
            r = self.run_step("Require a beta tag on its own stable branch", tmp, env)
            self.assertNotEqual(r.returncode, 0)
            self.assertIn("is not on stable/1.0", r.stderr)

            r = self.run_step("Require a beta tag on its own stable branch", tmp,
                              {"RELEASE_TAG": "v1.2.0-beta.1", "STABLE_BRANCH": "stable/1.2"})
            self.assertNotEqual(r.returncode, 0)
            self.assertIn("there is no stable/1.2", r.stderr)

    def test_the_tag_must_match_the_version_file(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / "scripts").mkdir()
            # version.py --check has its own tests; here it only has to be called.
            (root / "scripts" / "version.py").write_text("import sys\nprint('checked ' + ' '.join(sys.argv[1:]))\n")
            (root / "VERSION").write_text("1.0.0~beta1\n")
            ok = self.run_step("Verify tag matches VERSION", tmp,
                               {"RELEASE_TAG": "v1.0.0-beta.1", "TAG_VERSION": "1.0.0~beta1", "PRERELEASE": "true"})
            self.assertEqual(ok.returncode, 0, ok.stderr)
            self.assertIn("checked --check", ok.stdout)
            bad = self.run_step("Verify tag matches VERSION", tmp,
                                {"RELEASE_TAG": "v1.0.0-beta.2", "TAG_VERSION": "1.0.0~beta2", "PRERELEASE": "true"})
            self.assertNotEqual(bad.returncode, 0)
            self.assertIn("VERSION file holds 1.0.0~beta1", bad.stderr)

    def notes_tree(self, tmp):
        root = Path(tmp)
        (root / "scripts").mkdir()
        (root / "scripts" / "changelog-section.py").write_text(
            (REPO_ROOT / "scripts" / "changelog-section.py").read_text())
        (root / "docs" / "releases").mkdir(parents=True)
        return root

    def notes_env(self, tag):
        semver = tag[1:]
        return {"RELEASE_TAG": tag, "SEMVER": semver, "NOTES_NAME": semver + ".md",
                "PRERELEASE": "true" if "-beta." in tag else "false", "GITHUB_REPOSITORY": "open-astro/openastro-ara"}

    def test_a_beta_needs_its_notes_file_and_links_how_to_build_it(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = self.notes_tree(tmp)
            env = self.notes_env("v1.0.0-beta.1")
            missing = self.run_step("Build release notes", tmp, env)
            self.assertNotEqual(missing.returncode, 0)
            self.assertIn("needs docs/releases/1.0.0-beta.1.md", missing.stderr)

            (root / "docs" / "releases" / "1.0.0-beta.1.md").write_text("Plain words about the beta.")
            r = self.run_step("Build release notes", tmp, env)
            self.assertEqual(r.returncode, 0, r.stderr)
            notes = (root / "release" / "release-notes.md").read_text()
            self.assertTrue(notes.startswith("Plain words about the beta.\n"))
            self.assertIn("This is a beta", notes)
            self.assertIn(
                "Build it from source: https://github.com/open-astro/openastro-ara/blob/v1.0.0-beta.1/"
                "docs/RELEASES.md#building-a-release\n", notes)
            self.assertIn("openastro-ara-1.0.0-beta.1-source.tar.gz", notes)

    def test_a_stable_release_needs_its_dated_changelog_section(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = self.notes_tree(tmp)
            env = self.notes_env("v1.0.0")
            (root / "CHANGELOG.md").write_text("# Changelog\n\n## [0.0.1] - 2026-11-19\n\n- Old.\n")
            missing = self.run_step("Build release notes", tmp, env)
            self.assertNotEqual(missing.returncode, 0)
            # Plain-language notes do not stand in for the uncut section.
            (root / "docs" / "releases" / "1.0.0.md").write_text("Plain words about 1.0.\n")
            missing = self.run_step("Build release notes", tmp, env)
            self.assertNotEqual(missing.returncode, 0)
            (root / "docs" / "releases" / "1.0.0.md").unlink()

            (root / "CHANGELOG.md").write_text("# Changelog\n\n## [1.0.0] - 2026-12-01\n\n### Added\n\n- A thing.\n")
            from_changelog = self.run_step("Build release notes", tmp, env)
            self.assertEqual(from_changelog.returncode, 0, from_changelog.stderr)
            notes = (root / "release" / "release-notes.md").read_text()
            self.assertIn("- A thing.", notes)
            self.assertIn("Build it from source: https://github.com/open-astro/openastro-ara/blob/v1.0.0/", notes)

            (root / "docs" / "releases" / "1.0.0.md").write_text("Plain words about 1.0.\n")
            from_notes = self.run_step("Build release notes", tmp, env)
            self.assertEqual(from_notes.returncode, 0, from_notes.stderr)
            notes = (root / "release" / "release-notes.md").read_text()
            self.assertTrue(notes.startswith("Plain words about 1.0.\n"))
            self.assertNotIn("- A thing.", notes)
            self.assertIn(
                "Full technical changelog: https://github.com/open-astro/openastro-ara/blob/v1.0.0/"
                "CHANGELOG.md#100---2026-12-01\n", notes)


if __name__ == "__main__":
    unittest.main()
