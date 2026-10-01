#!/usr/bin/env python3
"""Guards for #1130: the §14e astrometry natives must not ship silently broken.

Run from the repo root:

    python3 -m unittest discover -s scripts/tests -v

libsofa.so / libnovas31.so are cross-built on ubuntu-latest (glibc 2.39) and
need `fmod@GLIBC_2.38`. Debian Bookworm ships glibc 2.36: the .deb installs,
dlopen fails, and the boot probe reports the file MISSING although it is on
disk. The package therefore has to refuse to install there, and every artifact
that bundles the publish dir (the .deb and the Docker image) has to refuse a
publish dir without the two libraries.
"""

from __future__ import annotations

import re
import unittest
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent.parent
CONTROL = REPO_ROOT / "packaging" / "debian" / "DEBIAN" / "control.template"
BUILD_DEB = REPO_ROOT / "packaging" / "build-deb.sh"
DOCKERFILE = REPO_ROOT / "Dockerfile"
NATIVES = ("libsofa.so", "libnovas31.so")


class Libc6Dependency(unittest.TestCase):
    def test_libc6_is_versioned_at_or_above_2_38(self) -> None:
        depends = next(
            line for line in CONTROL.read_text().splitlines()
            if line.startswith("Depends:")
        )
        m = re.search(r"\blibc6 \(>= (\d+)\.(\d+)\)", depends)
        self.assertIsNotNone(m, f"libc6 must carry a version in: {depends}")
        self.assertGreaterEqual((int(m[1]), int(m[2])), (2, 38),
                                "natives need fmod@GLIBC_2.38")


class NativesGuards(unittest.TestCase):
    def test_build_deb_refuses_a_publish_dir_without_the_natives(self) -> None:
        text = BUILD_DEB.read_text()
        for lib in NATIVES:
            self.assertIn(lib, text)
        self.assertIn("exit 1", text)

    def test_dockerfile_copies_each_native_explicitly(self) -> None:
        # The chiseled base has no shell, so the guard is a COPY whose source
        # must exist; one line that names both libraries.
        copies = [
            line for line in DOCKERFILE.read_text().splitlines()
            if line.startswith("COPY ")
        ]
        self.assertTrue(
            any(all(lib in line for lib in NATIVES) for line in copies),
            f"no COPY names both natives: {copies}",
        )


if __name__ == "__main__":
    unittest.main()
