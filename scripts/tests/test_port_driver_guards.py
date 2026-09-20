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

import hashlib
import re
import subprocess
import tempfile
import unittest
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent.parent
SKILL = REPO_ROOT / ".claude" / "skills" / "port-driver" / "SKILL.md"


def git(repo: Path, *args: str) -> str:
    """Run a git command that must succeed, and return its stdout.

    `check=True` pins the fixtures as well as the guards: a silently failing
    setup step (a `branch -D` that errors, say) would otherwise leave HEAD
    somewhere unintended and let a case pass for the wrong reason. Commands
    whose failure is meaningful go through `git_ok` instead.
    """
    return subprocess.run(
        ["git", *args], cwd=repo, capture_output=True, text=True, check=True
    ).stdout.strip()


def git_ok(repo: Path, *args: str) -> bool:
    return subprocess.run(
        ["git", *args], cwd=repo, capture_output=True, text=True
    ).returncode == 0


def scenario_b(repo: Path, merged: list[str]) -> str:
    """SKILL.md scenario B: classify a branch against its merged PRs' head OIDs.

    Returns "B" (live work, carry on), "C" (nothing to push yet, or this exact
    commit landed) or "D" (hold for a human). Mirrors the skill's conditions in
    the order the skill states them: (0) not on a branch, (1) commits ahead,
    (2) the merged-head walk. The order is part of what is pinned -- a detached
    HEAD at master's head is zero commits ahead, so (0) before (1) is the
    difference between D and C. The
    OIDs are walked one at a time, every test applied to each before moving on
    -- see test_mixed_merge_methods_on_one_name for why a pass-per-test is
    wrong.
    """
    if not git(repo, "branch", "--show-current"):
        return "D"  # condition (0): `gh pr list --head ""` matches anything
    # Condition (1): zero commits ahead is not B. Pushing an empty branch makes
    # `gh pr create` fail with "No commits between master and <branch>" and
    # strands a ref §19.1 bars the driver from deleting.
    if git(repo, "rev-list", "--count", "origin/master..HEAD") == "0":
        return "C"
    # Condition (2), checked only when (1) found commits.
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


def reuse_guard(repo: Path, branch: str, merged: list[str] = ()) -> str:
    """SKILL.md section 5 step 2: may scenario C reuse an existing local ref?

    Returns "REUSE" (switch to it), "RETIRE" (delete the local ref and
    re-create it: its head is exactly a merged PR's head, #1028) or "HELD".
    Asks for the ref's own commits, not how far it trails master -- see
    test_reuse_* for the two ways the ancestor test failed. `merged` is what
    `gh pr list --head <branch> --state merged --json headRefOid` returned;
    an empty list stands in for both "no PR" and an API failure, and both
    Hold.
    """
    if not git(repo, "rev-list", f"origin/master..{branch}"):
        return "REUSE"
    ref_oid = git(repo, "rev-parse", f"refs/heads/{branch}")
    if ref_oid and ref_oid in merged:
        return "RETIRE"
    return "HELD"


def retire(repo: Path, branch: str) -> None:
    """The RETIRE action: local delete, then a fresh branch from master."""
    git(repo, "checkout", "-q", "master")
    git(repo, "branch", "-D", branch)
    git(repo, "checkout", "-qb", branch, "master")


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

    def test_zero_commits_ahead_is_not_b(self):
        # Skill condition (1): a branch §5 step 2 created, interrupted before
        # any commit. B would push it empty and strand the ref.
        git(self.repo, "checkout", "-qb", "fresh", "master")
        self.assertEqual(scenario_b(self.repo, []), "C")

    def test_zero_commits_ahead_outranks_the_oid_walk(self):
        # (1) decides before (2) is consulted: same empty branch, but a PR
        # merged under this name earlier.
        git(self.repo, "checkout", "-qb", "prep-ci", "master")
        oid = self.commit("first use")
        self.merge_pr("prep-ci", squash=True)
        git(self.repo, "branch", "-qD", "prep-ci")
        git(self.repo, "checkout", "-qb", "prep-ci", "master")
        self.assertEqual(scenario_b(self.repo, [oid]), "C")

    def test_unfetched_oid_is_not_a_match(self):
        # Resumed from a fresh clone: --is-ancestor on a missing object exits
        # fatal, which is a miss, not ambiguity.
        git(self.repo, "checkout", "-qb", "work", "master")
        self.commit("live work")
        self.assertEqual(scenario_b(self.repo, ["0" * 40]), "B")


