#!/usr/bin/env python3
"""Guard for #1186: every required ReadWritePaths= entry must exist at start.

Run from the repo root:

    python3 -m unittest discover -s scripts/tests -v

systemd bind-mounts each ReadWritePaths= entry into the service's mount
namespace. An entry without a "-" prefix that does not exist makes the whole
namespace setup fail (status 226/NAMESPACE) and the unit restart-loops. The
package is the only thing that runs before the first start, so each required
entry has to be created by DEBIAN/postinst (`mkdir -p`) or by the tmpfiles.d
drop-in (a `d`/`D` line). Anything created later (the §29 store mount point)
must carry the "-" prefix.
"""

from __future__ import annotations

import shlex
import unittest
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent.parent
DEB = REPO_ROOT / "packaging" / "debian"
UNIT = DEB / "etc" / "systemd" / "system" / "openastroara-server.service"
POSTINST = DEB / "DEBIAN" / "postinst"
TMPFILES = DEB / "usr" / "lib" / "tmpfiles.d" / "openastroara.conf"


def rw_paths(unit_text: str) -> list[str]:
    """Every ReadWritePaths= token, "-" / "+" prefixes kept."""
    out: list[str] = []
    for raw in unit_text.splitlines():
        line = raw.strip()
        if line.startswith(("#", ";")) or "=" not in line:
            continue
        key, _, value = line.partition("=")
        if key.strip() == "ReadWritePaths":
            out.extend(value.split())
    return out


def required(tokens: list[str]) -> list[str]:
    """Entries systemd insists exist: no "-" in the leading prefix chars."""
    req = []
    for tok in tokens:
        prefix = tok[: len(tok) - len(tok.lstrip("-+"))]
        if "-" not in prefix:
            req.append(tok.lstrip("+"))
    return req


def postinst_dirs(text: str) -> set[str]:
    dirs: set[str] = set()
    for raw in text.splitlines():
        line = raw.strip()
        if line.startswith("#") or "mkdir" not in line:
            continue
        words = shlex.split(line, comments=True)
        if words and words[0] == "mkdir":
            dirs.update(w for w in words[1:] if w.startswith("/"))
    return dirs


def tmpfiles_dirs(text: str) -> set[str]:
    dirs: set[str] = set()
    for raw in text.splitlines():
        fields = raw.split()
        if len(fields) >= 2 and fields[0].rstrip("!") in ("d", "D"):
            dirs.add(fields[1])
    return dirs


def canon(path: str) -> str:
    """/var/run is a symlink to /run on every Debian this ships to."""
    path = path.rstrip("/") or "/"
    return "/run" + path[len("/var/run"):] if path == "/var/run" or path.startswith("/var/run/") else path


def missing(unit_text: str, postinst_text: str, tmpfiles_text: str) -> list[str]:
    created = {canon(p) for p in postinst_dirs(postinst_text) | tmpfiles_dirs(tmpfiles_text)}
    return [p for p in required(rw_paths(unit_text)) if canon(p) not in created]


class UnitReadWritePathsTest(unittest.TestCase):
    def test_every_required_entry_is_created_by_the_package(self):
        gaps = missing(UNIT.read_text(), POSTINST.read_text(), TMPFILES.read_text())
        self.assertEqual(
            gaps, [],
            f"{UNIT.relative_to(REPO_ROOT)}: ReadWritePaths= entries {gaps} have no '-' prefix "
            "but neither postinst nor tmpfiles.d creates them; a fresh install fails "
            "226/NAMESPACE (#1186). Create the directory or prefix the entry with '-'.")

    def test_store_mount_point_is_optional(self):
        # The store only exists after the storage step; see the unit's comment.
        self.assertIn("-/media/openastroara", rw_paths(UNIT.read_text()))

    def test_parser_sees_the_unit(self):
        # A parser that silently finds nothing would pass the check above vacuously.
        self.assertGreaterEqual(len(required(rw_paths(UNIT.read_text()))), 4)

    def test_catches_a_required_path_nothing_creates(self):
        unit = "[Service]\nReadWritePaths=/var/lib/x -/media/y\nReadWritePaths=+/srv/z\n"
        post = "mkdir -p /var/lib/x\n"
        self.assertEqual(missing(unit, post, ""), ["/srv/z"])

    def test_var_run_and_run_are_the_same_directory(self):
        unit = "ReadWritePaths=/var/run/x\n"
        self.assertEqual(missing(unit, "", "d /run/x 0750 a a -\n"), [])


if __name__ == "__main__":
    unittest.main()
