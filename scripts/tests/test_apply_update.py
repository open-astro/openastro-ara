#!/usr/bin/env python3
"""#1122: apply-update.sh (the root half of the §33 update push) against stub tools.

Run from the repo root:

    python3 -m unittest discover -s scripts/tests -v

The helper installs a staged .deb with dpkg, restarts the daemon, waits for
/healthz and reinstalls the previous package when the new one never answers.
Every tool it calls honours an environment override, so these tests replace
dpkg / dpkg-deb / dpkg-query / systemctl / curl with small shell stubs that log
their argv and answer from files, and check what the helper did and printed.
"""

from __future__ import annotations

import os
import stat
import subprocess
import tempfile
import unittest
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent.parent
HELPER = REPO_ROOT / "packaging" / "debian" / "opt" / "openastroara" / "scripts" / "apply-update.sh"

DPKG_STUB = r"""#!/bin/sh
echo "dpkg $*" >> "$LOG"
case "$1" in
  --print-architecture) echo arm64 ;;
  --compare-versions)
    # newer iff the candidate is listed in $NEWER
    [ "$2" = "$(cat "$STATE/newer")" ] ;;
  -i)
    ver=$(basename "$2" .deb)
    if [ -f "$STATE/fail-install-$ver" ]; then exit 1; fi
    echo "$ver" > "$STATE/installed" ;;
esac
"""
DPKG_DEB_STUB = r"""#!/bin/sh
# dpkg-deb -f <file> <Field>
case "$3" in
  Package) cat "$STATE/pkg" ;;
  Version) cat "$STATE/newer" ;;
  Architecture) echo arm64 ;;
esac
"""
DPKG_QUERY_STUB = r"""#!/bin/sh
cat "$STATE/installed"
"""
SYSTEMCTL_STUB = r"""#!/bin/sh
echo "systemctl $*" >> "$LOG"
# report the unit stopped so the helper's restart path is exercised
[ "$1" = is-active ] && exit 1
exit 0
"""
CURL_STUB = r"""#!/bin/sh
# healthy only when the installed version is not marked unhealthy
[ ! -f "$STATE/unhealthy-$(cat "$STATE/installed")" ]
"""


