#!/usr/bin/env python3
"""Tests for scripts/check-falsified-by.py.

Run from the repo root:

    python3 -m unittest discover -s scripts/tests -v

Each case builds a throwaway git repository (a base commit and a head commit),
copies the script into it and runs it as CI does: a body in a file, the exit
code and the FALSIFIED-BY / falsified-by lines asserted.
"""

from __future__ import annotations

import os
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent.parent
SCRIPT = REPO_ROOT / "scripts" / "check-falsified-by.py"

DART = "client/openastroara_client/test/album_test.dart"
CS = "OpenAstroAra.Test/AlbumTests.cs"
PY = "scripts/tests/test_thing.py"
LIB = "client/openastroara_client/lib/album.dart"
DAEMON = "OpenAstroAra.Server/Album.cs"
CHANGE = "delete the range check"

GIT_ENV = {
    "GIT_CONFIG_GLOBAL": os.devnull,
    "GIT_CONFIG_NOSYSTEM": "1",
    "GIT_AUTHOR_NAME": "t", "GIT_AUTHOR_EMAIL": "t@t",
    "GIT_COMMITTER_NAME": "t", "GIT_COMMITTER_EMAIL": "t@t",
}


def cs_test(cls, *methods, attr="[Test]"):
    body = "".join("    %s\n    public void %s() {}\n" % (attr, m) for m in methods)
    return "namespace T {\npublic class %s {\n%s}\n}\n" % (cls, body)


