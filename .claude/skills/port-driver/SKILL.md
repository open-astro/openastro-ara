---
name: port-driver
description: Drive the openastro-ara port end-to-end — pick up the next sub-PR per design/COMMIT-PR-RULES.md, build, open the PR, poll the claude[bot] review, merge under the §19.1 gate, advance PORT_PROGRESS.md, repeat. Designed to be invoked under `/loop /port-driver` (autonomous, self-paced) so it survives disconnects.
---

# port-driver

**You are driving the openastro-ara port autonomously.** The user is offline / may disconnect. Your job is to make forward progress every iteration without losing state. The canonical references are:

- `design/PORT_PLAYBOOK.md` (~12k lines — read sections on demand, do not load whole)
- `design/COMMIT-PR-RULES.md` (the rhythm + review loop + §19.1 merge-gate)
- `design/PORT_PROGRESS.md` (current phase + last merged sub-PR — **always re-read on each loop iteration**, it's the source of truth)
- `design/PORT_TODO.md` (out-of-scope review findings deferred)
- `design/PORT_DECISIONS.md` (locked-in decisions)

Plus the user's auto-memory under `~/.claude/projects/-Users-joey-Documents-GitHub-openastro-ara/memory/` — if a `MEMORY.md` index exists there, re-check it each iteration and read any entry relevant to the merge gate. Treat the directory as possibly empty; it is an index, not a dependency.

## One-line stop conditions

Stop the loop (omit `ScheduleWakeup`) when any of these are true. Post a final status note to the user and exit:

1. The user has explicitly paused work (a comment on an open PR saying "pause", "stop", "hold", or similar — check the most recent PR comments by `@joeytroy`).
2. You hit a `Held for human review @joeytroy — <reason>` situation in two consecutive iterations on the same PR with no intervening successful work (the counter resets the moment any iteration produces a fix push, a CI pass, a reply to a review finding, or any other non-Held outcome).
3. PORT_PROGRESS.md shows the port is complete (Phase 15 merged and `v0.0.1-ara.1` tagged).
4. A `dotnet build` or pre-PR gate fails twice in a row on the same fix attempt (don't ping-pong).
5. Git state is unexpectedly dirty or on an unknown branch (investigate, don't auto-recover).

## Per-iteration procedure

Each loop iteration is **one focused unit of work** + a self-paced wake-up. Do not try to do every step every iteration. Branch on state and do just the next useful thing.

### Step 1 — Orient (always)

Run in parallel:

- `git fetch origin` — every scenario-B check below compares against
  `origin/master`, and after a fresh clone or an interrupted iteration the
  remote ref is stale
- `git status --short && git branch --show-current`
- `git log -1 --oneline`
- `gh pr list --state open --json number,title,headRefName,baseRefName,author,updatedAt`
- Read `design/PORT_PROGRESS.md` (whole file — it's ≤200 lines)

If a `MEMORY.md` index is loaded, read any entry it lists that bears on merge authority or the review gate.

### Step 2 — Decide the branch state

Pick exactly one of these scenarios, checking in the order **A → B → C → D** (highest priority first). Don't multi-task.

**Allowlisted branch** means `phase/<N>[-<letter>]-<short-name>` and the named
prep branches (`prep-*`, e.g. `prep-ci`) from §19.1's branch allowlist, plus
`rules-*` (§22.2 lists both as branches the driver created and may delete) and
`chore/<short-name>`. Anything outside that set is genuinely unknown and belongs
in scenario D.

**Deliberate deviation (3 of 3) — `chore/<short-name>`.** §19.1 does not name it; it
closes with "All other branches are off-limits without explicit user
instruction", and `COMMIT-PR-RULES.md:337` records `chore/*` as the *community*
convention, distinct from the port's pattern. It is here because the driver's
own maintenance PRs use it (#1000, #1003) and without it the loop stopped on its
own work. Issue #1013 asks the maintainer to add it to §19.1; until that lands,
treat this as a deviation, not as something §19.1 says.

Because `chore/*` is also what outside contributors use, scenario A's
"not authored by you" clause deliberately excludes it: the driver never adopts a
`chore/*` PR it did not open. Recognising a branch, adopting someone's PR on it,
and creating one are three different permissions. §5 still only ever *creates*
`phase/…` — the phase naming is what `PORT_PROGRESS.md` and the
`COMMIT-PR-RULES.md` sub-split tables are keyed on.

There is no promotion scenario: under the master-only model (playbook §22.0) the merge to `master` **is** the integration. A phase boundary is a tag, handled inside §3b at merge time, not a separate iteration.

**(A) Open PR exists and you authored it — or it is the active sub-PR on a `phase/*`, `prep-*` or `rules-*` branch.**
(Not `chore/*`: see the deviation note above. A `chore/*` PR you did not author is scenario D.)
→ Go to §3 (review poll/fix loop).

**(B) On an allowlisted sub-branch carrying unmerged work, with no open PR** — whether or not the commits are pushed.
→ Run pre-PR gate, push if needed, open the PR (§4), then schedule a wake-up to start polling.
A fully-pushed branch with no PR is this case, not an ambiguous one: it is what a
successful `git push` followed by a failed `gh pr create` leaves behind, and
`gh pr create` on an already-pushed branch is the correct recovery.

Two conditions before matching, and they are different questions:

```shell
git fetch --prune origin
git rev-list --count origin/master..HEAD   # (1) is there anything here?
gh pr list --head "$(git branch --show-current)" --state merged \
  --json number,headRefOid --jq '.[] | select(.headRefOid == "'"$(git rev-parse HEAD)"'")'
                                           # (2) was THIS commit already merged?
```

1. **Zero commits ahead is not B.** A branch created by §5 whose work was
   interrupted before any commit would be pushed empty, and `gh pr create` then
   fails with `No commits between master and <branch>` — the phase's actual work
   is skipped and a stale branch is stranded on origin that §19.1 bars the driver
   from deleting. Zero ahead → scenario C (continue the work) or D.
2. **Already-merged work is not B either, and the commit count will not tell
   you** (check this only when (1) found commits — at zero ahead, (1) has already
   decided). §3b's default is `--squash`, which lands a *new* commit on `master`,
   so the branch's own commits never become ancestors of `origin/master` and the
   count stays > 0 forever after the merge. Ask whether **this exact commit** was
   merged: a merged PR for this head whose `headRefOid` equals `git rev-parse
   HEAD`. If so, pushing would **re-create the branch `--delete-branch` just
   removed** and open a duplicate PR → check out `master`, pull, and continue at
   scenario C. That is recoverable, not ambiguous: it is what an interruption
   between `gh pr merge` and §3b's `git checkout master` looks like.

   Both halves of that test are load-bearing, and each was wrong on its own in
   an earlier draft:

   - **Name alone is wrong.** `gh pr list --head <branch> --state merged` matches
     the ref *name*, and names are reused — `prep-ci` carries a merged PR from
     its first use, and §19.1/§19.5 have the driver grow that placeholder at
     later phase boundaries. Matching on name alone fires on live work and stops
     the loop on the driver's own commits.
   - **Comparing trees is wrong too.** `git diff --quiet origin/master HEAD` only
     answers "is master identical to this branch", which stops being true the
     moment anything else lands on `master` — another merge, or a maintainer
     hotfix through the admin bypass. Then the trees differ, the guard passes,
     and B re-creates the deleted branch anyway.

   `headRefOid == HEAD` is immune to both: it names a specific commit, and it
   stays true however far `master` advances afterwards.

**(C) No PR in flight and there is work to start or continue** — either on `master` after a merge advanced the phase, or on an allowlisted branch that carries no unmerged work yet (the zero-commits-ahead case B hands over).
→ Pick the next sub-PR per the COMMIT-PR-RULES.md table + PORT_PROGRESS.md, create the branch if you are not already on it, do the work (§5).

**(D) Anything ambiguous (unknown branch, conflicting state, broken working tree).**
→ Stop. Post a status note to the user. Do not schedule another wake-up.

### Step 3 — Review poll/fix loop (scenario A)

This implements COMMIT-PR-RULES.md "Review loop (AI-driven)". The reviewer is
`claude[bot]`, posted by `.github/workflows/claude-review.yml`, which runs on
`opened` and `synchronize` — pushing a fix is what re-reviews a PR. There is no
rate limit and no fallback path: if no review has appeared, it is still running
or the workflow failed, and both are waits, not reasons to self-review.

**One PR class never gets a review: a PR that edits `claude-review.yml`.** The
action refuses to run when the workflow differs from the default branch, and the
workflow's assert step exempts exactly that case (`claude-review.yml:218-228`,
and `:437-447` on the fork path),
exiting 0 with a warning and no comment. Since the safety net routes
`.github/workflows/` changes into their own infra sub-PR, the driver authors
these. Waiting on a review that can never arrive would spin forever — see step 6.

1. `gh pr checks <N>` — if any check is `fail`, treat findings as a fix opportunity:
   - Read the failing job's log via `gh run view <run-id> --log-failed`
   - Fix the underlying issue (do not skip hooks; do not retry blindly twice in a row)
   - Commit + push with a message like `fix(ci): <one-line>` and re-poll next iteration

   The `review` check failing means the workflow errored (it asserts a comment was
   posted). Read its log rather than assuming a review exists.

2. Pull the review — `{owner}` and `{repo}` are placeholders you must substitute with the actual GitHub coordinates (`open-astro` and `openastro-ara` for this repo):
   ```shell
   gh api repos/{owner}/{repo}/issues/<N>/comments --jq '.[] | {user: .user.login, created_at, updated_at, body: (.body | .[0:400])}'
   gh api repos/{owner}/{repo}/pulls/<N>/comments    --jq '.[] | {user: .user.login, path, line, body: (.body | .[0:400])}'
   ```
   The review is the comment by `claude[bot]` (or `github-actions[bot]` on the
   fork path) **whose `updated_at` is at or after your last push**. Select on
   `updated_at`, not `created_at`, and never just take the newest comment: the
   workflow sets `use_sticky_comment: true`, so a round-2 review may arrive as an
   *edit* of the round-1 comment, leaving `created_at` pinned to round 1. This is
   the same test the workflow's own assert step uses (`claude-review.yml:237`, and `:456` on the
   fork path: `select(.updated_at >= $since)`). A comment older than your last push is the
   previous round's verdict on code you have already changed — not a gate.

   **Also require the body to carry a sign-off marker** — `Approved` or
   `Issues found`. The sticky comment can be created or updated at run start with
   in-progress content, so a fresh `updated_at` alone does not mean the verdict
   has landed. This is the same grep the workflow's assert step uses. A body with
   a fresh timestamp but no marker is a review still being written: keep polling.

   Read the **comment body, not the check status** — a green `review` check means
   "a comment was posted", never "the comment was clean".

3. **Interpret the review by its two sections** — the rubric is a builder/checker
   split, and the distinction is the whole gate. The authoritative defect list is
   the prompt in `claude-review.yml` (search `DEFECTS block the merge`); the table
   below is a summary that can drift, so read the workflow if the two disagree:

   | Section | Meaning | Blocks merge? |
   |---|---|---|
   | **Defects** | Wrong result on a realistic input, untested changed behaviour, security hole, stale test/CI/doc/`design/*.md`, crash/hang/race/leak, non-Alpaca equipment access, blocking I/O on the per-frame or per-poll path, or a PR description that does not match the diff | **Yes** |
   | **Notes** | Hardening, alternatives, "correct today but fragile", follow-ups, out-of-scope and pre-existing issues. At most five. | **No** |

   A review with an empty Defects section is a pass — merge on it. Do not treat
   Notes as blocking, and do not fix them in the same PR when doing so would
   widen it beyond its description; open an issue or append to
   `design/PORT_TODO.md` instead.

4. **For each unaddressed Defect:**

   | Finding | Action |
   |---|---|
   | Trivial / nit / formatting | Fix, commit, push, reply `Fixed in <sha>` |
   | Real bug / correctness | Fix, commit, push, reply `Addressed in <sha>` |
   | Disagreement / contradicts playbook | Reply with reasoning + playbook §ref, no code change |
   | Out-of-scope | Append entry to `design/PORT_TODO.md`, reply `Acknowledged — tracked in design/PORT_TODO.md` |

   If the same issue ping-pongs >2× on the same thread, post `Deferring this to human review — see comments above` and stop touching that thread.

   Pushing a fix retriggers the workflow (`synchronize`). The verdict may arrive
   as a new comment or as an in-place edit of the existing one, so re-poll on
   `updated_at` rather than waiting for a new comment id.

5. **Quiescence check** (merge-gate clearance per §19.1):
   - Green CI on `gh pr checks <N>` (all required checks `pass`)
   - A `claude[bot]` review comment **for the current head** (`updated_at` ≥ your last push) **carrying a sign-off marker**, with **no unaddressed Defects**
   - ≥3 minutes since the most recent of (last commit, last bot/user comment) — use `updated_at` from `gh api`, for the same sticky-comment reason
   - Clean self-review against scope
   - **At a phase boundary:** the `phase-<N>-complete` tag (and any applicable
     `phase-<N>-<letter>-complete`) will be pushed as the first action of §3b,
     onto this PR's head, before the merge — that is §19.1's own gate item. It
     is not yet true when you evaluate this checklist; what you are checking is
     that you have identified this as a phase boundary at all.

   If all clear → **merge** (§3b).
   If any gate is ambiguous → post `Held for human review @joeytroy — <reason>` and stop the loop.

6. **If no review comment has appeared yet**, branch on *why*:

   - **The PR changes `.github/workflows/claude-review.yml`** — check with
     `gh pr diff <N> --name-only`. No review will ever be posted (see above).
     Post `Held for human review @joeytroy — PR edits claude-review.yml, the
     review action self-skips; needs eye review` and **stop the loop**. Do not
     merge on CI alone; do not keep re-polling.
   - **The PR head is a fork without the `safe-to-review` label** — same
     outcome: nothing will run until a maintainer applies the label. Post
     `Held for human review @joeytroy — fork PR awaiting safe-to-review` and stop.
   - **`gh pr checks <N>` shows `review` itself failed** — that is a workflow
     error, not a missing review; handle it under step 1.
   - **Otherwise** the run is still in flight. Schedule the next wake-up and
     re-poll — do not substitute `/review` and do not merge on CI alone.

   Re-polling is only ever correct in the last case. If you have re-polled more
   than ~6 times (roughly 30 minutes) with no comment and no in-flight run in
   `gh run list`, treat it as ambiguous: post `Held for human review @joeytroy —
   no review after N polls, no run in flight` and stop.

### Step 3b — Merge

Every PR targets `master` (playbook §22.0). Pick the merge method by the PR's
commit history — **except at a phase boundary, where the tag decides it**:

- **Single-commit PR, or a multi-commit one that should land as one logical change:** `gh pr merge <N> --squash --delete-branch` — the default
- **PR where per-commit granularity is worth keeping:** `gh pr merge <N> --merge --delete-branch`
- **Any PR that carries a phase or sub-phase tag:** `gh pr merge <N> --merge --delete-branch` — see below. This is keyed on *carrying a tag*, not on being the phase's last PR: a `phase-<N>-<letter>-complete` sub-phase milestone is routinely some other PR, and squashing it would orphan its tag exactly as described below.

**Phase boundary — tag first, then merge with `--merge`.** If this is the last
PR of a phase (consult the COMMIT-PR-RULES.md sub-split tables and
PORT_PROGRESS.md), push the tag *before* merging, per playbook §22.1 step 4:

```shell
# Name the ref. Do NOT rely on what is checked out — on a clean first-round
# review the driver never leaves `master`, and an unqualified `git tag` would
# then tag master's head: the commit BEFORE this PR, which still satisfies
# §19.1's "tag has been pushed" check while silently excluding the phase's
# final PR from the milestone.
git fetch --prune origin
git tag phase-<N>-complete origin/<branch-name>
git push origin phase-<N>-complete    # deliberate: see the note below
```

Confirm the tag points where you meant **before** merging —
`git log -1 --oneline phase-<N>-complete` should be the PR's head commit. The
merge is deliberately not in the block above, so that a driver running the block
verbatim cannot merge ahead of that check:

```shell
gh pr merge <N> --merge --delete-branch
```

If `git tag` fails with "tag already exists" — a `git fetch` pulled it, or a
previous §3b attempt got as far as tagging before being held — do **not** force
it. Check where the existing tag points: if it is already the PR head, skip the
`git tag` but **still run the push** (`git push origin <tag>` is idempotent, and
one of those causes leaves the tag local-only, which would merge the boundary
with no tag on origin and violate the §19.1 gate item this section exists to
satisfy); if it points anywhere else, post `Held for human review @joeytroy —
phase-<N>-complete already exists on a different commit` and stop.

A plain `git fetch` will not pull a tag pointing at a commit unreachable from
the fetched refs, so the tag can exist **on origin only**, at a different
commit, while `git tag` succeeds locally and the check above passes. The push is
then rejected as a non-fast-forward tag update. Do not force it — that is the
same situation, so take the same Held stop.

**Deliberate deviation (1 of 3):** the rule above — *any* PR carrying a phase or
sub-phase tag merges with `--merge` — is stricter than §19.1 and §22.1 step 5,
which pick the method from the PR's commit history. Not a contradiction (a merge
commit is already an allowed choice there), but it removes the discretion those
sections grant, because a squash would orphan the tag. Same maintainer
reconciliation as the deviation below.

**Deliberate deviation (2 of 3):** §22.1 step 4 and §19.1 both say `git push --tags`.
This pushes the named ref instead, because `--tags` pushes *every* stray local
tag — including the `backup-<timestamp>` tags §19.1 itself requires before a
`reset --hard`. Same result for this tag, fewer accidents. Don't "fix" it back;
the playbook lines are user-authoritative and reconciling them needs the
maintainer.

Sub-phase tags are `phase-<N>-<letter>-complete` where the sub-phase is a
coherent milestone — judgment call.

**If the merge fails after the tag is pushed** — a check flips red, a conflict
appears, or branch protection rejects the merge commit itself (which is what
would happen if `required_linear_history` were ever turned on; it is off today,
and there is deliberately no automatic fallback to `--squash`, because that
would orphan the tag just pushed) — the tag is already on origin pointing
at an unmerged commit, and §19.1 bars deleting remote refs without explicit
instruction. Do not retry blindly and do not delete it: post
`Held for human review @joeytroy — phase-<N>-complete pushed but the merge
failed` and stop the loop.

**Why `--merge` and not `--squash` here.** A squash merge replaces the branch
head with a new commit and `--delete-branch` removes the branch, so a tag on
that head ends up unreachable from `master` — invisible to `git describe master`
and `git log master`. A merge commit keeps the tagged commit as an ancestor, so
the milestone survives. This is not a special case invented here: §22.1 step 5
already picks merge-commit "where per-commit granularity matters", and §22.3
prescribes merge-commit for the phase-15 release PR for the same reason.
(Checked on the repo: `allow_merge_commit: true`, and `master`'s protection
allows merge commits. Note the control is a repository **ruleset** — see
`design/PORT_DECISIONS.md` "master protection" — so re-verify there rather than
in the classic branch-protection object.)

Tagging before the merge also satisfies the §19.1 gate condition the driver is
required to check — *"at a phase boundary: verify the expected
`phase-N-complete` tag has been pushed **for the work being merged**"* (search
`PORT_PLAYBOOK.md` §19.1 for that sentence; don't cite it by line number, the
file moves). Deferring the tag until after the merge would fail that check and
stall the loop at every phase boundary.

After merge:

```shell
git checkout master && git pull --ff-only
```

Then go to scenario C next iteration.

Update `design/PORT_PROGRESS.md` "Completed" section in the same commit pattern the prior phase entries use.

**Always fold it into a PR.** There is no direct-commit path for the driver —
not because the server would refuse it (it would not; see the safety net on
admin bypass) but because it would bypass the §19.1 gate. Include the
PORT_PROGRESS.md edit in the next sub-PR's commits so it reviews alongside the
code work. If the tracking update is the only thing outstanding, it rides along
with the next sub-PR rather than getting a PR of its own.

### Step 4 — Open the PR (scenario B)

1. Run pre-PR gate. If `scripts/pre-pr-check.sh` exists, run it. Otherwise the minimum gate is:

   ```shell
   dotnet build OpenAstroAra.Server/OpenAstroAra.Server.csproj -c Release
   # When the touched files include a domain project (Core / Astrometry /
   # Equipment / Image / Profile / PlateSolving / Test), additionally:
   dotnet build OpenAstroAra.<TouchedProject>/OpenAstroAra.<TouchedProject>.csproj -c Release
   ```

   **Why Server, not the whole solution:** per `OpenAstroAra.Server.csproj` comments, Server is the cross-platform daemon artifact (`net10.0`, AOT-published in Release) and is the only project CI grows into gating (Phase 4 expansion in `.github/workflows/ci.yml`). Inherited domain projects stay `net10.0-windows` with WPF until each is made cross-platform-clean per §26 / Phase 4+. The whole-solution build is currently blocked on `OpenAstroAra.Sequencer` (96 errors from `NINA.WPF.Base` references, tracked in `design/PORT_TODO.md` as a separate `phase-0.5p-followup-sequencer` pass). Once that lands, this minimum gate can widen back to `dotnet build OpenAstroAra.sln -c Release`.

   Non-zero exit = fix + retry once. Twice failing in a row = stop and notify user.

2. **CHANGELOG entry (convention-only enforcement).** If this sub-PR introduces a user-visible change (new feature, behavior change, removal, bug fix, security fix), add a bullet to the `[Unreleased]` section of `CHANGELOG.md` in this same diff under the matching subsection (Added / Changed / Deprecated / Removed / Fixed / Security). Mechanical PRs (pure renames, pure deletes during demolition, doc-only edits to internal `design/` files) are exempt — when in doubt, add one. See `CHANGELOG.md` "How to update this file" for the full rule.

3. Push the branch: `git push -u origin <branch-name>`

4. Open the PR:
   ```shell
   gh pr create --base master --head <branch-name> --title "<conventional-prefix>: <one-line>" --body "$(cat <<'EOF'
   ## Summary
   <1-3 bullets — what + why>

   ## Scope (COMMIT-PR-RULES.md row)
   <sub-PR row from the table — e.g., "Phase 0.5l — Rename NINA.Sequencer → OpenAstroAra.Sequencer">

   ## Test plan
   - [ ] dotnet build green
   - [ ] <any phase-specific verification>

   🤖 Generated with [Claude Code](https://claude.com/claude-code)
   EOF
   )"
   ```

5. Schedule next wake-up at 270s — give the review workflow time to start its pass.

### Step 5 — Start the next sub-PR (scenario C)

1. Re-read PORT_PROGRESS.md "Next" section + the COMMIT-PR-RULES.md table to identify which sub-PR comes next.

2. Get onto the branch (playbook §22.1 step 1). If you are already on it — the
   zero-commits-ahead hand-off from scenario B — skip the create, or
   `git checkout -b` dies with `a branch named '…' already exists`:
   ```shell
   # only when not already on the branch
   git checkout master && git pull --ff-only
   git checkout -b phase/<N>[-<letter>]-<short-name>   # e.g. phase/10-docker, phase/12h-settings
   ```
   The slash namespace is the convention (COMMIT-PR-RULES.md "Branch naming").
   It is valid because no branch is literally named `phase` — the old flat-name
   workaround (`phase-10`) was forced only while `port/ara` existed as a branch,
   and was retired with it on 2026-06-02.

3. Do the actual work for the sub-PR's scope. Keep commits small + focused. Push after every commit (per the 2026-05-23 cadence decision).

4. When the scope is complete, go to scenario B next iteration (open the PR).

**Important guardrails while doing sub-PR work:**
- Never skip hooks (`--no-verify` is forbidden by §19.1).
- Never amend a pushed commit (create new commits; the reviewer sees each push).
- Never force-push to a sub-branch with an open PR unless rebasing on updated `master` and announcing it in a PR comment.
- Don't refactor adjacent code outside the sub-PR's scope (track in `design/PORT_TODO.md` instead).
- Don't add comments that just describe what the code does (per CLAUDE.md guidance).

### Step 6 — Phase boundary (reference only)

No scenario routes here; it is kept as a single place to look up what a phase
boundary involves, all of which happens inside §3b:

- There is no promotion PR (playbook §22.0) — the merge to `master` *is* the
  integration.
- The `phase-<N>-complete` tag is pushed onto the PR's head **before** the
  merge, and that PR merges with `--merge` so the tagged commit stays reachable.
- `design/PORT_PROGRESS.md` moves the phase "In flight" → "Completed" and the
  "Currently working on" / "Next" pointers are reset, folded into the next
  sub-PR's commits.

## Pacing (ScheduleWakeup)

At the **end of every iteration** call `ScheduleWakeup` with the same prompt the user invoked (typically the literal sentinel `<<autonomous-loop-dynamic>>` when invoked under `/loop /port-driver` with no interval). Pick `delaySeconds` based on what you're waiting for:

| Situation | delaySeconds | Why |
|---|---|---|
| CI is `pending` and you just pushed | 270 | CI usually finishes in 2-5 min; stay in cache window |
| Review workflow running, no comment yet | 270 | Turnaround is a few minutes; it runs per push |
| Quiescence test (3 min idle for merge-gate) | 270 | Tight loop, cache-friendly |
| Building / doing sub-PR work, expecting next iteration to push | 60 | Work is local + fast |
| Just merged, about to start next sub-PR | 60 | No external wait |
| Idle (everything green, between phases, nothing to do) | 1800 | Don't churn |

**Skip `ScheduleWakeup` entirely** when a stop condition (above) is hit. The user can re-invoke `/loop /port-driver` to resume.

## State you should print at the end of each iteration

One short paragraph for the user / log:

```text
[port-driver iter] <UTC timestamp> | phase=<N> branch=<X> pr=<N|none> | did: <verb-phrase> | next: <verb-phrase> | sleep <seconds>s
```

Example:
```text
[port-driver iter] 2026-05-26T18:42Z | phase=10 branch=phase/10-docker pr=#43 | did: pushed fix for a review Defect on the Dockerfile USER directive | next: poll for the round-2 review | sleep 270s
```

That single line is enough — don't write multi-paragraph summaries each iteration, they pile up.

## Safety net — what you do NOT do

- Do NOT run destructive git ops (`reset --hard`, `branch -D`, `clean -f`, force-push to `master`) without an explicit user instruction.
- Do NOT merge a PR whose CI is failing, whose findings are unresolved, or for which no `claude[bot]` review comment has been posted.
- Do NOT touch `master` directly — only via merged PRs. **Do not rely on the server to stop you:** `master` is governed by a repository **ruleset** (`PORT_DECISIONS.md` "master protection") that requires a PR for everyone *except* its bypass actors — repository admins, retained for hotfixes — and the driver runs under the maintainer's admin credentials. (Don't go looking for `enforce_admins`: that is classic branch protection, which this repo does not use.) A direct push would succeed and land an unreviewed commit outside the §19.1 gate. The driver must never use that bypass.
- Do NOT modify `.husky/` or `.github/workflows/` as part of a feature sub-PR. Those go in their own infra sub-PRs.
- Do NOT update `design/PORT_PLAYBOOK.md` rules autonomously — that's user-authoritative.
- Do NOT spawn cloud agents (no `/ultrareview`, no `/schedule`) from within the loop — they cost extra and the user runs them manually.
