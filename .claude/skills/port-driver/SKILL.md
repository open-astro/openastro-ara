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

- `git status --short && git branch --show-current`
- `git log -1 --oneline`
- `gh pr list --state open --json number,title,headRefName,baseRefName,author,updatedAt`
- Read `design/PORT_PROGRESS.md` (whole file — it's ≤200 lines)

If a `MEMORY.md` index is loaded, read any entry it lists that bears on merge authority or the review gate.

### Step 2 — Decide the branch state

Pick exactly one of these scenarios, checking in the order **D → A → B → C → E** (highest priority first). Don't multi-task. Precedence matters because (C) and (D) can both be true when the last merge was the final sub-PR of a phase — (D) wins so the promotion tag + `port/ara → master` PR happen before scenario-C picks up the next phase's sub-PR.

**(A) Open PR exists and you authored it (or it's the active sub-PR on `phase-N…`).**
→ Go to §3 (review poll/fix loop).

**(B) On a `phase-N…` sub-branch with unpushed commits and no open PR.**
→ Run pre-PR gate, push, open the PR (§4), then schedule a wake-up to start polling.

**(C) On `port/ara` or `master` with no PR in flight, last merge advanced the phase.**
→ Pick the next sub-PR per the COMMIT-PR-RULES.md table + PORT_PROGRESS.md, create the branch, do the work (§5).

**(D) Phase boundary just crossed (last sub-PR of the phase merged).**
→ Tag `phase-N-complete` on `port/ara`, open the `port/ara → master` promotion PR per playbook §22.0 (§6).

**(E) Anything ambiguous (unknown branch, conflicting state, broken working tree).**
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
   - Green CI on `gh pr checks <N>`: every required check is `pass`, **or
     `skipping` and you can attribute the skip to CI's path gate** — `ci.yml`'s
     `changes` job emitting `docs_only=true` (#1020).

     That gate skips **six jobs**, which is eight check contexts:

     ```
     Alpaca simulator harness (smoke)
     Alpaca discovery integration test
     Analyzer gate (full solution, warnings = errors)
     Server (build + cross-publish + Docker)            <- required
     Settings + Help registry gate                      <- required
     Client (native build) — {macos,linux,windows}
     ```

     The three `Client (analyze + test) — *-latest` legs are **required and
     matrix-expanded**, so they are gated at *step* level and report `pass`
     with their steps skipped, never `skipping` (#1025: a matrix job skipped at
     job level never expands, so its required contexts are never reported and
     the PR blocks forever). Expect `pass` there, not `skipping`. Confirm that is why, don't assume it: `gh pr checks`
     shows the skip but not its cause, and GitHub counts *any* skipped required
     context as satisfied. Check the `changes` job's `docs_only` output. A skip
     outside that list, or one you cannot account for — a `needs:` failure, an
     `if:` you don't recognise — is **ambiguous, not clearance**. `pending` or
     `fail` is never clearance. (Playbook §19.1, amended for this in #1021.)
   - A `claude[bot]` review comment **for the current head** (`updated_at` ≥ your last push) **carrying a sign-off marker**, with **no unaddressed Defects**
   - ≥3 minutes since the most recent of (last commit, last bot/user comment) — use `updated_at` from `gh api`, for the same sticky-comment reason
   - Clean self-review against scope

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

- **Sub-PR (base = `port/ara`):** `gh pr merge <N> --squash --delete-branch`
- **Promotion PR (`port/ara → master`):** `gh pr merge <N> --merge` (preserves per-phase history)

After merge:
- `git checkout port/ara && git pull --ff-only`
- If the merged sub-PR was the last in a phase (consult COMMIT-PR-RULES.md sub-split tables and PORT_PROGRESS.md), go to scenario D next iteration.
- Otherwise go to scenario C next iteration.

Update `design/PORT_PROGRESS.md` "Completed" section in the same commit pattern the prior phase entries use. Timing rule:

- **Standalone direct commit to `port/ara`**: use this *only* when you're on `port/ara` immediately after a merge and PORT_PROGRESS.md is the single file changing (pure tracking-metadata update, no code).
- **Folded into the next sub-PR**: use this when you've already moved onto a `phase-N` branch and have other changes queued — include the PORT_PROGRESS.md edit in the sub-PR's commits so it reviews alongside the code work.

When in doubt, prefer folding into the sub-PR — direct pushes bypass review, so keep that path narrow.

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
   gh pr create --base port/ara --head <branch-name> --title "<conventional-prefix>: <one-line>" --body "$(cat <<'EOF'
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

2. From `port/ara`:
   ```shell
   git checkout port/ara && git pull --ff-only
   git checkout -b <branch-name>     # flat name, e.g. phase-10, phase-12a
   # NEVER use hierarchical names like port/ara/phase-10 — Git refs are tree-structured,
   # so `port/ara/anything` is invalid while `port/ara` itself exists as a branch
   # (per COMMIT-PR-RULES.md "Git ref naming constraint").
   ```

3. Do the actual work for the sub-PR's scope. Keep commits small + focused. Push after every commit (per the 2026-05-23 cadence decision).

4. When the scope is complete, go to scenario B next iteration (open the PR).

**Important guardrails while doing sub-PR work:**
- Never skip hooks (`--no-verify` is forbidden by §19.1).
- Never amend a pushed commit (create new commits; the reviewer sees each push).
- Never force-push to a sub-branch with an open PR unless rebasing on updated `port/ara` and announcing it in a PR comment.
- Don't refactor adjacent code outside the sub-PR's scope (track in `design/PORT_TODO.md` instead).
- Don't add comments that just describe what the code does (per CLAUDE.md guidance).

### Step 6 — Phase boundary promotion (scenario D)

1. From `port/ara`: `git tag phase-<N>-complete && git push origin phase-<N>-complete`
2. Open the promotion PR:
   ```shell
   gh pr create --base master --head port/ara --title "Merge port/ara: Phase <N> (<short-description>)" --body "<summary of what landed>"
   ```
3. This PR also goes through the review loop (§3) — same gate, but use `--merge` not `--squash` when merging it (per COMMIT-PR-RULES.md).
4. Update `design/PORT_PROGRESS.md` to move the phase from "In flight" → "Completed", reflect the new "Currently working on" / "Next" pointer.

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
[port-driver iter] 2026-05-26T18:42Z | phase=10 branch=phase-10 pr=#43 | did: pushed fix for a review Defect on the Dockerfile USER directive | next: poll for the round-2 review | sleep 270s
```

That single line is enough — don't write multi-paragraph summaries each iteration, they pile up.

## Safety net — what you do NOT do

- Do NOT run destructive git ops (`reset --hard`, `branch -D`, `clean -f`, force-push to `master`/`port/ara`) without an explicit user instruction.
- Do NOT merge a PR whose CI is failing, whose findings are unresolved, or for which no `claude[bot]` review comment has been posted.
- Do NOT touch `master` directly — only via promotion PRs.
- Do NOT modify `.husky/` or `.github/workflows/` as part of a feature sub-PR. Those go in their own infra sub-PRs.
- Do NOT update `design/PORT_PLAYBOOK.md` rules autonomously — that's user-authoritative.
- Do NOT spawn cloud agents (no `/ultrareview`, no `/schedule`) from within the loop — they cost extra and the user runs them manually.