class FalsifiedByTest(unittest.TestCase):
    def setUp(self):
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        self.repo = Path(tmp.name) / "repo"
        self.repo.mkdir()
        self.env = {k: v for k, v in os.environ.items() if not k.startswith("GIT_")}
        self.env.update(GIT_ENV)
        self.git("init", "-q", "-b", "master")
        (self.repo / "scripts").mkdir()
        shutil.copy(SCRIPT, self.repo / "scripts" / "check-falsified-by.py")
        for p in (LIB, DAEMON):
            self.write(p, "// production\n")

    def git(self, *args):
        r = subprocess.run(["git", "-C", str(self.repo), "-c", "commit.gpgsign=false", *args],
                           capture_output=True, text=True, env=self.env)
        self.assertEqual(r.returncode, 0, r.stderr)
        return r.stdout.strip()

    def write(self, rel, text):
        p = self.repo / rel
        p.parent.mkdir(parents=True, exist_ok=True)
        p.write_text(text, encoding="utf-8")

    def commit(self, msg="c"):
        self.git("add", "-A")
        self.git("commit", "-q", "-m", msg)

    def start_branch(self):
        self.commit("base")
        self.git("checkout", "-q", "-b", "pr")

    def run_check(self, body="", base=None):
        bf = self.repo.parent / "body.md"
        bf.write_text(body, encoding="utf-8")
        cmd = [sys.executable, str(self.repo / "scripts" / "check-falsified-by.py"), "--body-file", str(bf)]
        if base:
            cmd += ["--base", base]
        return subprocess.run(cmd, capture_output=True, text=True, env=self.env)

    def body(self, *cases, path=LIB):
        return "Falsified by:\n" + "".join('- "%s": %s:1 %s\n' % (c, path, CHANGE) for c in cases)

    def assert_new_case(self, name, path_text):
        """`name` is detected as new (no line fails) and passes with a line."""
        path, text = path_text
        self.start_branch()
        self.write(path, text)
        self.commit("head")
        r = self.run_check()
        self.assertEqual(r.returncode, 1, r.stdout)
        self.assertIn("new test case has no `Falsified by:` line: %r" % name, r.stdout)
        r = self.run_check(self.body(name))
        self.assertEqual(r.returncode, 0, r.stdout)
        self.assertIn("falsified-by: OK (1 new case(s)", r.stdout)

    # --- case readers ------------------------------------------------------

    def test_dart_single_line_case(self):
        self.assert_new_case("adds a frame", (DART, "void main() { test('adds a frame', () {}); }\n"))

    def test_dart_name_on_the_next_line(self):
        self.assert_new_case("split name", (DART, "void main() {\n  testWidgets(\n    'split name',\n    (t) async {});\n}\n"))

    def test_dart_interpolated_name_is_read_as_written(self):
        self.assert_new_case("shows $count frames", (DART, 'void main() { test("shows $count frames", () {}); }\n'))

    def test_dart_adjacent_and_raw_literals(self):
        self.assert_new_case(r"first second \d", (DART, "void main() { test('first ' r'second \\d', () {}); }\n"))

    def test_dart_commented_case_is_not_a_case(self):
        self.start_branch()
        self.write(DART, "void main() { // test('ghost', () {});\n}\n")
        self.commit("head")
        self.assertEqual(self.run_check().returncode, 0)

    def test_csharp_test_attribute(self):
        self.assert_new_case("AlbumTests.Merges", (CS, cs_test("AlbumTests", "Merges")))

    def test_csharp_stacked_testcase_counts_once_per_method(self):
        text = ("namespace T {\npublic class AlbumTests {\n"
                '    [TestCase(1, "]")]\n    [TestCase(2, "b")]\n    public void Rows(int x, string s) {}\n}\n}\n')
        self.assert_new_case("AlbumTests.Rows", (CS, text))

    def test_csharp_xunit_fact(self):
        self.assert_new_case("AlbumTests.Merges", (CS, cs_test("AlbumTests", "Merges", attr="[Fact]")))

    def test_csharp_helper_without_attribute_is_not_a_case(self):
        self.start_branch()
        self.write(CS, "namespace T { public class AlbumTests { public void Helper() {} } }\n")
        self.commit("head")
        self.assertEqual(self.run_check().returncode, 0)

    def test_csharp_harness_project_is_not_a_test_project(self):
        self.start_branch()
        self.write("OpenAstroAra.TestHarness/Fake.cs", cs_test("FakeRig", "Looks"))
        self.commit("head")
        self.assertEqual(self.run_check().returncode, 0)

    def test_python_test_method(self):
        self.assert_new_case("ThingTest.test_works", (PY, "import unittest\nclass ThingTest(unittest.TestCase):\n    def test_works(self):\n        pass\n    def helper(self):\n        pass\n"))

    # --- moved, renamed, existing ------------------------------------------

    def test_moved_case_is_not_new(self):
        self.write(DART, "void main() { test('mover', () {}); }\n")
        self.start_branch()
        self.write(DART, "void main() {}\n")
        self.write("client/openastroara_client/test/other_test.dart", "void main() { test('mover', () {}); }\n")
        self.commit("move")
        r = self.run_check()
        self.assertEqual(r.returncode, 0, r.stdout)
        self.assertIn("no new test cases", r.stdout)

    def test_renamed_case_is_new(self):
        self.write(DART, "void main() { test('old name', () {}); }\n")
        self.start_branch()
        self.write(DART, "void main() { test('new name', () {}); }\n")
        self.commit("rename")
        r = self.run_check()
        self.assertEqual(r.returncode, 1)
        self.assertIn("'new name'", r.stdout)

    def test_same_name_in_a_second_file_is_new(self):
        self.write(DART, "void main() { test('shared', () {}); }\n")
        self.start_branch()
        self.write("client/openastroara_client/test/b_test.dart", "void main() { test('shared', () {}); }\n")
        self.commit("copy")
        self.assertEqual(self.run_check().returncode, 1)

    def test_line_for_an_existing_case_is_accepted(self):
        self.write(DART, "void main() { test('old', () {}); }\n")
        self.start_branch()
        self.write(LIB, "// changed\n")
        self.commit("fix")
        self.assertEqual(self.run_check(self.body("old")).returncode, 0)

    def test_stale_line_fails(self):
        self.start_branch()
        self.write(LIB, "// changed\n")
        self.commit("head")
        r = self.run_check(self.body("typo"))
        self.assertEqual(r.returncode, 1)
        self.assertIn("names no test case at HEAD (stale or typo): 'typo'", r.stdout)

    # --- the body ------------------------------------------------------------

    def _one_new_dart_case(self):
        self.start_branch()
        self.write(DART, "void main() { test('case a', () {}); }\n")
        self.commit("head")

    def test_malformed_line_fails(self):
        self._one_new_dart_case()
        r = self.run_check("Falsified by:\n- case a: whatever\n")
        self.assertEqual(r.returncode, 1)
        self.assertIn("malformed line", r.stdout)

    def test_change_under_three_words_fails(self):
        self._one_new_dart_case()
        r = self.run_check('Falsified by:\n- "case a": %s:1 delete it\n' % LIB)
        self.assertEqual(r.returncode, 1)
        self.assertIn("change has fewer than 3 words for 'case a'", r.stdout)

    def test_missing_path_fails(self):
        self._one_new_dart_case()
        r = self.run_check(self.body("case a", path="client/openastroara_client/lib/nope.dart"))
        self.assertEqual(r.returncode, 1)
        self.assertIn("path does not exist", r.stdout)

    def test_production_paths_are_accepted(self):
        self._one_new_dart_case()
        self.write("scripts/tool.py", "x = 1\n")
        self.write(".github/workflows/w.yml", "name: w\n")
        self.commit("more")
        for path in (LIB, DAEMON, "scripts/tool.py", ".github/workflows/w.yml"):
            with self.subTest(path=path):
                self.assertEqual(self.run_check(self.body("case a", path=path)).returncode, 0)

    def test_helper_path_is_refused_unless_the_name_says_the_helper_is_the_subject(self):
        self.start_branch()
        self.write(DART, "void main() { test('case a', () {}); test('Fake rotator moves', () {}); }\n")
        self.write("OpenAstroAra.TestHarness/Fake.cs", "// helper\n")
        self.commit("head")
        for path in (DART, CS, "OpenAstroAra.TestHarness/Fake.cs", PY):
            self.write(path, self.repo.joinpath(path).read_text() if self.repo.joinpath(path).exists() else "x = 1\n")
        self.commit("helpers")
        r = self.run_check(self.body("Fake rotator moves", "case a", path="OpenAstroAra.TestHarness/Fake.cs"))
        self.assertEqual(r.returncode, 1)
        self.assertIn("'case a': OpenAstroAra.TestHarness/Fake.cs is a test helper and the case name does not say", r.stdout)
        self.assertEqual(r.stdout.count("is a test helper"), 1, r.stdout)

    def test_test_project_path_is_not_production(self):
        self._one_new_dart_case()
        self.write(CS, "// helper\n")
        self.write("README.md", "doc\n")
        self.commit("files")
        for path in (CS, "README.md"):
            with self.subTest(path=path):
                r = self.run_check(self.body("case a", path=path))
                self.assertEqual(r.returncode, 1)
                self.assertTrue("is not production code" in r.stdout or "is a test helper" in r.stdout, r.stdout)
        self.assertIn("README.md is not production code", self.run_check(self.body("case a", path="README.md")).stdout)

    def test_no_cases_and_no_lines_passes(self):
        self.start_branch()
        self.write(LIB, "// changed\n")
        self.commit("head")
        r = self.run_check()
        self.assertEqual(r.returncode, 0)
        self.assertIn("no new test cases; nothing to check", r.stdout)

    def test_unknown_argument_is_a_usage_error(self):
        r = subprocess.run([sys.executable, str(SCRIPT), "--bogus"], capture_output=True, text=True)
        self.assertEqual(r.returncode, 2)

    # --- base resolution -----------------------------------------------------

    def test_merge_parent_base_ignores_cases_other_prs_landed(self):
        self.write(DART, "void main() { test('old', () {}); }\n")
        self.commit("base")
        self.git("checkout", "-q", "-b", "pr")
        self.write(DART, "void main() { test('old', () {}); test('mine', () {}); }\n")
        self.commit("pr")
        self.git("checkout", "-q", "master")
        self.write("client/openastroara_client/test/other_test.dart", "void main() { test('other', () {}); }\n")
        self.commit("other pr")
        self.git("checkout", "-q", "-B", "merge", "master")
        self.git("merge", "-q", "--no-ff", "-m", "merge", "pr")
        r = self.run_check(self.body("mine"), base="merge-parent")
        self.assertEqual(r.returncode, 0, r.stdout)
        self.assertIn("(1 new case(s)", r.stdout)
        # a plain branch (no second parent) falls back to the default base
        self.git("checkout", "-q", "pr")
        r = self.run_check(self.body("mine"), base="merge-parent")
        self.assertEqual(r.returncode, 0, r.stdout)


if __name__ == "__main__":
    unittest.main()
