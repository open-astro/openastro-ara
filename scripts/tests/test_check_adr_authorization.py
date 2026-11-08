#!/usr/bin/env python3
"""Tests for scripts/check-adr-authorization.py.

Run from the repo root:

    python3 -m unittest discover -s scripts/tests -v

The one script tested through its functions, not its command line: `decide`
and `records_lookup` talk to GitHub, so the tests give them fake permission and
record-content lookups (no network).
"""

from __future__ import annotations

import importlib.util
import json
import unittest
from pathlib import Path

SCRIPT = Path(__file__).resolve().parent.parent / "check-adr-authorization.py"
_spec = importlib.util.spec_from_file_location("check_adr_authorization", SCRIPT)
auth = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(auth)

HEAD = "0123456789abcdef0123456789abcdef01234567"
OLD = "fedcba9876543210fedcba9876543210fedcba98"
PERMS = {"maint": "maintain", "writer": "write", "admin1": "admin", "reader": "read", "triager": "triage"}
REC = ["docs/adr/0005-task-clock.md", "OpenAstroAra.Server/X.cs"]
R5 = "docs/adr/0005-task-clock.md"
R7 = "docs/adr/0007-other.md"


def decide(changed, statements, records_at=lambda _sha: None, head=HEAD):
    return auth.decide(changed, head, statements, PERMS.get, records_at)


def setattr_all(saved):
    auth.gh, auth.gh_list, auth.permission_lookup, auth.records_lookup = saved


class DecideTest(unittest.TestCase):
    def test_pr_with_no_record_passes(self):
        self.assertEqual(decide(["OpenAstroAra.Server/X.cs"], [])[0], "success")

    def test_unauthorized_record_change_fails_and_says_what_to_comment(self):
        state, desc = decide(REC, [])
        self.assertEqual(state, "failure")
        self.assertIn("ADR authorized 0123456789ab", desc)

    def test_write_maintain_and_admin_may_authorize(self):
        for login in ("writer", "maint", "admin1"):
            with self.subTest(login=login):
                self.assertEqual(decide(REC, [(login, "ADR authorized 0123456")])[0], "success")

    def test_read_triage_and_strangers_may_not(self):
        for login in ("reader", "triager", "stranger"):
            with self.subTest(login=login):
                self.assertEqual(decide(REC, [(login, "ADR authorized %s" % HEAD)])[0], "failure")

    def test_a_bot_may_not_authorize(self):
        state, _ = auth.decide(REC, HEAD, [("github-actions[bot]", "ADR authorized 0123456")], lambda _l: "admin")
        self.assertEqual(state, "failure")

    def test_wrong_short_or_embedded_authorizations_do_not_count(self):
        for body in ("ADR authorized fedcba9", "ADR authorized 012345", "I think ADR authorized 0123456 is fine",
                     "Not yet: ADR authorized 0123456"):
            with self.subTest(body=body):
                self.assertEqual(decide(REC, [("writer", body)])[0], "failure")

    def test_phrase_on_its_own_line_in_a_longer_comment_any_case(self):
        body = "Looks good.\n  ADR authorized 0123456789ABCDEF  \nThanks"
        self.assertEqual(decide(REC, [("writer", body)])[0], "success")

    def test_rename_away_from_the_directory_still_needs_authorization(self):
        """run() lists a renamed file under both names, so moving a record out of the directory is still a change."""
        posted = []

        def fake_gh_list(path):
            if "/files" in path:
                return [{"filename": "docs/other/0009-x.md", "previous_filename": "docs/adr/0009-x.md"}]
            return []

        def fake_gh(*args):
            if args[:2] == ("api", "-X"):
                posted.append(args)
            return HEAD + "\n"

        saved = (auth.gh, auth.gh_list, auth.permission_lookup, auth.records_lookup)
        auth.gh, auth.gh_list = fake_gh, fake_gh_list
        auth.permission_lookup, auth.records_lookup = (lambda repo: PERMS.get), (lambda repo: (lambda sha: None))
        self.addCleanup(lambda: setattr_all(saved))
        auth.run("o/r", "7", True)
        self.assertIn("state=failure", posted[0])

    def test_long_login_keeps_the_description_within_a_commit_status(self):
        state, desc = auth.decide(REC, HEAD, [("w" * 200, "ADR authorized 0123456")], lambda _l: "write")
        self.assertEqual(state, "success")
        self.assertLessEqual(len(desc), auth.MAX_DESCRIPTION)