class ApplyUpdate(unittest.TestCase):
    def setUp(self) -> None:
        self.tmp = tempfile.TemporaryDirectory()
        root = Path(self.tmp.name)
        self.state = root / "state"
        self.state.mkdir()
        self.stage = root / "stage"
        self.stage.mkdir()
        self.rollback = root / "rollback"
        self.apt = root / "apt"
        self.apt.mkdir()
        self.log = root / "log"
        self.log.touch()
        bin_dir = root / "bin"
        bin_dir.mkdir()
        self.tools = {}
        for name, body in [("dpkg", DPKG_STUB), ("dpkg-deb", DPKG_DEB_STUB), ("dpkg-query", DPKG_QUERY_STUB),
                           ("systemctl", SYSTEMCTL_STUB), ("curl", CURL_STUB)]:
            p = bin_dir / name
            p.write_text(body)
            p.chmod(p.stat().st_mode | stat.S_IXUSR)
            self.tools[name] = str(p)
        (self.state / "installed").write_text("1.0")
        (self.state / "newer").write_text("2.0")
        (self.state / "pkg").write_text("openastroara-server")
        self.deb = self.stage / "abc123.deb"
        self.deb.write_text("new package bytes")
        # The apt cache holds the running version, as on a rig installed from apt.
        (self.apt / "openastroara-server_1.0_arm64.deb").write_text("old package bytes")

    def tearDown(self) -> None:
        self.tmp.cleanup()

    def run_helper(self, deb: str | None = None, port: str = "5555") -> subprocess.CompletedProcess[str]:
        env = dict(os.environ,
                   STAGE_DIR=str(self.stage), ROLLBACK_DIR=str(self.rollback), APT_CACHE=str(self.apt),
                   DPKG=self.tools["dpkg"], DPKG_DEB=self.tools["dpkg-deb"], DPKG_QUERY=self.tools["dpkg-query"],
                   SYSTEMCTL=self.tools["systemctl"], CURL=self.tools["curl"],
                   HEALTH_TIMEOUT="2", DRAIN_SECONDS="0", STATE=str(self.state), LOG=str(self.log))
        return subprocess.run(["sh", str(HELPER), deb or str(self.deb), port],
                              env=env, capture_output=True, text=True, timeout=60)

    def installed(self) -> str:
        return (self.state / "installed").read_text().strip()

    def test_a_healthy_update_is_applied_and_both_packages_are_kept(self) -> None:
        r = self.run_helper()
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        self.assertIn("status=applied", r.stdout)
        self.assertIn("from=1.0", r.stdout)
        self.assertIn("to=2.0", r.stdout)
        self.assertIn("rollback=available", r.stdout)
        self.assertEqual(self.installed(), "2.0")
        self.assertTrue(self.deb.exists(), "root never deletes in the daemon-owned stage dir; the daemon does")
        self.assertTrue((self.rollback / "1.0.deb").exists(), "the running version is kept for rollback")
        self.assertTrue((self.rollback / "2.0.deb").exists(), "and the new one, for the next update's rollback")
        self.assertIn("systemctl restart openastroara-server.service", self.log.read_text())

    def test_an_update_that_never_answers_healthz_is_rolled_back(self) -> None:
        (self.state / "unhealthy-2.0").touch()
        r = self.run_helper()
        self.assertNotEqual(r.returncode, 0)
        self.assertIn("status=rolled_back", r.stdout)
        self.assertEqual(self.installed(), "1.0")

    def test_a_failed_dpkg_install_is_rolled_back(self) -> None:
        (self.state / "fail-install-2.0").touch()
        r = self.run_helper()
        self.assertIn("status=rolled_back", r.stdout)
        self.assertEqual(self.installed(), "1.0")

    def test_no_rollback_copy_reports_unavailable_and_failed(self) -> None:
        for f in self.apt.iterdir():
            f.unlink()
        (self.state / "unhealthy-2.0").touch()
        r = self.run_helper()
        self.assertIn("rollback=unavailable", r.stdout)
        self.assertIn("status=failed", r.stdout)

    def assert_refused(self, r: subprocess.CompletedProcess[str]) -> None:
        self.assertIn("status=failed", r.stdout)
        self.assertEqual(self.installed(), "1.0")
        self.assertNotIn("dpkg -i", self.log.read_text())

    def test_a_different_package_is_refused(self) -> None:
        (self.state / "pkg").write_text("something-else")
        self.assert_refused(self.run_helper())

    def test_a_version_that_is_not_newer_is_refused(self) -> None:
        stub = Path(self.tools["dpkg"])
        stub.write_text(DPKG_STUB.replace('[ "$2" = "$(cat "$STATE/newer")" ]', "false"))
        self.assert_refused(self.run_helper())

    def test_staged_path_outside_the_stage_dir_or_a_symlink_is_refused(self) -> None:
        outside = Path(self.tmp.name) / "evil.deb"
        outside.write_text("x")
        link = self.stage / "link.deb"
        link.symlink_to(outside)
        nested = self.stage / "sub"
        nested.mkdir()
        (nested / "n.deb").write_text("x")
        for path in [str(outside), str(link), str(nested / "n.deb"), str(self.stage / "../evil.deb")]:
            with self.subTest(path):
                r = self.run_helper(deb=path)
                self.assert_refused(r)

    def test_root_writes_only_to_the_rollback_dir(self) -> None:
        r = self.run_helper()
        self.assertIn("status=applied", r.stdout)
        self.assertEqual([p.name for p in self.stage.iterdir()], ["abc123.deb"], "root neither writes nor deletes in the stage dir")
        self.assertEqual(sorted(p.name for p in self.rollback.iterdir()), ["1.0.deb", "2.0.deb"],
                         "the work dir is cleaned up; only the two kept packages remain")
        self.assertEqual(oct(self.rollback.stat().st_mode & 0o777), "0o700")

    def test_old_rollback_copies_are_pruned_after_a_healthy_update(self) -> None:
        self.rollback.mkdir(mode=0o700)
        (self.rollback / "0.5.deb").write_text("ancient")
        r = self.run_helper()
        self.assertIn("status=applied", r.stdout)
        self.assertEqual(sorted(p.name for p in self.rollback.iterdir()), ["1.0.deb", "2.0.deb"])

    def test_a_symlinked_stage_directory_is_refused(self) -> None:
        real = Path(self.tmp.name) / "real-stage"
        self.stage.rename(real)
        self.stage.symlink_to(real)
        self.assert_refused(self.run_helper(deb=str(self.stage / "abc123.deb")))
        self.assertTrue((real / "abc123.deb").exists(), "root never touched the file behind the link")

    def run_with_drain(self, drain: str) -> subprocess.CompletedProcess[str]:
        # No DRAIN_SECONDS override: the third argument (from the request) must be used.
        env = {k: v for k, v in os.environ.items() if k != "DRAIN_SECONDS"}
        env.update(STAGE_DIR=str(self.stage), ROLLBACK_DIR=str(self.rollback), APT_CACHE=str(self.apt),
                   DPKG=self.tools["dpkg"], DPKG_DEB=self.tools["dpkg-deb"], DPKG_QUERY=self.tools["dpkg-query"],
                   SYSTEMCTL=self.tools["systemctl"], CURL=self.tools["curl"],
                   HEALTH_TIMEOUT="2", STATE=str(self.state), LOG=str(self.log))
        return subprocess.run(["sh", str(HELPER), str(self.deb), "5555", drain],
                              env=env, capture_output=True, text=True, timeout=60)

    def test_the_drain_time_comes_from_the_request(self) -> None:
        r = self.run_with_drain("1")
        self.assertIn("draining the running daemon for 1s", r.stdout)
        self.assertIn("status=applied", r.stdout)

    def test_a_non_numeric_drain_time_is_refused(self) -> None:
        self.assert_refused(self.run_with_drain("1;id"))

    def test_a_non_numeric_port_is_refused(self) -> None:
        self.assert_refused(self.run_helper(port="5555;reboot"))


if __name__ == "__main__":
    unittest.main()
