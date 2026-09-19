#!/usr/bin/env python3
"""Executable copy of the port-driver skill's two git-state guards.

Run from the repo root:

    python3 -m unittest discover -s scripts/tests -v

`.claude/skills/port-driver/SKILL.md` decides, from git state alone, whether a
branch carries live work or the remains of a PR that already merged. Getting it
wrong is expensive in both directions: too strict and the autonomous loop Helds
on its own brand-new work (#1007); too loose and it re-creates a deleted branch
and re-opens an already-merged diff as a duplicate PR.

The rules read plausibly either way, and every revision of them so far has been
wrong in a way that only a real repository exposed -- each round of review on
#1003 found a hole introduced by the round before. So they are encoded here
against throwaway repositories rather than argued about in prose.

These functions mirror the SKILL.md text; they are not imported by anything.
**When the skill's decision list changes, change these to match** -- a drift
between them is the bug this file exists to catch, and the cases below are
named for the situations that produced them.

No network, no fixtures: each test builds the repository it needs.
"""

from __future__ import annotations

import subprocess
import tempfile
import unittest
from pathlib import Path


def git(repo: Path, *args: str) -> str:
    return subprocess.run(
        ["git", *args], cwd=repo, capture_output=True, text=True
    ).stdout.strip()


def git_ok(repo: Path, *args: str) -> bool:
    return subprocess.run(
        ["git", *args], cwd=repo, capture_output=True, text=True
    ).returncode == 0


def scenario_b(repo: Path, merged: list[str]) -> str:
    """SKILL.md scenario B: classify a branch against its merged PRs' head OIDs.

    Returns "B" (live work, carry on), "C" (this exact commit landed) or
    "D" (hold for a human). The OIDs are walked one at a time, every test
    applied to each before moving on -- see test_mixed_merge_methods for why
    a pass-per-test is wrong.
    """
    if not git(repo, "branch", "--show-current"):
        return "D"  # detached HEAD: `gh pr list --head ""` matches anything
    if not merged:
        return "B"
    head = git(repo, "rev-parse", "HEAD")
    for oid in merged:
        if not git_ok(repo, "cat-file", "-e", f"{oid}^{{commit}}"):
            continue  # never fetched; absent is not reachable
        if oid == head:
            return "C"
        if git_ok(repo, "merge-base", "--is-ancestor", oid, "origin/master"):
            continue  # merge-committed: reachable only through master
        if git_ok(repo, "merge-base", "--is-ancestor", oid, "HEAD"):
            return "D"  # squashed and still underneath us
    return "B"


def reuse_guard(repo: Path, branch: str) -> str:
    """SKILL.md section 5 step 2: may scenario C reuse an existing local ref?

    Returns "REUSE" or "HELD". Asks for the ref's own commits, not how far it
    trails master -- see test_reuse_* for the two ways the ancestor test failed.
    """
    return "HELD" if git(repo, "rev-list", f"origin/master..{branch}") else "REUSE"


class GitFixture(unittest.TestCase):
    def setUp(self) -> None:
        self._tmp = tempfile.TemporaryDirectory()
        self.repo = Path(self._tmp.name)
        self.addCleanup(self._tmp.cleanup)
        for args in (
            ("init", "-q", "-b", "master"),
            ("config", "user.email", "t@example.invalid"),
            ("config", "user.name", "t"),
        ):
            git(self.repo, *args)
        self.n = 0
        self.commit("base")
        self.publish()

    def commit(self, msg: str) -> str:
        self.n += 1
        (self.repo / "f").write_text(f"{msg}{self.n}\n")
        git(self.repo, "add", "f")
        git(self.repo, "commit", "-qm", msg)
        return git(self.repo, "rev-parse", "HEAD")

    def publish(self) -> None:
        """Point origin/master at master, as a fetch after a merge would."""
        git(self.repo, "update-ref", "refs/remotes/origin/master", "master")

    def merge_pr(self, branch: str, squash: bool) -> None:
        git(self.repo, "checkout", "-q", "master")
        if squash:
            git(self.repo, "merge", "-q", "--squash", branch)
            git(self.repo, "commit", "-qm", f"squash {branch}")
        else:
            git(self.repo, "merge", "-q", "--no-ff", branch, "-m", f"merge {branch}")
        self.publish()


