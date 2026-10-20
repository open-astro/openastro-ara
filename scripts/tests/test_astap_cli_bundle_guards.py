#!/usr/bin/env python3
"""Guards for the bundled ASTAP solver (#1136): every artifact and every client default
agrees with the daemon on where astap_cli lives.

Run from the repo root:

    python3 -m unittest discover -s scripts/tests -v

SolverPathMigration.AstapCliPath is the profile default and the migration target. The
.deb installs the daemon at that directory, so the Docker image's WORKDIR must be the same
directory (the image copies astap_cli next to the daemon), and the client's three copies
of the default (Settings registry, the profile API fallback, the settings state) must name
the same file, or a fresh profile points at a binary that is not there.
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
    (CLIENT / "settings" / "registry.dart",
     r"id: 'img\.platesolve\.path_or_endpoint',.*?defaultValue: '([^']+)'"),
    (CLIENT / "services" / "profile_api.dart",
     r"pathOrEndpoint: \(j\['path_or_endpoint'\] as String\?\) \?\? '([^']+)'"),
    (CLIENT / "state" / "settings" / "plate_solve_settings_state.dart",
     r"this\.pathOrEndpoint = '([^']+)'"),
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
            self.assertEqual(m[1], expected, f"{path.relative_to(REPO_ROOT)} default")


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
