---
name: port-driver
description: Drive the openastro-ara port end-to-end — pick up the next sub-PR per design/COMMIT-PR-RULES.md, build, open the PR, poll CodeRabbit (with /review fallback when rate-limited), merge under the §19.1 gate, advance PORT_PROGRESS.md, repeat. Designed to be invoked under `/loop /port-driver` (autonomous, self-paced) so it survives disconnects.
---

# port-driver

**You are driving the openastro-ara port autonomously.** The user is offline / may disconnect. Your job is to make forward progress every iteration without losing state. The canonical references are:

- `design/PORT_PLAYBOOK.md` (~12k lines — read sections on demand, do not load whole)
- `design/COMMIT-PR-RULES.md` (the rhythm + CR loop + §19.1 merge-gate)
- `design/PORT_PROGRESS.md` (current phase + last merged sub-PR — **always re-read on each loop iteration**, it's the source of truth)
- `design/PORT_TODO.md` (out-of-scope CR findings deferred)
- `design/PORT_DECISIONS.md` (locked-in decisions)

Plus the user's auto-memory under `~/.claude/projects/-Users-dev-Documents-GitHub-openastro-ara/memory/` — re-check `MEMORY.md` each iteration; relevant entries are `feedback-merge-authority` and `project-coderabbit-rate-limit`.

## One-line stop conditions

Stop the loop (omit `ScheduleWakeup`) when any of these are true. Post a final status note to the user and exit:

1. The user has explicitly paused work (a comment on an open PR saying "pause", "stop", "hold", or similar — check the most recent PR comments by `@joeytroy`).
2. You hit a `Held for human review @joeytroy — <reason>` situation in two consecutive iterations on the same PR with no intervening successful work (the counter resets the moment any iteration produces a fix push, a CI pass, a CR reply, or any other non-Held outcome).
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

If `MEMORY.md` is loaded and `[[feedback-merge-authority]]` / `[[project-coderabbit-rate-limit]]` aren't already in mind, read those two files.

### Step 2 — Decide the branch state

Pick exactly one of these scenarios, checking in the order **D → A → B → C → E** (highest priority first). Don't multi-task. Precedence matters because (C) and (D) can both be true when the last merge was the final sub-PR of a phase — (D) wins so the promotion tag + `port/ara → master` PR happen before scenario-C picks up the next phase's sub-PR.

**(A) Open PR exists and you authored it (or it's the active sub-PR on `phase-N…`).**
→ Go to §3 (CR poll/fix loop).

**(B) On a `phase-N…` sub-branch with unpushed commits and no open PR.**
→ Run pre-PR gate, push, open the PR (§4), then schedule a wake-up to start polling.

**(C) On `port/ara` or `master` with no PR in flight, last merge advanced the phase.**
→ Pick the next sub-PR per the COMMIT-PR-RULES.md table + PORT_PROGRESS.md, create the branch, do the work (§5).

**(D) Phase boundary just crossed (last sub-PR of the phase merged).**
→ Tag `phase-N-complete` on `port/ara`, open the `port/ara → master` promotion PR per playbook §22.0 (§6).

**(E) Anything ambiguous (unknown branch, conflicting state, broken working tree).**
→ Stop. Post a status note to the user. Do not schedule another wake-up.

### Step 3 — CR poll/fix loop (scenario A)

This implements COMMIT-PR-RULES.md "CodeRabbit review loop".

1. `gh pr checks <N>` — if any check is `fail`, treat findings as a fix opportunity:
   - Read the failing job's log via `gh run view <run-id> --log-failed`
   - Fix the underlying issue (do not skip hooks; do not retry blindly twice in a row)
   - Commit + push with a message like `fix(ci): <one-line>` and re-poll next iteration

2. Pull CR's comments — `{owner}` and `{repo}` are placeholders you must substitute with the actual GitHub coordinates (`open-astro` and `openastro-ara` for this repo):
   ```shell
   gh api repos/{owner}/{repo}/issues/<N>/comments --jq '.[] | {user: .user.login, created_at, body: (.body | .[0:400])}'
   gh api repos/{owner}/{repo}/pulls/<N>/comments    --jq '.[] | {user: .user.login, path, line, body: (.body | .[0:400])}'
   ```
   Look for these markers in CR's *latest* comment body — **comment body, not check status** (CR's check reports pass even when throttled):
   - `<!-- walkthrough_start -->` or `📝 Walkthrough` → real review, in progress or done
   - `No actionable comments were generated` (or equivalent) → real review, clean
   - `Review limit reached` / `out of usage credits` / `refill in` → **rate-limited**, NOT a real review

3. **If rate-limited and >15 minutes have passed since the PR was opened (or the last `@coderabbitai review` retrigger):**
   → Switch to **/review fallback** (see §3a below). This is the policy update from 2026-05-26: `/review` self-review now satisfies the merge-gate when CR is unavailable.

4. **If real CR comments exist and there are unaddressed actionable findings:**
   For each finding (use the table from COMMIT-PR-RULES.md):

   | Finding | Action |
   |---|---|
   | Trivial / nit / formatting | Fix, commit, push, reply `Fixed in <sha>` |
   | Real bug / correctness | Fix, commit, push, reply `Addressed in <sha>` |
   | Disagreement / contradicts playbook | Reply with reasoning + playbook §ref, no code change |
   | Out-of-scope | Append entry to `design/PORT_TODO.md`, reply `Acknowledged — tracked in design/PORT_TODO.md` |

   If the same issue ping-pongs >2× on the same thread, post `Deferring this to human review — see comments above` and stop touching that thread.

5. **Quiescence check** (merge-gate clearance per §19.1):
   - Green CI on `gh pr checks <N>` (all required checks `pass`)
   - Real CR review posted (walkthrough/summary) **OR** /review fallback completed cleanly (§3a)
   - No unresolved actionable findings
   - ≥3 minutes since the most recent of (last commit, last bot/user comment) — use `created_at` from `gh api`
   - Clean self-review against scope

   If all clear → **merge** (§3b).
   If any gate is ambiguous → post `Held for human review @joeytroy — <reason>` and stop the loop.

### Step 3a — /review fallback (CR rate-limited)

When CR has been rate-limited for >15 min and no real review is forthcoming:

1. Check out the PR locally: `gh pr checkout <N>`
2. Invoke the built-in `/review` skill via the `Skill` tool with the PR number as arg. This runs a structured self-review against the diff.
3. Process the findings the same way as CR findings (§3 step 4 table). Fix → commit → push.
4. After /review is clean (no remaining actionable findings), post a comment on the PR:
   ```text
   CodeRabbit was rate-limited for >15 min. Ran `/review` self-review as the gate per the 2026-05-26 policy update — clean. Proceeding to merge under the §19.1 gate.
   ```
5. Treat the clean /review as satisfying the "real review" condition of §19.1. Continue to merge.

### Step 3b — Merge

- **Sub-PR (base = `port/ara`):** `gh pr merge <N> --squash --delete-branch`
- **Promotion PR (`port/ara → master`):** `gh pr merge <N> --merge` (preserves per-phase history)

After merge:
- `git checkout port/ara && git pull --ff-only`
- If the merged sub-PR was the last in a phase (consult COMMIT-PR-RULES.md sub-split tables and PORT_PROGRESS.md), go to scenario D next iteration.
- Otherwise go to scenario C next iteration.

Update `design/PORT_PROGRESS.md` "Completed" section in the same commit pattern the prior phase entries use. Timing rule:

- **Standalone direct commit to `port/ara`**: use this *only* when you're on `port/ara` immediately after a merge and PORT_PROGRESS.md is the single file changing (pure tracking-metadata update, no code).
- **Folded into the next sub-PR**: use this when you've already moved onto a `phase-N` branch and have other changes queued — include the PORT_PROGRESS.md edit in the sub-PR's commits so it reviews through CR alongside the code work.

When in doubt, prefer folding into the sub-PR — direct pushes to `port/ara` bypass CR review, so keep that path narrow.

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

2. Push the branch: `git push -u origin <branch-name>`

3. Open the PR:
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

4. Schedule next wake-up at 270s — give CR time to start its review pass.

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
- Never amend a pushed commit (create new commits; CR sees incremental review).
- Never force-push to a sub-branch with an open PR unless rebasing on updated `port/ara` and announcing it in a PR comment.
- Don't refactor adjacent code outside the sub-PR's scope (track in `design/PORT_TODO.md` instead).
- Don't add comments that just describe what the code does (per CLAUDE.md guidance).

### Step 6 — Phase boundary promotion (scenario D)

1. From `port/ara`: `git tag phase-<N>-complete && git push origin phase-<N>-complete`
2. Open the promotion PR:
   ```shell
   gh pr create --base master --head port/ara --title "Merge port/ara: Phase <N> (<short-description>)" --body "<summary of what landed>"
   ```
3. This PR also goes through the CR loop (§3) — same gate, but use `--merge` not `--squash` when merging it (per COMMIT-PR-RULES.md).
4. Update `design/PORT_PROGRESS.md` to move the phase from "In flight" → "Completed", reflect the new "Currently working on" / "Next" pointer.

## Pacing (ScheduleWakeup)

At the **end of every iteration** call `ScheduleWakeup` with the same prompt the user invoked (typically the literal sentinel `<<autonomous-loop-dynamic>>` when invoked under `/loop /port-driver` with no interval). Pick `delaySeconds` based on what you're waiting for:

| Situation | delaySeconds | Why |
|---|---|---|
| CI is `pending` and you just pushed | 270 | CI usually finishes in 2-5 min; stay in cache window |
| CR review in progress (walkthrough started but not done) | 270 | Same; CR turnaround is 2-10 min |
| CR rate-limited, waiting for refill (<60 min) | 1200 | Refill usually 30-60 min; one cache miss buys the wait |
| CR rate-limited, refill long (>60 min) | 1800 | Same; bigger ceiling |
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
[port-driver iter] 2026-05-26T18:42Z | phase=10 branch=phase-10 pr=#43 | did: pushed fix for CR nit on Dockerfile USER directive | next: poll CR for round-2 review | sleep 270s
```

That single line is enough — don't write multi-paragraph summaries each iteration, they pile up.

## Safety net — what you do NOT do

- Do NOT run destructive git ops (`reset --hard`, `branch -D`, `clean -f`, force-push to `master`/`port/ara`) without an explicit user instruction.
- Do NOT merge a PR whose CI is failing, whose findings are unresolved, or whose only "review" signal is the CR rate-limit comment (use /review fallback first).
- Do NOT touch `master` directly — only via promotion PRs.
- Do NOT modify `.husky/`, `.github/workflows/`, or `.coderabbit.yaml` as part of a feature sub-PR. Those go in their own infra sub-PRs.
- Do NOT update `design/PORT_PLAYBOOK.md` rules autonomously — that's user-authoritative.
- Do NOT spawn cloud agents (no `/ultrareview`, no `/schedule`) from within the loop — they cost extra and the user runs them manually.