class HoldsAcrossPushesTest(unittest.TestCase):
    def setUp(self):
        self.snapshots = {HEAD: {R5: "b1", "docs/adr/README.md": "i1"}, OLD: {R5: "b1", "docs/adr/README.md": "i0"}}

    def records_at(self, sha):
        return next((v for k, v in self.snapshots.items() if k.startswith(sha)), None)

    def test_older_authorization_holds_when_the_changed_records_are_the_same(self):
        state, desc = decide(REC, [("writer", "ADR authorized fedcba9")], self.records_at)
        self.assertEqual(state, "success")
        self.assertIn("same at", desc)

    def test_a_record_changed_after_the_authorization_needs_a_new_one(self):
        state, _ = decide(REC + ["docs/adr/README.md"], [("writer", "ADR authorized fedcba9")], self.records_at)
        self.assertEqual(state, "failure")

    def test_a_record_added_after_the_authorization_needs_a_new_one(self):
        new = "docs/adr/0020-new.md"

        def records_at(sha):
            snap = self.records_at(sha)
            return dict(snap, **({new: "n1"} if HEAD.startswith(sha) else {})) if snap is not None else None

        state, _ = decide(REC + [new], [("writer", "ADR authorized fedcba9")], records_at)
        self.assertEqual(state, "failure")

    def test_unresolvable_sha_does_not_count(self):
        self.assertEqual(decide(REC, [("writer", "ADR authorized abcdef1")], self.records_at)[0], "failure")

    def test_older_authorization_by_a_reader_does_not_count(self):
        self.assertEqual(decide(REC, [("reader", "ADR authorized fedcba9")], self.records_at)[0], "failure")

    def test_when_the_heads_records_cannot_be_read_only_the_head_sha_authorizes(self):
        self.assertEqual(decide(REC, [("writer", "ADR authorized fedcba9")], lambda sha: None)[0], "failure")


class AgedDescriptionTest(unittest.TestCase):
    def setUp(self):
        self.two = [R5, R7]
        self.snaps = {HEAD: {R5: "b2", R7: "o1"}, OLD: {R5: "b1", R7: "o1"}}

    def by_sha(self, sha):
        return next((v for k, v in self.snaps.items() if k.startswith(sha)), None)

    def aged(self, statements=None):
        return decide(self.two, statements or [("writer", "ADR authorized fedcba9")], self.by_sha)

    def test_names_the_record_that_differs(self):
        state, desc = self.aged()
        self.assertEqual(state, "failure")
        self.assertEqual(desc, "Authorized at fedcba9; 0005-task-clock.md changed since: comment 'ADR authorized 0123456789ab'")

    def test_a_record_absent_at_the_authorization_counts_as_differing(self):
        self.snaps[OLD] = {R7: "o1"}
        self.assertIn("0005-task-clock.md changed since", self.aged()[1])

    def test_a_record_absent_at_the_head_counts_as_differing(self):
        self.snaps[HEAD] = {R5: "b2"}
        self.assertIn("0005-task-clock.md, 0007-other.md changed since", self.aged()[1])

    def test_head_authorization_still_wins_after_an_aged_one(self):
        state, _ = self.aged([("writer", "ADR authorized fedcba9"), ("writer", "ADR authorized 0123456")])
        self.assertEqual(state, "success")

    def test_no_valid_authorization_keeps_the_count_text(self):
        self.assertTrue(self.aged([("reader", "ADR authorized fedcba9")])[1].startswith("2 decision record file(s) changed"))
        self.assertTrue(decide(self.two, [("writer", "ADR authorized fedcba9")], lambda s: None)[1]
                        .startswith("2 decision record file(s) changed"))

    def test_the_last_aged_authorization_in_statement_order_is_named(self):
        self.snaps[HEAD] = {R5: "b2", R7: "o2"}
        self.snaps["5" * 40] = {R5: "b2", R7: "o1"}
        _, desc = self.aged([("writer", "ADR authorized fedcba9"), ("writer", "ADR authorized 5555555")])
        self.assertTrue(desc.startswith("Authorized at 5555555; 0007-other.md changed since"), desc)

    def test_names_that_do_not_fit_fall_back_to_a_count_and_the_instruction_is_never_cut(self):
        long = ["docs/adr/%04d-%s.md" % (i, "x" * 30) for i in range(5)]
        self.snaps[HEAD] = {r: "n" for r in long}
        self.snaps[OLD] = {r: "o" for r in long}
        _, desc = decide(long, [("writer", "ADR authorized fedcba9")], self.by_sha)
        self.assertEqual(desc, "Authorized at fedcba9; 5 records changed since: comment 'ADR authorized 0123456789ab'")
        self.snaps[HEAD] = {r: "n" for r in long[:3]}
        self.snaps[OLD] = {r: "o" for r in long[:3]}
        _, desc = decide(long[:3], [("writer", "ADR authorized fedcba9")], self.by_sha)
        self.assertLessEqual(len(desc), auth.MAX_DESCRIPTION)
        self.assertTrue(desc.endswith("'ADR authorized 0123456789ab'"), desc)

    def test_hostile_path_names_are_escaped_to_the_count_form(self):
        for label, bad in (("newline", "docs/adr/0009-a\n::error::x.md"), ("bidi", "docs/adr/0009-a\u202eb.md")):
            with self.subTest(label=label):
                self.snaps[HEAD] = {bad: "n"}
                self.snaps[OLD] = {bad: "o"}
                _, desc = decide([bad], [("writer", "ADR authorized fedcba9")], self.by_sha)
                self.assertEqual(desc, "Authorized at fedcba9; 1 records changed since: comment 'ADR authorized 0123456789ab'")
                self.assertTrue(desc.isprintable())


