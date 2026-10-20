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