class ReuseGuard(GitFixture):
    def test_ref_merely_behind_an_advanced_master(self):
        git(self.repo, "branch", "stale", "master")
        self.commit("advance")
        self.publish()
        self.assertEqual(reuse_guard(self.repo, "stale"), "REUSE")

    def test_squash_merged_leftover_with_no_merged_pr_known_holds(self):
        # An API failure (or genuinely no PR) leaves `merged` empty: the
        # commits are unpushed work as far as the driver can tell.
        git(self.repo, "checkout", "-qb", "left", "master")
        self.commit("work")
        self.merge_pr("left", squash=True)
        self.assertEqual(reuse_guard(self.repo, "left", []), "HELD")

    def test_squash_merged_leftover_is_retired(self):
        # #1028: `--delete-branch` removed the remote ref, the local one
        # survived, and its head IS the merged PR's head. Every byte on it is
        # in master by content, so the local ref may go and the name is
        # reusable -- the prep-ci at 0.5p -> 4 -> 11 case that Held the loop.
        git(self.repo, "checkout", "-qb", "left", "master")
        oid = self.commit("work")
        self.merge_pr("left", squash=True)
        self.assertEqual(reuse_guard(self.repo, "left", [oid]), "RETIRE")
        retire(self.repo, "left")
        self.assertEqual(git(self.repo, "rev-list", "--count", "origin/master..left"), "0")
        self.assertEqual(reuse_guard(self.repo, "left", [oid]), "REUSE")

    def test_squash_merged_leftover_plus_stray_commit_holds(self):
        # Exact equality only: a commit on top of the merged head is either
        # stray or real work, and the driver cannot tell which.
        git(self.repo, "checkout", "-qb", "left", "master")
        oid = self.commit("work")
        self.merge_pr("left", squash=True)
        git(self.repo, "checkout", "-q", "left")
        self.commit("stray")
        self.assertEqual(reuse_guard(self.repo, "left", [oid]), "HELD")

    def test_merge_committed_leftover_is_plain_reuse(self):
        # A merge-committed head is an ancestor of origin/master, so the ref
        # has no commits of its own: REUSE, no deletion needed.
        git(self.repo, "checkout", "-qb", "left", "master")
        oid = self.commit("work")
        self.merge_pr("left", squash=False)
        self.assertEqual(reuse_guard(self.repo, "left", [oid]), "REUSE")

    def test_older_merged_head_does_not_retire_a_newer_leftover(self):
        # A reused name with two merged PRs: the ref sits on the NEWER squashed
        # head plus nothing else, so it retires -- but only because the newer
        # OID is in the list. With only the older OID known, it Holds.
        git(self.repo, "checkout", "-qb", "mix", "master")
        old = self.commit("first")
        self.merge_pr("mix", squash=True)
        git(self.repo, "branch", "-qD", "mix")
        git(self.repo, "checkout", "-qb", "mix", "master")
        new = self.commit("second")
        self.merge_pr("mix", squash=True)
        self.assertEqual(reuse_guard(self.repo, "mix", [old]), "HELD")
        self.assertEqual(reuse_guard(self.repo, "mix", [old, new]), "RETIRE")

    def test_genuine_unpushed_work(self):
        # The ancestor test REUSED this: origin/master is an ancestor of a
        # branch carrying unpushed commits, so new work piled on top of it.
        # And a merged PR under the same name whose head is NOT this commit
        # must not change that.
        git(self.repo, "checkout", "-qb", "wip", "master")
        self.commit("unpushed")
        self.assertEqual(reuse_guard(self.repo, "wip"), "HELD")
        self.assertEqual(reuse_guard(self.repo, "wip", ["0" * 40]), "HELD")

    def test_ref_level_with_master(self):
        git(self.repo, "branch", "even", "master")
        self.assertEqual(reuse_guard(self.repo, "even"), "REUSE")


class MirrorPin(unittest.TestCase):
    """The mirror above is hand-synced with SKILL.md; this makes drift fail (#1030).

    Two sections of the skill are hashed: scenario B's decision list
    (condition 0 through the end of the D bullet) and the §5 step 2 fence
    that `reuse_guard`/`retire` mirror. Editing either without touching this
    file fails the Sanity job, which is the point: drift already happened
    once inside #1003 (the skill tested detached-HEAD inside condition 2, the
    mirror tested it first -- C vs D with the suite green) and review, not
    the tests, caught it.

    To update: re-read the changed section, bring `scenario_b` /
    `reuse_guard` and their cases into line, then paste the new hash the
    failure message prints. Pasting the hash without the re-read defeats the
    test, and there is nothing here that can stop that -- it is a forcing
    function for a human look, not a proof of equivalence.
    """

    SECTIONS = {
        "scenario_b_decision_list": (
            # From the two-condition fence above condition 0 through the
            # closing "No OID matched -> B" paragraph, which is scenario_b's
            # final `return "B"` -- flipping it would otherwise leave the pin
            # green.
            "Two conditions before matching, and they are different questions:",
            "**(C) No PR in flight and there is work to start or continue**",
            '5cb3449b39d1bbd4',
        ),
        "step_5_reuse_fence": (
            "   # Reuse the branch if it is already there.",
            "   The slash namespace is the convention",
            '62837b960298902f',
        ),
    }

    @staticmethod
    def digest(text: str) -> str:
        # Whitespace-insensitive so a reflow is not a rule change.
        return hashlib.sha256(re.sub(r"\s+", " ", text).strip().encode()).hexdigest()[:16]

    def section(self, start: str, end: str) -> str:
        text = SKILL.read_text()
        try:
            i = text.index(start)
            j = text.index(end, i)
        except ValueError:
            self.fail(
                f"SKILL.md no longer contains the marker line {start!r} / {end!r} "
                "that delimits a mirrored section. Re-read the section, update the "
                "mirror in this file to match, then update the marker in SECTIONS."
            )
        return text[i:j]

    def test_sections_match_the_pinned_hashes(self):
        for name, (start, end, expected) in self.SECTIONS.items():
            with self.subTest(section=name):
                actual = self.digest(self.section(start, end))
                self.assertEqual(
                    actual, expected,
                    f"SKILL.md section '{name}' changed. Re-read it, update the "
                    f"mirror in this file to match, then replace the pinned hash "
                    f"with {actual!r}.",
                )


if __name__ == "__main__":
    unittest.main()