class RecordsLookupTest(unittest.TestCase):
    """records_lookup over a fake GitHub API."""

    OLD_FULL, HEAD_FULL = "a" * 40, "b" * 40

    def setUp(self):
        self.trees = {self.OLD_FULL: {"0005-task-clock.md": "b1"},
                      self.HEAD_FULL: {"0005-task-clock.md": "b1", "sub/0020-new.md": "n1"}}
        self.truncated_at = None
        self.flaky = False

    def fake_gh(self, args, **_kw):
        class R:
            returncode, stdout, stderr = 0, "", ""
        r, url = R(), args[2]
        ref = url.split("ref=")[-1] if "ref=" in url else None
        if "/commits/" in url:
            full = next((f for f in self.trees if f.startswith(url.rsplit("/", 1)[-1])), None)
            body = {"sha": full} if full else None
        elif "/contents/docs?" in url:
            if self.flaky:
                r.returncode, r.stderr = 1, "gh: error connecting to api.github.com: 404 bytes read"
                return r
            body = [{"name": "adr", "path": "docs/adr", "sha": "tree-" + ref, "type": "dir"}]
        elif "/git/trees/tree-" in url:
            sha = url.split("/git/trees/tree-")[1].split("?")[0]
            body = {"truncated": sha == self.truncated_at,
                    "tree": [{"path": n, "sha": v, "type": "blob"} for n, v in self.trees[sha].items()]
                    + [{"path": d, "sha": "d", "type": "tree"} for d in {n.split("/")[0] for n in self.trees[sha] if "/" in n}]}
        else:
            body = None
        if body is None:
            r.returncode, r.stderr = 1, "gh: Not Found (HTTP 404)"
            return r
        r.stdout = json.dumps(body)
        return r

    def lookup(self):
        return auth.records_lookup("o/r", run=self.fake_gh)

    def test_record_added_in_a_subdirectory_after_the_authorization_needs_a_new_one(self):
        nested = REC + ["docs/adr/sub/0020-new.md"]
        state, _ = auth.decide(nested, self.HEAD_FULL, [("writer", "ADR authorized aaaaaaa")], PERMS.get, self.lookup())
        self.assertEqual(state, "failure")

    def test_unchanged_records_keep_the_authorization(self):
        state, _ = auth.decide(REC, self.HEAD_FULL, [("writer", "ADR authorized aaaaaaa")], PERMS.get, self.lookup())
        self.assertEqual(state, "success")

    def test_commit_the_api_cannot_find_has_no_snapshot(self):
        self.assertIsNone(self.lookup()("c" * 7))

    def test_truncated_tree_has_no_snapshot(self):
        self.truncated_at = self.OLD_FULL
        self.assertIsNone(self.lookup()(self.OLD_FULL))

    def test_api_error_that_only_mentions_404_is_not_a_missing_docs_directory(self):
        self.flaky = True
        self.assertIsNone(self.lookup()(self.OLD_FULL))


if __name__ == "__main__":
    unittest.main()