class ScenarioB(GitFixture):
    """Each case is a state the autonomous loop actually reached or could."""

    def _reused_name_after(self, squash: bool) -> str:
        """A placeholder name (prep-ci) grown again at a later phase boundary."""
        git(self.repo, "checkout", "-qb", "prep-ci", "master")
        oid = self.commit("first use")
        self.merge_pr("prep-ci", squash=squash)
        git(self.repo, "branch", "-qD", "prep-ci")
        git(self.repo, "checkout", "-qb", "prep-ci", "master")
        self.commit("brand-new work")
        return oid

    def test_reused_name_after_merge_commit_is_live_work(self):
        # #1007's shape: a merge-committed head is an ancestor of every later
        # branch cut from master, so a bare --is-ancestor Helds on new work.
        oid = self._reused_name_after(squash=False)
        self.assertEqual(scenario_b(self.repo, [oid]), "B")

    def test_reused_name_after_squash_is_live_work(self):
        # A squashed head is neither equal nor an ancestor: a two-way test
        # returned no verdict at all here.
        oid = self._reused_name_after(squash=True)
        self.assertEqual(scenario_b(self.repo, [oid]), "B")

    def test_branch_left_exactly_on_merged_head(self):
        # Interrupted between `gh pr merge` and `git checkout master`.
        git(self.repo, "checkout", "-qb", "done", "master")
        oid = self.commit("work")
        self.merge_pr("done", squash=True)
        git(self.repo, "checkout", "-q", "done")
        self.assertEqual(scenario_b(self.repo, [oid]), "C")

    def test_squashed_head_plus_stray_commit_holds(self):
        # The genuinely duplicating case: pushing re-creates the deleted branch
        # with the whole already-merged diff on it.
        git(self.repo, "checkout", "-qb", "done", "master")
        oid = self.commit("work")
        self.merge_pr("done", squash=True)
        git(self.repo, "checkout", "-q", "done")
        self.commit("stray")
        self.assertEqual(scenario_b(self.repo, [oid]), "D")

    def test_merge_committed_head_plus_stray_is_live_work(self):
        # Not a duplicate: the head is already in master, so re-pushing shows
        # only the stray commit. B pushes it rather than holding.
        git(self.repo, "checkout", "-qb", "done", "master")
        oid = self.commit("work")
        self.merge_pr("done", squash=False)
        git(self.repo, "checkout", "-q", "done")
        self.commit("stray")
        self.assertEqual(scenario_b(self.repo, [oid]), "B")

    def test_detached_head_holds(self):
        git(self.repo, "checkout", "-q", "--detach")
        self.assertEqual(scenario_b(self.repo, [git(self.repo, "rev-parse", "HEAD")]), "D")

    def test_master_advanced_during_the_b_window(self):
        # Branch pushed, `gh pr create` failed, then a maintainer hotfix landed
        # through the admin bypass. origin/master is no longer an ancestor of
        # HEAD even though this is ordinary fresh work.
        oid = self._reused_name_after(squash=False)
        git(self.repo, "checkout", "-q", "master")
        self.commit("hotfix")
        self.publish()
        git(self.repo, "checkout", "-q", "prep-ci")
        self.assertEqual(scenario_b(self.repo, [oid]), "B")

    def test_mixed_merge_methods_on_one_name(self):
        # Why the OIDs are walked per-OID: an older merge-committed PR and a
        # newer squashed one on the same name. Tested as separate passes, the
        # old OID clears the branch and the squashed head -- the one actually
        # left behind -- goes unnoticed.
        git(self.repo, "checkout", "-qb", "mix", "master")
        old = self.commit("first")
        self.merge_pr("mix", squash=False)
        git(self.repo, "branch", "-qD", "mix")
        git(self.repo, "checkout", "-qb", "mix", "master")
        new = self.commit("second")
        self.merge_pr("mix", squash=True)
        git(self.repo, "checkout", "-q", "mix")
        self.commit("stray")
        self.assertEqual(scenario_b(self.repo, [old, new]), "D")

    def test_unfetched_oid_is_not_a_match(self):
        # Resumed from a fresh clone: --is-ancestor on a missing object exits
        # fatal, which is a miss, not ambiguity.
        self.assertEqual(scenario_b(self.repo, ["0" * 40]), "B")


class ReuseGuard(GitFixture):
    def test_ref_merely_behind_an_advanced_master(self):
        git(self.repo, "branch", "stale", "master")
        self.commit("advance")
        self.publish()
        self.assertEqual(reuse_guard(self.repo, "stale"), "REUSE")

    def test_squash_merged_leftover(self):
        git(self.repo, "checkout", "-qb", "left", "master")
        self.commit("work")
        self.merge_pr("left", squash=True)
        self.assertEqual(reuse_guard(self.repo, "left"), "HELD")

    def test_genuine_unpushed_work(self):
        # The ancestor test REUSED this: origin/master is an ancestor of a
        # branch carrying unpushed commits, so new work piled on top of it.
        git(self.repo, "checkout", "-qb", "wip", "master")
        self.commit("unpushed")
        self.assertEqual(reuse_guard(self.repo, "wip"), "HELD")

    def test_ref_level_with_master(self):
        git(self.repo, "branch", "even", "master")
        self.assertEqual(reuse_guard(self.repo, "even"), "REUSE")


if __name__ == "__main__":
    unittest.main()
