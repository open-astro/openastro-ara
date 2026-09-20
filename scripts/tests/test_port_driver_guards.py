#!/usr/bin/env python3
"""Executable copy of the port-driver skill's git-state guards and API-answer probes.

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

No network, no fixtures: each git-state test builds the repository it needs;
the probe mirrors are pure functions of what `gh` printed and need none.
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
PLAYBOOK = REPO_ROOT / "design" / "PORT_PLAYBOOK.md"
PR_CHECKER = REPO_ROOT / ".claude" / "commands" / "pr-checker.md"


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


def reuse_guard(
    repo: Path, branch: str, merged: list[str] = (), remote_oid: str | None = None
) -> str:
    """SKILL.md section 5 step 2: may scenario C reuse an existing local ref?

    Returns "REUSE" (switch to it), "RETIRE" (delete the local ref and
    re-create it: its head is exactly a merged PR's head, #1028) or "HELD".
    Asks for the ref's own commits, not how far it trails master -- see
    test_reuse_* for the two ways the ancestor test failed. `merged` is what
    `gh pr list --head <branch> --base master --state merged --limit 100 --json headRefOid`
    returned;
    an empty list stands in for both "no PR" and an API failure, and both
    Hold. `remote_oid` is what `git ls-remote --heads origin refs/heads/<branch>`
    printed (`remote_head` below), None when the remote ref is gone. Checked
    before anything local (#1047, #1057, #1059): a surviving origin/<branch>
    that origin/master does not contain makes the eventual push
    non-fast-forward whether the local ref is reused, retired or does not
    exist yet, so Hold; an ancestor of origin/master fast-forwards and is
    fine. An OID that is not in the local object store fails the ancestor
    test, which is the Hold direction. With no local ref the verdict is
    "CREATE" (the skill's plain `checkout -b` fallback).
    A 100-row `merged` is a possibly truncated page; that changes only the Held
    message (the miss already Holds), so the verdict is the same here.
    """
    if remote_oid and not git_ok(repo, "merge-base", "--is-ancestor", remote_oid, "origin/master"):
        return "HELD"
    if not git_ok(repo, "rev-parse", "--verify", "-q", f"refs/heads/{branch}"):
        return "CREATE"
    if not git(repo, "rev-list", f"origin/master..{branch}"):
        return "REUSE"
    ref_oid = git(repo, "rev-parse", f"refs/heads/{branch}")
    if ref_oid and ref_oid in merged:
        return "RETIRE"
    return "HELD"


def remote_head(repo: Path, branch: str) -> str | None:
    """The #1047 probe: the OID `git ls-remote --heads origin refs/heads/<branch>` prints,
    None when the remote ref is gone. A failed ls-remote raises rather than
    reading as "gone" -- the skill's `|| exit 1` on the same line.
    """
    out = git(repo, "ls-remote", "--heads", "origin", f"refs/heads/{branch}")
    return out.split("\t")[0] if out else None


def retire(repo: Path, branch: str) -> None:
    """The RETIRE action: local delete, then a fresh branch from master."""
    git(repo, "checkout", "-q", "master")
    git(repo, "branch", "-D", branch)
    git(repo, "checkout", "-qb", branch, "origin/master")


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

    def test_surviving_remote_ref_holds_instead_of_retiring(self):
        # #1047: an aborted PR or a hand merge without --delete-branch leaves
        # origin/<B> on the squashed head. Retiring would re-create the branch
        # and the eventual push is rejected non-fast-forward; §19.1 bars the
        # driver from `push --delete`, so it Holds and names the ref.
        git(self.repo, "checkout", "-qb", "left", "master")
        oid = self.commit("work")
        self.merge_pr("left", squash=True)
        self.assertEqual(reuse_guard(self.repo, "left", [oid], remote_oid=oid), "HELD")
        self.assertEqual(reuse_guard(self.repo, "left", [oid], remote_oid=None), "RETIRE")

    def test_surviving_remote_ref_holds_a_reusable_ref_too(self):
        # #1057: the same remote leftover beside a local ref that is level
        # with master (reset, or never committed to). REUSE would switch to
        # it and the push would be rejected all the same, so the probe runs
        # before either arm.
        git(self.repo, "checkout", "-qb", "left", "master")
        oid = self.commit("work")
        self.merge_pr("left", squash=True)
        git(self.repo, "branch", "-f", "left", "origin/master")
        self.assertEqual(reuse_guard(self.repo, "left", [oid], remote_oid=oid), "HELD")
        self.assertEqual(reuse_guard(self.repo, "left", [oid]), "REUSE")

    def test_surviving_remote_ref_holds_before_a_fresh_create(self):
        # #1059: no local ref at all (fresh clone, or a hand `branch -D`), but
        # origin/<B> survives at the squashed head. The plain `checkout -b`
        # fallback would walk into the same non-fast-forward push.
        git(self.repo, "checkout", "-qb", "left", "master")
        oid = self.commit("work")
        self.merge_pr("left", squash=True)
        git(self.repo, "branch", "-D", "left")
        self.assertEqual(reuse_guard(self.repo, "left", [oid], remote_oid=oid), "HELD")
        self.assertEqual(reuse_guard(self.repo, "left", [oid]), "CREATE")

    def test_remote_ref_already_in_master_is_fine(self):
        # A merge-committed head, or an empty pushed branch, is an ancestor of
        # origin/master: the push fast-forwards over it, so no Hold.
        git(self.repo, "checkout", "-qb", "left", "master")
        oid = self.commit("work")
        self.merge_pr("left", squash=False)
        self.assertEqual(reuse_guard(self.repo, "left", [oid], remote_oid=oid), "REUSE")
        base = git(self.repo, "rev-parse", "origin/master")
        self.assertEqual(reuse_guard(self.repo, "left", [oid], remote_oid=base), "REUSE")

    def test_unfetched_remote_oid_holds(self):
        # The remote OID is not in the local store: --is-ancestor fails, and a
        # failure is a Hold, not a pass.
        git(self.repo, "branch", "left", "master")
        self.assertEqual(reuse_guard(self.repo, "left", remote_oid="0" * 40), "HELD")

    def test_remote_head_probe_against_a_real_origin(self):
        # The probe itself, against a bare origin: the pushed OID after a push,
        # None after the remote delete `--delete-branch` performs.
        with tempfile.TemporaryDirectory() as bare:
            git(self.repo, "init", "-q", "--bare", bare)
            git(self.repo, "remote", "add", "origin", bare)
            git(self.repo, "checkout", "-qb", "left", "master")
            git(self.repo, "push", "-q", "origin", "left")
            self.assertEqual(remote_head(self.repo, "left"), git(self.repo, "rev-parse", "left"))
            git(self.repo, "push", "-q", "origin", "--delete", "left")
            self.assertIsNone(remote_head(self.repo, "left"))
            git(self.repo, "remote", "remove", "origin")
        with self.assertRaises(subprocess.CalledProcessError):
            remote_head(self.repo, "left")  # no origin: fails loudly, not "gone"

    def test_a_full_merged_page_still_decides_the_same_way(self):
        # #1050: 100 rows may be a truncated listing. The skill only adds a hint
        # to the Held message; an OID that IS on the page still retires.
        git(self.repo, "checkout", "-qb", "left", "master")
        oid = self.commit("work")
        self.merge_pr("left", squash=True)
        page = ["0" * 40] * 99
        self.assertEqual(reuse_guard(self.repo, "left", page + [oid]), "RETIRE")
        self.assertEqual(reuse_guard(self.repo, "left", page + ["1" * 40]), "HELD")

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


def delete_branch_flag(is_cross_repository_output: str) -> str:
    """SKILL.md section 3b / pr-checker Step 4: the `$DEL` probe.

        [ "$(gh pr view <PR> --json isCrossRepository --jq .isCrossRepository)" = "false" ] \\
          && DEL=--delete-branch || DEL=

    Returns "--delete-branch" or "". Only the literal string `false` keeps the
    flag; `true`, an empty string (API failure) and anything else drop it. The
    direction matters: inverting the test to `= "true"` would delete a
    contributor's fork branch on an API blip.
    """
    return "--delete-branch" if is_cross_repository_output == "false" else ""


PAGE_LIMIT = 100  # the `--limit 100` on every `gh pr list` the guards run


def chore_delete_verdict(me: str, rows: list[tuple[str, bool]]) -> str:
    """Playbook section 22.2: may the driver `push origin --delete chore/<name>`?

    `rows` is what `gh pr list --state all --limit 100 --head chore/<name>
    --json author,isCrossRepository` returned, as (author, isCrossRepository).
    Returns "DELETE" or "HELD". An empty `me` (the token has no user, as an
    app token does) Holds before anything is read. A full page Holds next: the
    listing truncates silently and a foreign author past the cut would
    otherwise turn a mixed set into a DELETE (#1050) -- counted before the
    cross-repo filter, since truncation happens before it. Then cross-repo
    rows (a fork's same-named branch) are discarded, at least one row must
    remain, and every remaining author must be `me`.
    """
    if not me:
        return "HELD"
    if len(rows) >= PAGE_LIMIT:
        return "HELD"
    authors = sorted({author for author, cross in rows if not cross})
    if not authors:
        return "HELD"
    return "DELETE" if authors == [me] else "HELD"


class ProbeMirrors(unittest.TestCase):
    """The two API-answer probes from #1036, pinned in the fail-safe direction (#1038).

    Unlike the git-state guards these need no repository: each is a pure
    function of what `gh` printed, so the cases enumerate the outputs.
    """

    def test_delete_branch_only_on_a_literal_false(self):
        self.assertEqual(delete_branch_flag("false"), "--delete-branch")

    def test_fork_head_keeps_its_branch(self):
        self.assertEqual(delete_branch_flag("true"), "")

    def test_api_failure_keeps_the_branch(self):
        # Empty output: rate limit, auth blip, network. Never delete on a guess.
        self.assertEqual(delete_branch_flag(""), "")

    def test_garbage_output_keeps_the_branch(self):
        for junk in ("False", "null", "error: not found", " false"):
            with self.subTest(output=junk):
                self.assertEqual(delete_branch_flag(junk), "")

    def test_chore_delete_needs_a_same_repo_pr_of_ours(self):
        self.assertEqual(chore_delete_verdict("me", [("me", False)]), "DELETE")
        self.assertEqual(chore_delete_verdict("me", [("me", False), ("me", False)]), "DELETE")

    def test_chore_delete_holds_with_no_pr(self):
        self.assertEqual(chore_delete_verdict("me", []), "HELD")

    def test_chore_delete_holds_on_a_foreign_or_mixed_author(self):
        self.assertEqual(chore_delete_verdict("me", [("someone", False)]), "HELD")
        self.assertEqual(chore_delete_verdict("me", [("me", False), ("someone", False)]), "HELD")

    def test_a_fork_pr_of_the_same_name_does_not_vouch(self):
        # #1040 item 3: my own fork's chore/foo is not origin's chore/foo.
        self.assertEqual(chore_delete_verdict("me", [("me", True)]), "HELD")
        # ...and it does not poison a genuine same-repo match either.
        self.assertEqual(chore_delete_verdict("me", [("me", False), ("someone", True)]), "DELETE")

    def test_chore_delete_holds_when_the_token_has_no_user(self):
        # `gh api user --jq .login` prints nothing under an app token.
        self.assertEqual(chore_delete_verdict("", [("", False)]), "HELD")

    def test_chore_delete_holds_on_a_full_page(self):
        # #1050: exactly --limit rows may be a truncated listing, and the
        # foreign author may be row 101. Ninety-nine of ours still delete.
        ours = [("me", False)] * 99
        self.assertEqual(chore_delete_verdict("me", ours), "DELETE")
        self.assertEqual(chore_delete_verdict("me", ours + [("me", False)]), "HELD")
        # Fork rows count toward the page too: they were returned, then discarded.
        self.assertEqual(chore_delete_verdict("me", ours + [("me", True)]), "HELD")


class MirrorPin(unittest.TestCase):
    """The mirror above is hand-synced with the prose; this makes drift fail (#1030).

    Three sections of the skill are hashed: scenario B's whole walk (the
    two-condition shell block through the closing "No OID matched -> B"
    paragraph), which `scenario_b` mirrors; the §5 step 2 fence, which
    `reuse_guard`/`retire`/`remote_head` mirror; and the reference copy
    of the §3b `$DEL` probe, whose direction `ProbeMirrors.delete_branch_flag`
    mirrors. A fourth section is the playbook's §22.2 fence, which
    `chore_delete_verdict` mirrors (#1050). Editing
    any of them without touching this file fails the Sanity job, which is the
    point: drift already happened
    once inside #1003 (the skill tested detached-HEAD inside condition 2, the
    mirror tested it first -- C vs D with the suite green) and review, not
    the tests, caught it.

    To update: re-read the changed section, bring its mirror (`scenario_b`,
    `reuse_guard`/`retire`, or `ProbeMirrors`) and its cases into line, then
    paste the new hash the failure message prints. Pasting the hash without the re-read defeats the
    test, and there is nothing here that can stop that -- it is a forcing
    function for a human look, not a proof of equivalence.
    """

    SECTIONS = {
        "scenario_b_decision_list": (
            SKILL,
            # From the two-condition fence above condition 0 through the
            # closing "No OID matched -> B" paragraph, which is scenario_b's
            # final `return "B"` -- flipping it would otherwise leave the pin
            # green.
            "Two conditions before matching, and they are different questions:",
            "**(C) No PR in flight and there is work to start or continue**",
            '8d8bc816d4656c2d',
        ),
        "step_3b_delete_branch_probe": (
            SKILL,
            # The reference copy of the $DEL probe; ProbeMirrors mirrors its
            # direction, this pin catches an inversion of the text itself.
            # Starts above the fence so its `text` opener is hashed too: flipping it
            # back to `shell` (two runnable copies on the squash path, #1040) must trip.
            "copy below is for reading, not running",
            "The fail-safe direction of that test is mirrored by `ProbeMirrors`",
            '198eae1bac6f2615',
        ),
        "step_5_reuse_fence": (
            SKILL,
            # From the fence's first statement: the `|| exit 1` rationale in
            # the RETIRE arm rests on `checkout master` having run.
            "   git checkout master && git pull --ff-only",
            "   The slash namespace is the convention",
            '0223849346f8e166',
        ),
        "playbook_22_2_chore_delete_fence": (
            PLAYBOOK,
            # The whole §22.2 fence: ME, the truncation Hold, the cross-repo
            # filter and the strict author comparison.
            "ME=$(gh api user --jq .login)",
            "`--head` matches head branch *names*",
            '219a8fff3b42b25c',
        ),
    }

    @staticmethod
    def digest(text: str) -> str:
        # Whitespace-insensitive so a reflow is not a rule change.
        return hashlib.sha256(re.sub(r"\s+", " ", text).strip().encode()).hexdigest()[:16]

    def section(self, path: Path, start: str, end: str) -> str:
        if not path.is_file():
            self.fail(f"{path} is missing: this test mirrors it and cannot run without it")
        text = path.read_text()
        try:
            i = text.index(start)
            j = text.index(end, i)
        except ValueError:
            self.fail(
                f"{path.name} no longer contains the marker line {start!r} / {end!r} "
                "that delimits a mirrored section. Re-read the section, update the "
                "mirror in this file to match, then update the marker in SECTIONS."
            )
        return text[i:j]

    def test_sections_match_the_pinned_hashes(self):
        for name, (path, start, end, expected) in self.SECTIONS.items():
            with self.subTest(section=name):
                actual = self.digest(self.section(path, start, end))
                self.assertEqual(
                    actual, expected,
                    f"{path.name} section '{name}' changed. Re-read it, update the "
                    f"mirror in this file to match, then replace the pinned hash "
                    f"with {actual!r}.",
                )


class ProbePins(unittest.TestCase):
    """Every runnable `$DEL` probe is byte-identical to the §3b reference (#1050).

    `MirrorPin` hashes only the reference (`text`) copy. The runnable copies --
    three in SKILL.md §3b and three in `/pr-checker` Step 4, one per merge
    path -- could each be inverted to `= "true"` with the suite green. So each
    file's copies are counted and compared, whitespace-normalised and with the
    `<PR>`/`<N>` placeholder unified, against the reference. Counts are pinned
    too: a dropped copy is a merge path that lost its probe.
    """

    PROBE = re.compile(
        r'\[ "\$\(gh pr view <(?:PR|N)> --json isCrossRepository --jq \.isCrossRepository\)" = "[a-z]*" \]\s*\\?\s*'
        r"&& DEL=[^ ;|]* \|\| DEL=(?=[\s;}])"
    )
    REFERENCE = (
        '[ "$(gh pr view <PR> --json isCrossRepository --jq .isCrossRepository)" = "false" ] '
        "&& DEL=--delete-branch || DEL="
    )
    EXPECTED_COPIES = {SKILL: 4, PR_CHECKER: 3}  # the reference plus 3; 2 fences plus the inline chain

    @classmethod
    def normalise(cls, probe: str) -> str:
        return re.sub(r"\s*\\?\s+", " ", probe).replace("<N>", "<PR>").strip()

    def test_every_copy_matches_the_reference(self):
        for path, expected in self.EXPECTED_COPIES.items():
            with self.subTest(file=path.name):
                text = path.read_text()
                copies = [m.group(0) for m in self.PROBE.finditer(text)]
                self.assertEqual(
                    len(copies), expected,
                    f"{path.name} carries {len(copies)} $DEL probes, expected {expected}: "
                    "a merge path lost or gained its probe",
                )
                for copy in copies:
                    self.assertEqual(self.normalise(copy), self.REFERENCE, f"in {path.name}")

    def test_no_stray_delete_branch_assignment(self):
        # Belt and braces: the flag is assigned only inside a matching probe.
        for path in self.EXPECTED_COPIES:
            with self.subTest(file=path.name):
                text = self.PROBE.sub("", path.read_text())
                self.assertNotIn("DEL=--delete-branch", text)


if __name__ == "__main__":
    unittest.main()
