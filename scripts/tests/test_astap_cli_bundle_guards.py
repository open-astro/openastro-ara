#!/usr/bin/env python3
"""Guards for the bundled ASTAP solver (#1136): every artifact and every client default
agrees with the daemon on where astap_cli lives.

Run from the repo root:

    python3 -m unittest discover -s scripts/tests -v

SolverPathMigration.AstapCliPath is the profile default and the migration target. The
.deb installs the daemon at that directory, so the Docker image's WORKDIR must be the same
directory (the image copies astap_cli next to the daemon), and the client's copies of the
default (the profile API fallback, the settings state, and the Settings registry row unless
it is null: plate solve loads before it saves, #1807) must name the same file, or a fresh
profile points at a binary that is not there.
"""

from __future__ import annotations

import re
import unittest
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent.parent
MIGRATION = REPO_ROOT / "OpenAstroAra.Server" / "Services" / "SolverPathMigration.cs"
DOCKERFILE = REPO_ROOT / "Dockerfile"
CLIENT = REPO_ROOT / "client" / "openastroara_client" / "lib"
CLIENT_DEFAULTS = (
    # The row's own default, never a later row's (the match stops at the next Setting( row).
    (CLIENT / "settings" / "registry.dart",
     r"id: 'img\.platesolve\.path_or_endpoint',(?:(?!Setting\().)*?defaultValue: (?:null|'([^']+)')"),
)
# Since #1807 (ADR 0015 rule 3) the client keeps no default for a rig-owned field: the reader
# and the model must hold no copy of the solver path at all, so none can drift from the daemon's.
CLIENT_NO_COPY = (
    (CLIENT / "services" / "profile_api.dart",
     r"j\['path_or_endpoint'\] as String\?\) \?\? '"),
    (CLIENT / "state" / "settings" / "plate_solve_settings_state.dart",
     r"this\.pathOrEndpoint = '"),
)


def server_astap_path() -> str:
    m = re.search(r'public const string AstapCliPath = "([^"]+)";', MIGRATION.read_text())
    assert m, "SolverPathMigration.AstapCliPath not found"
    return m[1]


class DockerImage(unittest.TestCase):
    def test_workdir_is_the_daemon_directory_the_default_solver_path_names(self) -> None:
        workdir = re.search(r"^WORKDIR (\S+)$", DOCKERFILE.read_text(), re.M)
        self.assertIsNotNone(workdir, "Dockerfile has no WORKDIR")
        expected = str(Path(server_astap_path()).parent)
        self.assertEqual(workdir[1], expected,
                         f"the image must run the daemon from {expected}: the profile default "
                         f"{server_astap_path()} is absolute, and the solver is copied next to the daemon")

    def test_image_copies_astap_cli_next_to_the_daemon(self) -> None:
        self.assertRegex(DOCKERFILE.read_text(), re.compile(r"^COPY publish/arm64/astap_cli \./$", re.M),
                         "the image must carry astap_cli in the daemon directory")


class ClientDefaults(unittest.TestCase):
    def test_every_client_default_names_the_bundled_solver(self) -> None:
        expected = server_astap_path()
        for path, pattern in CLIENT_DEFAULTS:
            m = re.search(pattern, path.read_text(), re.S)
            self.assertIsNotNone(m, f"{path.relative_to(REPO_ROOT)}: default not found")
            if m[1] is not None:  # a null registry row holds no copy that could drift
                self.assertEqual(m[1], expected, f"{path.relative_to(REPO_ROOT)} default")

    def test_the_client_reader_and_model_hold_no_copy_of_the_solver_path(self) -> None:
        for path, pattern in CLIENT_NO_COPY:
            self.assertIsNone(re.search(pattern, path.read_text()),
                              f"{path.relative_to(REPO_ROOT)}: a client default for the solver path is back")


if __name__ == "__main__":
    unittest.main()


BUILD_DEB = REPO_ROOT / "packaging" / "build-deb.sh"
VM_SH = REPO_ROOT / ".claude" / "skills" / "ara-sbc-vm" / "scripts" / "vm.sh"


class DebPackaging(unittest.TestCase):
    def test_build_deb_refuses_a_publish_dir_without_astap_cli(self) -> None:
        self.assertRegex(BUILD_DEB.read_text(),
                         r'\[ -f "\$PUBLISH_DIR/astap_cli" \] \|\| \{[^\n]*exit 1;')


class VmBuildPr(unittest.TestCase):
    """vm.sh build-pr assembles the .deb inside the VM; since build-deb.sh refuses a publish
    dir without astap_cli, the skill has to ship the solver source and build it there."""

    def test_ships_the_solver_submodule_and_build_script(self) -> None:
        text = VM_SH.read_text()
        tar = re.search(r"tar -cf - ([^)]*)\) \| vssh \"tar -xf - -C ~/build\"", text)
        self.assertIsNotNone(tar, "vm.sh no longer ships the sources with tar")
        for item in ("third_party/astap", "scripts/build-astap-cli.sh"):
            self.assertIn(item, tar[1].split(), f"vm.sh must ship {item}")

    def test_builds_astap_cli_before_the_deb(self) -> None:
        text = VM_SH.read_text()
        self.assertRegex(text, r"apt-get install[^\n]*\bfpc\b", "the VM needs fpc")
        cli = text.find("bash scripts/build-astap-cli.sh publish")
        deb = text.find("bash packaging/build-deb.sh publish")
        self.assertGreater(cli, -1, "vm.sh must run build-astap-cli.sh")
        self.assertLess(cli, deb, "build-astap-cli.sh must run before build-deb.sh")


CI_YML = REPO_ROOT / ".github" / "workflows" / "ci.yml"
DATA_MANAGER = REPO_ROOT / "OpenAstroAra.Server" / "Services" / "DataManagerService.cs"


class CiSolveCheck(unittest.TestCase):
    """The astap-solve CI job downloads the D05 package Settings → Sky data installs; the URL and
    checksum it pins must be the daemon's, so a sky-data release bump moves both together."""

    def test_d05_url_and_checksum_match_the_daemon_catalog(self) -> None:
        daemon = DATA_MANAGER.read_text()
        url = re.search(r'SourceUrl: new Uri\("([^"]+/astap-d05\.tar\.gz)"\)', daemon)
        sha = re.search(r'\["astap-d05"\] = "([0-9a-f]{64})"', daemon)
        self.assertIsNotNone(url, "DataManagerService: D05 SourceUrl not found")
        self.assertIsNotNone(sha, "DataManagerService: D05 CatalogSha256 not found")
        job = CI_YML.read_text().split("\n  astap-solve:\n", 1)
        self.assertEqual(len(job), 2, "ci.yml has no astap-solve job")
        job[1] = re.split(r"\n  [A-Za-z0-9_-]+:\n", job[1], maxsplit=1)[0]  # this job only
        self.assertIn(url[1], job[1], "astap-solve must download the daemon's D05 package")
        self.assertIn(f"echo \"{sha[1]}  ", job[1], "astap-solve must verify the daemon's D05 checksum")
        self.assertIn(f"key: astap-d05-{sha[1]}", job[1], "the D05 cache key must follow the checksum")
        self.assertIn("scripts/ci-astap-solve.sh", job[1])
