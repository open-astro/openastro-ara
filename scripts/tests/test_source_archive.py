#!/usr/bin/env python3
"""Tests for scripts/source-archive.sh (#2093): the source tarball a release ships.

Run from the repo root:

    python3 -m unittest discover -s scripts/tests -v

Each case builds a throwaway superproject with one submodule, copies the
script and the version scripts it calls into it, and archives a tag or a ref.
"""

from __future__ import annotations

import hashlib
import os
import shutil
import subprocess
import tarfile
import tempfile
import unittest
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent.parent
SCRIPTS = ("source-archive.sh", "version.py", "release-tag.py", "release-tag-cases.txt")

GIT_ENV = {
    "GIT_CONFIG_GLOBAL": os.devnull,
    "GIT_CONFIG_NOSYSTEM": "1",
    "GIT_AUTHOR_NAME": "t", "GIT_AUTHOR_EMAIL": "t@t",
    "GIT_COMMITTER_NAME": "t", "GIT_COMMITTER_EMAIL": "t@t",
    "GIT_AUTHOR_DATE": "2026-11-19T12:00:00Z", "GIT_COMMITTER_DATE": "2026-11-19T12:00:00Z",
}


class SourceArchiveTest(unittest.TestCase):
    def setUp(self):
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        self.tmp = Path(tmp.name)
        self.env = {k: v for k, v in os.environ.items() if not k.startswith("GIT_")}
        self.env.update(GIT_ENV)
        self.sub = self.tmp / "sub"
        self.sub.mkdir()
        self.git(self.sub, "init", "-q", "-b", "main")
        (self.sub / "lib.c").write_text("int one;\n")
        self.git(self.sub, "add", "lib.c")
        self.git(self.sub, "commit", "-q", "-m", "one")
        self.repo = self.tmp / "ara"
        self.repo.mkdir()
        self.git(self.repo, "init", "-q", "-b", "master")
        (self.repo / "scripts").mkdir()
        for name in SCRIPTS:
            shutil.copy2(REPO_ROOT / "scripts" / name, self.repo / "scripts" / name)
        (self.repo / "VERSION").write_text("1.0.0~beta1\n")
        (self.repo / "README.md").write_text("ara\n")
        self.git(self.repo, "-c", "protocol.file.allow=always", "submodule", "add", "-q", str(self.sub), "third_party/sub")
        self.git(self.repo, "add", "-A")
        self.git(self.repo, "commit", "-q", "-m", "base")
        self.git(self.repo, "tag", "-a", "-m", "beta", "v1.0.0-beta.1")
        self.out = self.tmp / "out"

    def git(self, cwd, *args):
        r = subprocess.run(["git", "-C", str(cwd), "-c", "commit.gpgsign=false", "-c", "tag.gpgsign=false", *args],
                           capture_output=True, text=True, env=self.env)
        self.assertEqual(r.returncode, 0, r.stderr)
        return r.stdout.strip()

    def archive(self, ref, version=None, out=None):
        args = [ref, str(out or self.out)] + ([version] if version else [])
        return subprocess.run(["bash", str(self.repo / "scripts" / "source-archive.sh"), *args],
                              capture_output=True, text=True, env=self.env)

    def members(self, path):
        with tarfile.open(path) as t:
            return {m.name: (m, t.extractfile(m).read() if m.isreg() else None) for m in t}

    def test_a_tag_archive_holds_the_submodule_under_one_top_folder(self):
        r = self.archive("v1.0.0-beta.1")
        self.assertEqual(r.returncode, 0, r.stderr)
        members = self.members(self.out / "openastro-ara-1.0.0-beta.1-source.tar.gz")
        self.assertEqual(members["openastro-ara-1.0.0-beta.1/third_party/sub/lib.c"][1], b"int one;\n")
        self.assertEqual(members["openastro-ara-1.0.0-beta.1/VERSION"][1], b"1.0.0~beta1\n")
        self.assertTrue(all(n == "openastro-ara-1.0.0-beta.1" or n.startswith("openastro-ara-1.0.0-beta.1/")
                            for n in members))

    def test_the_sha256_file_names_the_tarball(self):
        self.assertEqual(self.archive("v1.0.0-beta.1").returncode, 0)
        name = "openastro-ara-1.0.0-beta.1-source.tar.gz"
        digest = hashlib.sha256((self.out / name).read_bytes()).hexdigest()
        self.assertEqual((self.out / (name + ".sha256")).read_text(), "%s  %s\n" % (digest, name))

    def test_the_archive_is_the_same_bytes_every_time(self):
        self.assertEqual(self.archive("v1.0.0-beta.1").returncode, 0)
        (self.repo / "README.md").touch()
        self.assertEqual(self.archive("v1.0.0-beta.1", out=self.tmp / "again").returncode, 0)
        name = "openastro-ara-1.0.0-beta.1-source.tar.gz"
        self.assertEqual((self.out / name).read_bytes(), (self.tmp / "again" / name).read_bytes())
        stamp = int(self.git(self.repo, "log", "-1", "--format=%ct"))
        self.assertEqual({m.mtime for m, _ in self.members(self.out / name).values()}, {stamp})

    def test_the_recorded_submodule_commit_is_archived_not_the_checked_out_one(self):
        checkout = self.repo / "third_party" / "sub"
        (checkout / "lib.c").write_text("int two;\n")
        self.git(checkout, "commit", "-q", "-am", "two")
        r = self.archive("v1.0.0-beta.1")
        self.assertEqual(r.returncode, 0, r.stderr)
        members = self.members(self.out / "openastro-ara-1.0.0-beta.1-source.tar.gz")
        self.assertEqual(members["openastro-ara-1.0.0-beta.1/third_party/sub/lib.c"][1], b"int one;\n")

    def test_a_tag_that_disagrees_with_version_fails(self):
        self.git(self.repo, "tag", "-a", "-m", "wrong", "v1.0.0")
        r = self.archive("v1.0.0")
        self.assertEqual(r.returncode, 1)
        self.assertIn("tag v1.0.0 names 1.0.0, but its VERSION file is 1.0.0~beta1", r.stderr)
        self.assertFalse((self.out / "openastro-ara-1.0.0-source.tar.gz").exists())

    def test_a_ref_takes_the_version_file_or_an_explicit_version(self):
        self.assertEqual(self.archive("HEAD").returncode, 0)
        self.assertTrue((self.out / "openastro-ara-1.0.0-beta.1-source.tar.gz").is_file())
        r = self.archive("master", "0.0.1-dev.gabc1234")
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertIn("openastro-ara-0.0.1-dev.gabc1234/README.md",
                      self.members(self.out / "openastro-ara-0.0.1-dev.gabc1234-source.tar.gz"))

    def test_an_uninitialised_submodule_fails(self):
        self.git(self.repo, "submodule", "deinit", "-q", "-f", "third_party/sub")
        r = self.archive("v1.0.0-beta.1")
        self.assertEqual(r.returncode, 1)
        self.assertIn("is not initialised", r.stderr)


if __name__ == "__main__":
    unittest.main()
