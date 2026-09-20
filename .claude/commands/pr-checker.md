---
description: Drive one or more open PRs through the review-bot loop until each is clean, then merge them in order; keeps looping until every PR is done
allowed-tools: Read, Edit, Write, Bash, Grep, Glob
---

You are the PR checker for the **openastro-ara** project (`open-astro/openastro-ara`, base branch
`master`). The user gives you one or more PR numbers (`/pr-checker 1015 1016 1017`,
`/pr-checker 1015-1017`, or `/pr-checker` alone to mean every open PR). For each PR, in ascending
number order, loop **fix -> push -> poll the review bot** until the bot posts a clean verdict, then
merge it, then move to the next PR. Do not stop early, do not hand a half-finished PR back, and do
not ask "shall I continue?" between rounds. The only stopping points are: every listed PR is merged
(or closed), or a PR is blocked on something only the user can decide (see **Hard stops**).

Companion docs, authoritative where they disagree with this file: `design/COMMIT-PR-RULES.md`
(the rhythm, the review loop, the §19.1 merge gate) and `.claude/skills/port-driver/SKILL.md`
(the same gate from the autonomous side).

## Step 0 — Resolve the PR list

```bash
gh pr list --state open --json number,title,author,headRefName,headRepositoryOwner,isDraft,labels \
  --jq '.[] | "\(.number) \(.title) | \(.author.login) \(.headRepositoryOwner.login):\(.headRefName) draft=\(.isDraft) labels=\([.labels[].name]|join(","))"'
```

Expand ranges (`1015-1017` -> 1015 1016 1017). Skip numbers that are not open PRs and say so.
Record for each PR: author, head owner/branch, whether it is a **fork PR** (head owner !=
`open-astro`), whether it is a **Dependabot** PR (the review workflow skips `dependabot[bot]`
entirely on both paths — see **Dependabot PRs** below), whether it is a draft, and whether it
carries the `safe-to-review` label.

**Validate every contributor-controlled string before it touches a shell command.** Branch names
and fork owners come from the PR author and can contain anything git allows. Refuse (hard stop for
that PR) any `headRefName` that does not match `^[A-Za-z0-9][A-Za-z0-9._/-]*$` **and** contains
`..` (check both: the regex alone lets `foo..bar` through), and any `headRepositoryOwner.login`
that does not match `^[A-Za-z0-9](?:-?[A-Za-z0-9])*$` (GitHub login rules: alphanumerics with
single inner hyphens only, so no leading, trailing or consecutive hyphen, and an owner can never
become a bare `-x` argument). No leading `-`, no whitespace, no quotes, no path traversal, and
always double-quote them when interpolated (`"$BRANCH"`, `"$OWNER"`), never bare `<branch>`.
PR numbers must match `^[0-9]+$`. Never `eval` or build a command from a PR title or body.

Print the queue once, then work it top to bottom.

## Step 1 — Per PR: make sure the bot is actually going to run

The review bot (`.github/workflows/claude-review.yml`) posts a comment ending in `✅ Approved` or
`⚠️ Issues found`. Two paths, two check-run names:

- **`review`** — same-repo branches, `pull_request`, runs on every push, no label needed.
- **`review-fork`** — fork heads, `pull_request_target`, runs **only** while the `safe-to-review`
  label is on the PR (`labeled` starts the first pass, `synchronize` keeps it going).

Poll for both names. The comment author is `claude[bot]` on the same-repo path; the REST API
also reports `github-actions[bot]` for the workflow-posted variant while `gh pr view --json
comments` reports it as `github-actions`, so match all three. The verdict may arrive as a **new
comment or as an in-place edit of an existing one**, so bind on `updated_at`, never on comment id.

Checks to make before waiting on anything:

1. **Fork PR with no `safe-to-review` label** -> add it via REST (`gh pr edit --add-label` can
   choke on a GraphQL projects warning):
   ```bash
   gh api -X POST repos/open-astro/openastro-ara/issues/<N>/labels -f 'labels[]=safe-to-review'
   ```
   The `labeled` event starts a fresh review immediately.
2. **`review-fork` failed in ~15 s on permissions, or the label is on but no run exists** ->
   remove and re-add the label via REST to re-trigger it as the maintainer:
   ```bash
   gh api -X DELETE repos/open-astro/openastro-ara/issues/<N>/labels/safe-to-review
   gh api -X POST   repos/open-astro/openastro-ara/issues/<N>/labels -f 'labels[]=safe-to-review'
   ```
3. **Branch is behind master** (`gh api "repos/open-astro/openastro-ara/compare/master...$OWNER:$BRANCH" --jq .behind_by`
   is non-zero; `$OWNER`/`$BRANCH` validated in Step 0). Branch protection is strict, so it must be
   updated before it can merge, and updating re-runs CI + the bot. Do this **now** rather than
   after the verdict so you do not pay for two bot rounds:
   ```bash
   gh api -X PUT repos/open-astro/openastro-ara/pulls/<N>/update-branch
   ```
   **Check the result before polling for a verdict**: `update-branch` returns HTTP 422 on a merge
   conflict, and a chain that ignores that then waits 30 minutes for a verdict that never comes.
   GitHub recomputes mergeability asynchronously, so the first read after the call is usually
   `UNKNOWN`; poll until it settles and treat a timeout as a conflict, never as "fine":
   ```bash
   for i in $(seq 1 12); do   # up to 2 min
     m=$(gh pr view <N> --json mergeable --jq .mergeable)   # MERGEABLE | CONFLICTING | UNKNOWN
     [ "$m" != "UNKNOWN" ] && break; sleep 10
   done
   echo "$m"
   ```
   `CONFLICTING` (or still `UNKNOWN` after the loop) -> resolve locally on the head branch now
   (Step 3 mechanics: fetch the head, `git merge origin/master`, keep both sides when two PRs
   added adjacent CI jobs or gates, validate syntax, push), then poll. `MERGEABLE` -> Step 2.
   It is fine to update while a review is still in flight: the run on the old head is cancelled
   and a fresh one starts on the merged head, so nothing is lost.
4. **Verdict already present for the current head SHA** -> skip the wait and go straight to Step 3.
   "Belongs to the current head" is the same rule as the Step 2 poll, so run **one pass** of that
   block with `BUDGET=0` (the loop checks before it sleeps, so `BUDGET=0` is exactly one check):
   a `review`/`review-fork` check-run on the head SHA completed, none still queued or running, and
   the newest bot verdict updated after that run started. Commit dates are never used: a committer
   date is local commit time, so a verdict on the previous head can be newer than it. Exit codes:
   `0` prints the verdict (Step 3); `2` or `3` are the Step 3 "no verdict" cases; `1` from a
   `BUDGET=0` pass only means **no verdict yet** (the normal state right after a push): go to
   Step 2. It is not a timeout and does not count toward the "two consecutive timeouts" hard stop.
   **Skip this check if Step 1.1, 1.2 or 1.3 just acted**: a relabel or update-branch re-triggers
   the bot on the same head, and for a few seconds the only check-run is the old one, which the
   pre-check would report as skipped, failed or cancelled. Go straight to Step 2, whose first look
   comes after one tick.

### Dependabot PRs

`claude-review.yml` skips `github.actor == 'dependabot[bot]'` on **both** paths, so a Dependabot
bump will never get a verdict and the poll would burn its whole budget. Do not poll one. Review it
by eye instead: read `gh pr diff <N>`, confirm it is a lockstep version bump with no source edits,
wait for CI (`gh pr checks <N> --watch`), and merge under Step 4's gate. Anything beyond a version
bump in the diff is a **Hard stop**.

## Step 2 — Poll for the verdict (3-minute cadence, background)

Never foreground-sleep. Run this with `run_in_background` and a 30-minute deadline:

```bash
PR=<N>; REPO=open-astro/openastro-ara; TICK=${TICK:-180}; DEADLINE=$(( $(date +%s) + ${BUDGET:-1800} ))
# ISO-8601 -> epoch, portable: `date -u -d` is GNU and this repo is driven from macOS.
iso2epoch() { python3 -c 'import sys,datetime;print(int(datetime.datetime.strptime(sys.argv[1],"%Y-%m-%dT%H:%M:%SZ").replace(tzinfo=datetime.timezone.utc).timestamp()))' "$1"; }
# A re-trigger on the same head (relabel, update-branch) takes a few seconds to show a
# new check-run; a first look inside that window classifies the OLD run as final. So a
# real poll sleeps one tick before it looks. BUDGET=0 is the one-pass pre-check, which
# is only valid when nothing was just re-triggered, and looks at once.
if [ "${BUDGET:-1800}" -gt 0 ]; then sleep "$TICK"; fi
while :; do
  # Bind the verdict to the review check-run on the head SHA (filter=all so a later
  # cancelled attempt cannot hide the finished one). Everything is fetched with
  # --paginate: unpaginated, both endpoints return only the 30 oldest items.
  SHA=$(gh api "repos/$REPO/pulls/$PR" --jq .head.sha)
  if [ "$SHA" != "${LAST_SHA:-}" ]; then CANCELLED_SEEN=0; LAST_SHA=$SHA; fi   # new head, new latch
  RUNS=$(gh api --paginate "repos/$REPO/commits/$SHA/check-runs?per_page=100&filter=all" | jq -s 'map(.check_runs[]) | map(select(.name == "review" or .name == "review-fork"))')
  # Newest finished run that was not cancelled or skipped. A failed run still counts:
  # the assert step can fail after the post step published the verdict, and nothing
  # re-runs a failed run on its own, so it must be reported, not waited out.
  DONE=$(jq -r '[.[] | select(.status == "completed" and (.conclusion | IN("cancelled","skipped") | not))] | max_by(.started_at) | select(. != null) | "\(.started_at) \(.conclusion) \(.html_url)"' <<<"$RUNS")
  RUN_STARTED=${DONE%% *}; RUN_STATE=${DONE#* }
  PENDING=$(jq -r '[.[] | select(.status == "queued" or .status == "in_progress")] | length' <<<"$RUNS")
  SKIPPED=$(jq -r '[.[] | select(.conclusion == "skipped")] | length' <<<"$RUNS")
  CANCELLED=$(jq -r '[.[] | select(.conclusion == "cancelled")] | length' <<<"$RUNS")
  # REST, not `gh pr view --json comments`: only REST exposes updated_at, and the
  # verdict is often an in-place EDIT of an older comment. `select(. != null)`
  # matters: with no verdict yet, `last` is null and would print the literal "null".
  LAST=$(gh api --paginate "repos/$REPO/issues/$PR/comments?per_page=100" | jq -r -s 'add // [] | [.[] | select((.user.login | test("^(claude|github-actions)(\\[bot\\])?$")) and (.body | test("✅ Approved|⚠️ Issues found")))] | last | select(. != null) | "\(.updated_at) \(.body)"')
  if [ -n "$RUN_STARTED" ] && [ "$PENDING" = 0 ] && [ -n "$LAST" ] \
     && [ "$(iso2epoch "${LAST%% *}")" -ge "$(iso2epoch "$RUN_STARTED")" ]; then
    printf '%s\n' "${LAST#* }"; exit 0
  fi
  if [ -n "$RUN_STARTED" ] && [ "$PENDING" = 0 ]; then
    # Finished, nothing pending, no verdict for this head. Never wait 30 minutes for it.
    case "$RUN_STATE" in
      success*)
        # The PR edits the review workflow and the action skipped it (the assert step
        # passes it). Hand back for eyeball review; this is a hard stop, never a merge.
        if gh pr diff "$PR" --name-only | grep -qxF ".github/workflows/claude-review.yml"; then
          echo "NO VERDICT for head $SHA: this PR edits the review workflow and the action skipped it. Hard stop: review it by eye." >&2; exit 2
        fi
        # Green run, ordinary PR, no verdict: the agent wrote its verdict to chat instead
        # of the review comment. Nothing re-runs it; report, do not wait.
        echo "REVIEW RUN succeeded for head $SHA but published no verdict: ${RUN_STATE#* }" >&2; exit 3 ;;
      *)
        echo "REVIEW RUN ENDED ${RUN_STATE%% *} for head $SHA with no verdict: ${RUN_STATE#* }" >&2; exit 3 ;;
    esac
  fi
  # Only skipped runs and nothing pending: the actor gate skipped the job (fork push
  # without the label, or a Dependabot PR — see Step 1). A cancelled run beside the
  # skipped one means a labelled run existed and was re-triggered mid-poll; that is
  # the two-look cancelled case below, not a skip.
  if [ -z "$RUN_STARTED" ] && [ "$PENDING" = 0 ] && [ "$SKIPPED" != 0 ] && [ "$CANCELLED" = 0 ]; then
    echo "REVIEW SKIPPED for head $SHA: no run to wait for. Apply or re-apply safe-to-review (Step 1), or it is a Dependabot PR." >&2; exit 3
  fi
  # Only cancelled runs: a superseding trigger never came. A relabel issued mid-poll
  # cancels the in-flight run a few seconds before its replacement appears, so this
  # state has to hold on two consecutive looks before it is reported.
  if [ -z "$RUN_STARTED" ] && [ "$PENDING" = 0 ] && [ "$CANCELLED" != 0 ]; then
    if [ "${CANCELLED_SEEN:-0}" = 1 ]; then
      echo "REVIEW CANCELLED for head $SHA and nothing replaced it: re-trigger with the relabel trick." >&2; exit 3
    fi
    CANCELLED_SEEN=1
  else
    CANCELLED_SEEN=0
  fi
  [ "$(date +%s)" -ge "$DEADLINE" ] && break
  sleep "$TICK"
done
echo "NO VERDICT for head ${SHA:-?} after $(( ${BUDGET:-1800} / 60 )) min (review runs pending: ${PENDING:-?}, newest finished: ${RUN_STATE:-none})" >&2; exit 1
```

Exit codes: `0` = verdict on stdout. `1` = no verdict within the budget (with `BUDGET=0`, just
"not yet"). `2` = the action skipped a workflow-editing PR (hard stop). `3` = no run will produce
a verdict for this head (the newest run failed, succeeded without publishing one, or the job was
skipped or every run was cancelled); the message says which and carries the run URL where there is
one. Every non-zero exit prints nothing on stdout, so a chained `poll && merge` never reaches the
merge.

When several PRs are queued, poll them all in one background loop and act on whichever verdict
lands first, but **merge strictly in ascending number order** so the update-branch dance is
predictable.

On exit `3`, or a timeout: open the run URL (or `gh run list --workflow=claude-review.yml
--limit 5`) and read the failing job. Known stalls: the workflow triggers only on `opened`,
`synchronize` and `labeled` (closing and reopening the PR does NOT re-run it), so a stuck or
cancelled run is restarted with the remove-and-re-add `safe-to-review` label trick from Step 1.2
on a fork, or an empty-commit push on a same-repo branch. Do not restart the poll blindly.

While waiting, watch CI too (`gh pr checks <N>`). A red CI check gets fixed and pushed in the same
batch as the bot findings, not on its own.

## Step 3 — Act on the verdict

Read the newest bot comment in full. A poll that exited without a verdict has no comment to act on
and never merges: exit `2` (workflow-editing PR skipped by the action) is a **Hard stop**; exit `3`
(review run failed) means read the run log, apply the relabel trick from Step 1.2 if it is the
permission skip, and otherwise treat it as a broken workflow (**Hard stop** after two).

### `⚠️ Issues found`

The bot's comment has two sections (`claude-review.yml` prompt): **Defects**, which are what made
the verdict a rejection, and **Notes**, which never block. Fix **every Defect** on this PR, in this
PR, in **one batched push** (one commit per Defect, see "Prove it before you push" below); do not
defer a Defect to a follow-up issue and do not decline one as low priority unless the user says so.
Handle the **Notes** by the classification rules in the `✅ Approved` section below: a mechanical
note goes into the same round's push, a judgment note or one the bot marks "out of scope, open an
issue" goes to the wrap-up and is never pushed (out-of-scope items get an entry in
`design/PORT_TODO.md` per COMMIT-PR-RULES.md, which is a wrap-up line, not a second push). A note
is never a reason for a second push. Each push restarts a full fresh review.

**Before writing a line**, fetch the head branch: contributors watch the same bot and often push
their own fix for the same finding within minutes. If their head already moved past the reviewed
SHA, read their diff first; if it addresses the finding, adopt it (reset your local branch to their
head) and just poll again.

### Prove it before you push (test-first, per finding)

Run `git fetch --prune origin` before anything below: every `origin/master` in this section is only
as fresh as the last fetch, and a loop that merges PRs back to back makes it stale within the run.

The bot is the second pair of eyes, not the test suite. For **every** Defect, in this order:

1. **Reproduce the claim.** Re-read the bot's exact statement and make it observable before
   changing anything: run the failing input through the current code, run the script against the
   real file, or write the unit test and watch it fail. If it cannot be reproduced, that is the
   finding to answer (hard stop or a wrong-claim note in the commit message), not a reason to
   change code on faith.
2. **Fix, then re-run the same probe.** Red, then green, with the same input. A fix that was never
   red is not proven.
3. **Ship the probe with the fix** whenever it can live in the repo: an xUnit case under
   `OpenAstroAra.Test`, a Flutter test under `client/openastroara_client/test`, a case in
   `scripts/tests/` for the Python tooling, or a synthetic-repo check described in the commit
   message when the probe cannot be committed.
4. **Fix the structure before adding guards.** When a finding exposes brittle structure, fix the
   cause first, inside the PR's own files. Keep a guard only when it catches something distinct
   from the structural fix, and say so in its comment. Never widen to files the PR does not touch.
5. **Run the exact CI gate for what changed**, not the whole matrix and not nothing. Mirror
   `.github/workflows/ci.yml`; run `scripts/pre-pr-check.sh` instead if it exists:
   - **Server / domain C#**: `dotnet build OpenAstroAra.Server/OpenAstroAra.Server.csproj -c Release`,
     plus `dotnet build OpenAstroAra.<TouchedProject>/OpenAstroAra.<TouchedProject>.csproj -c Release`
     for each touched domain project (Core / Astrometry / Equipment / Image / Profile /
     PlateSolving / Test). Not the whole solution: `OpenAstroAra.Sequencer` is still blocked on
     `NINA.WPF.Base` (tracked in `design/PORT_TODO.md`).
   - **Tests**: `dotnet test OpenAstroAra.Test/OpenAstroAra.Test.csproj` when C# under test changed;
     the `Analyzer gate (full solution, warnings = errors)` job is the CI counterpart, so a new
     warning is a failure, not a nit.
   - **Flutter client** (`client/openastroara_client`): `flutter analyze && flutter test`. This is
     the `Client (analyze + test)` matrix. When the change touches native/plugin wiring, also
     `flutter build macos --release` (the `Client (native build)` gate exists because analyze-only
     let a non-linking macOS client merge, #599).
   - **Settings / help registries**: `node scripts/check-settings-registry.mjs` and
     `node scripts/check-help-registry.mjs` (add `--staged` pre-commit). CI runs these as
     `Settings + Help registry gate`.
   - **Any branch, always cheap**: `python3 .github/scripts/check-unicode.py` (the `Unicode scan`
     job) and `python3 -m unittest discover -s scripts/tests -v` (runs inside `Sanity`).
   - **Workflows**: `zizmor --offline .github/workflows/` (the `zizmor (workflow audit)` job).
   - **Docs-only branches** still run Sanity + Unicode scan + the Python tooling tests; everything
     else is skipped by the `changes` path gate (#1020). Run those three and push.
6. **One commit per Defect, one push per round** (this `⚠️ Issues found` path only). Commits stay
   atomic so a wrong one can be reverted alone; the push stays batched because every push costs a
   full review. A cleanup round after an approval is different: its notes are small and related, so
   they go in ONE commit as the `✅ Approved` section says.

Mechanics for a **fork PR**:

```bash
# $REMOTE is a local remote name you chose, $BRANCH the validated head name.
git fetch "$REMOTE" "$BRANCH"
git checkout -B "$BRANCH" "$REMOTE/$BRANCH"
# ... apply fixes ...
# Gates then push then poll as ONE background chain (see "Keep looping").
# run_gates is the step 5 gate set for the files this round touched, written out
# as a function so a multi-command set chains like a single one.
run_gates() { <the step 5 commands for what changed>; }
run_gates > "$LOG" 2>&1 \
  && git fetch "$REMOTE" "$BRANCH" \
  && [ -z "$(git log --oneline "HEAD..$REMOTE/$BRANCH")" ] \
  && git push "$REMOTE" "HEAD:$BRANCH" \
  && <Step 2 poll loop>
```

A red gate is a hard block ONLY for failures in code this branch touches; see "Keep looping" for
the flake rule. A non-empty `git log HEAD..$REMOTE/$BRANCH` means the contributor pushed meanwhile:
read their diff and rebase or adopt before pushing.

If the fork remote does not exist, add it with the validated owner:
`git remote add "$REMOTE" "https://github.com/$OWNER/openastro-ara.git"`.
If the contributor already pushed an equivalent fix while you were working, **adopt theirs** and
drop your duplicate instead of force-pushing. Never force-push a contributor's branch. Adopting is
not a rubber stamp: read their **entire** diff against the previously reviewed head (not just the
hunk that addresses the finding) and confirm it contains nothing beyond that fix before resetting
onto it; anything unrelated goes back to the bot as a normal push and review round.

For an `open-astro` branch, the same flow against `origin`.

Commit message: verb-first conventional-prefix title under 70 chars, body explaining what the bot
found and how it was fixed, then the attribution trailer from the session. If the round introduces
a user-visible change, add the `CHANGELOG.md` `[Unreleased]` bullet in the same diff. After
pushing, go back to Step 1 and Step 2.

**Never post PR comments replying to the bot** on an ordinary finding. A finding is either a change
to the code, skill or docs (push it), an out-of-scope item tracked in `design/PORT_TODO.md`, or, if
it is clearly wrong and nothing can be changed to satisfy it, a hard stop for the user to rule on.
Explanations belong in the commit message, not in the PR thread. If the same issue ping-pongs more
than twice on one thread, post `Deferring this to human review — see comments above` and stop
touching that thread (COMMIT-PR-RULES.md). Keep a running tally of rounds per PR and report it in
the wrap-up.

### `✅ Approved`

**At most one cleanup round, then merge.** Approvals usually carry Notes. Leaving them is how
leftovers accumulate; chasing them one at a time is how a PR reaches 29 commits. Handle them like
this:

1. Classify each note. **Mechanical** = a change a reviewer would accept without discussion and
   that stays inside the PR's files and purpose: unused import, missing test for a string the PR
   added, regex edge case, a comment fix. **Judgment** = changes a default or behaviour, widens
   scope, needs hardware, or contradicts the PR author's stated intent. Judgment notes are listed
   in the wrap-up for the user, never pushed.
2. Fix **all** mechanical notes in ONE commit, push once, poll again.
3. Merge on the next verdict that has no Defects, whatever Notes it carries, and list those Notes
   in the wrap-up. **Hard cap: 1 cleanup round per PR.**
4. `⚠️ Issues found` on a cleanup round is handled like any other round: fix, push, poll. Counting
   against the cap is mechanical: a round whose Defects section is non-empty does **not** count (it
   is a fix round, not a cleanup round).
5. If the approval has **no** mechanical notes, skip straight to the merge below.

## Step 4 — The merge gate (§19.1)

```bash
gh pr view <N> --json isDraft,mergeable,mergeStateStatus --jq '"draft=\(.isDraft) mergeable=\(.mergeable) state=\(.mergeStateStatus)"'
gh pr checks <N>
```

All four must hold, per `design/COMMIT-PR-RULES.md` §19.1:

1. **Green CI.** Every required check is `pass`, **or** `skipping` that you can attribute to
   `ci.yml`'s `changes` job emitting `docs_only=true` (#1020). That gate skips **six** contexts:
   ```
   Alpaca simulator harness (smoke)
   Alpaca discovery integration test
   Analyzer gate (full solution, warnings = errors)
   Server (build + cross-publish + Docker)              <- required
   Settings + Help registry gate                        <- required
   Client (native build) — ${{ matrix.target }}         <- literally this
   ```
   The last one is not a typo: `client-build` is a matrix skipped at job level, so it never expands
   and reports **one** context under its raw, uninterpolated name. The three
   `Client (analyze + test) — *-latest` legs are required **and** matrix-expanded, so they are
   gated at *step* level and report `pass` with their steps skipped, never `skipping` (#1025).

   **`docs_only` is no longer the only attributable cause.** The `changes` job also emits `dotnet`
   and `client`, which gate the four NON-required contexts in that list:

   ```
   Alpaca simulator harness (smoke)                     <- dotnet
   Alpaca discovery integration test                    <- dotnet
   Analyzer gate (full solution, warnings = errors)     <- dotnet
   Client (native build) — ${{ matrix.target }}         <- client
   ```

   So a client-only PR legitimately shows those three `dotnet` jobs as `skipping` while nothing is
   docs-only, and a PR touching neither graph skips all four. The two required contexts it can
   skip (`Server (build + cross-publish + Docker)`, `Settings + Help registry gate`) stay on
   `docs_only` alone, on purpose: a skipped required context counts as satisfied, so the finer
   buckets are kept out of that blast radius.

   Attribute a skip by running the classifier on the PR's own diff rather than guessing:
   ```bash
   git diff --no-renames --name-only "$(git merge-base origin/master HEAD)" HEAD \
     | python3 scripts/classify-changed-paths.py
   ```
   `dotnet=false` explains the three dotnet skips, `client=false` explains `client-build`,
   `docs_only=true` explains all six. Confirm the cause, don't assume it — `gh pr checks` shows the
   skip but not why. A skip outside that list, or one the classifier does not account for (a
   `needs:` failure, an `if:` you don't recognise), is **ambiguous, not clearance**.
   `pending` or `fail` is never clearance.
2. **A verdict for the current head** with no unaddressed Defects (Step 2 exit `0`).
3. **≥3 minutes of quiescence** since the most recent of (last commit, last bot/user comment),
   measured with `updated_at` from `gh api`, not comment ids.
4. **Clean self-review against scope** — the diff does what the PR says and nothing else.

Then:

- `draft=true` -> `gh pr ready <N>` first (`gh pr merge` refuses drafts).
- `state=BEHIND` -> `update-branch` (Step 1.3) and go back to Step 2; the merge commit re-runs the bot.
- `state=BLOCKED` with checks still running -> `gh pr checks <N> --watch`, then merge.
- otherwise, **settle the merge method before running any merge fence** — the tag check and the
  boundary decision come first, so a top-down reader cannot merge ahead of them:

  **A PR carrying a phase or sub-phase tag always takes `--merge`**, never
  `--squash`: squashing rewrites the head the tag points at, so the tag is left
  on a commit that is not reachable from `master` (COMMIT-PR-RULES.md steps 6-7).
  Check for a tag on the head:
  ```bash
  HEAD_OID=$(gh pr view <N> --json headRefOid --jq .headRefOid)
  # Bail rather than guess: an empty HEAD_OID makes the grep below match every
  # line, so a failed API call would read as "tagged" for every PR.
  [ -n "$HEAD_OID" ] || { echo "cannot read head OID -- hard stop"; exit 1; }
  # Anchor to the phase namespace: §19.1 mandates `backup-<timestamp>` tags
  # before a `reset --hard`, and one of those on the head is not a phase tag.
  git ls-remote --tags origin \
    | grep -E "^$HEAD_OID[[:space:]]+refs/tags/phase-" \
    && echo "tagged -> use --merge"
  ```
  **Phase-boundary PR with no tag on its head -> do not merge yet (#1029).** The probe above only
  detects a tag that is already pushed; nothing else in this command pushes one, and under the
  port-driver's §3b ordering the tag goes on immediately before the merge. So before choosing the
  method, decide whether this PR closes a phase or sub-phase: read `design/PORT_PROGRESS.md` and the
  COMMIT-PR-RULES.md sub-split tables, exactly as §3b does. If it does and the probe found no
  `phase-*` tag, run the §3b tag block from `.claude/skills/port-driver/SKILL.md` verbatim —
  `git fetch origin pull/<N>/head`, tag the `headRefOid`, both pre-push verifications, then
  `git push origin <tag>` (the named ref, never `--tags`) — and only then merge with `--merge`.
  Merging it untagged silently skips §19.1's phase-boundary gate item. If you cannot tell whether
  it is a boundary, that is ambiguous: **Hard stop** with `Held for human review`.

  Then merge. Multi-commit PR landing as one logical change:
  ```bash
  # Same invocation as the merge: shell state does not survive between blocks.
  # Fails safe -- an empty/errored probe is != "false", so the flag is omitted.
  [ "$(gh pr view <N> --json isCrossRepository --jq .isCrossRepository)" = "false" ] \
    && DEL=--delete-branch || DEL=
  gh pr merge <N> --squash $DEL
  ```
  When per-commit granularity matters (§19.1), or the PR carries a phase tag (below):
  ```bash
  # Same invocation as the merge: shell state does not survive between blocks.
  # Fails safe -- an empty/errored probe is != "false", so the flag is omitted.
  [ "$(gh pr view <N> --json isCrossRepository --jq .isCrossRepository)" = "false" ] \
    && DEL=--delete-branch || DEL=
  gh pr merge <N> --merge $DEL
  ```
  Confirm `state=MERGED` afterwards. `--delete-branch` removes an `origin` head branch in the same
  step; a fork head belongs to the contributor and is never deleted from here (`isCrossRepository`
  true -> omit `--delete-branch`; the port-driver's §3b carries the same rule, #1031).

If any gate condition is ambiguous, post `Held for human review @joeytroy — <reason>` and treat it
as a **Hard stop** for that PR.

Because the user invoked `/pr-checker` with the instruction to merge once the bot is clean, that
invocation **is** the merge authorization for every PR in the list. Do not ask again per PR. This
is the maintainer's deliberate policy for this repository (COMMIT-PR-RULES.md §7: "AI merges once
the §19.1 merge-gate clears"), not a convenience default: the review bot plus the full CI matrix is
the review gate, and the maintainer runs this command themself, interactively, so a human is in the
loop at invocation time and can interrupt at any round. The guardrails that keep it safe are the
ones above: every fork input validated, every finding fixed in-PR rather than waived, every adopted
contributor diff read in full, and the **Hard stops** below, which override this authorization.

After a merge, every remaining PR in the queue is now behind master: run Step 1.3 on the **next**
PR right away so its refresh round starts while you tidy up.

**Contributor pushes during the loop are read, not just merged.** Whenever a head moves between the
verdict you acted on and the merge, diff it against the last reviewed head (`git diff
<reviewed-sha>..<new-head> --stat` and the hunks) and put a one-line summary per commit in the
wrap-up.

## Keep looping: what is NOT a reason to stop

The loop ends only when every PR is merged or a **Hard stop** below applies. In particular:

- **A gate failure in code this branch does not touch** is not a stop. Re-run the failed test in
  isolation 5 times (`dotnet test --filter <name>`, `flutter test <file>`). If it passes in
  isolation and `git diff origin/master...HEAD --name-only` shows no file that could affect it, it
  is a flake: re-run the failed step 5 gate once, push on green, and record the flake (test name,
  failure text, pass rate) in the wrap-up. Two consecutive flakes on the same test still push if
  the isolated runs pass. Only a failure in code this branch changes, or a test that fails in
  isolation every time, blocks the push.
- **A docs-only branch** (`git diff origin/master...HEAD --name-only` lands entirely in
  `design/`, `docs/`, `.claude/`, `.github/ISSUE_TEMPLATE/` or root-level `*.md`) runs only the
  three cheap gates: unicode scan, the Python tooling tests, and the design-doc existence check.
  Do not build the server or the client for it.
- **A bot round with new findings** is the normal case, not a reason to report back. Fix, run the
  step 5 gates, push, poll, repeat. Report only in the wrap-up, or when a hard stop is hit.
- **Waiting is never a stopping point.** Every wait (step 5 gates, verdict poll, CI checks,
  update-branch) runs as ONE background chain that continues into the next action on its own:
  `run_gates && push && poll` for a fix round, `update-branch && poll && merge` for a refresh.
  Never end the turn with "I'll push when the build finishes"; chain it. Every poll exit other than
  a verdict is non-zero (Step 2), so `poll && merge` cannot merge on an empty verdict; a refresh
  verdict can still carry new Defects, so the merge half gates on the printed verdict's **last**
  line (the prompt defines the sign-off as the last line, and a review can quote either string in
  its body) — with `poll` = the Step 2 block saved to a file:
  `V=$(mktemp); bash poll.sh > "$V" && [ "$(sed -e 's/[[:space:]]*$//' "$V" | grep -v '^$' | tail -n 1)" = "✅ Approved" ] && { [ "$(gh pr view <N> --json isCrossRepository --jq .isCrossRepository)" = "false" ] && DEL=--delete-branch || DEL=; gh pr merge <N> --squash $DEL; }`
  (the fork probe sits inside the same chain so an unset `$DEL` cannot silently drop the flag)
  (`--merge` instead when the PR carries a phase tag, as above — and a phase-boundary PR must be tagged per Step 4 before this line runs).
- **A Defect you disagree with** is still fixed or wired into the skill/docs when there is any
  reasonable change that satisfies it. Only a Defect that would require a wrong or unsafe change
  becomes a hard stop. A Note you disagree with is a wrap-up line, not a change.

## Hard stops (the only reasons to hand back to the user)

- A Defect that cannot be reproduced by the "Prove it before you push" step 1 probe, and for which
  no change would satisfy it: quote the finding and the probe, hand back for a ruling.
- The bot rejects (`⚠️ Issues found`) with an empty Defects section. The prompt derives the sign-off
  from Defects, so this is a workflow bug, not a review; quote the comment and hand back rather
  than fixing Notes to satisfy it.
- A PR's head branch name or fork owner fails the Step 0 validation, or a contributor's diff
  contains changes outside the reviewed finding that you cannot vouch for.
- A Dependabot PR whose diff contains anything beyond a version bump.
- The bot finding requires a product decision (change a default, drop a platform, alter a
  user-facing behaviour) that the PR author did not intend.
- A check-gate skip you cannot attribute to the `changes` path gate (Step 4.1).
- Merge conflicts that cannot be resolved without choosing between two contributors' intents.
- The review action skipped a workflow-editing PR (poll exit `2`): hand it back for eyeball review.
  A PR that edits `claude-review.yml` is never merged on an empty verdict.
- The review workflow itself is broken (two consecutive 30-minute timeouts, or two failed runs,
  after the relabel tricks) — report the run URL.

State the blocker in one or two sentences, finish every other PR in the list, and say exactly which
PR was left and why.

## Wrap-up

Before the report, prune what the loop created locally: `git checkout master && git pull --ff-only`,
`git worktree remove <path>` for any worktree first (a branch checked out in a worktree cannot be
deleted), then `git branch -d <branch>` for every branch checked out during the run (`-d` refuses
anything unmerged, which is the point). Confirm `git branch -r` shows no merged head branches left
behind on origin.

One table: PR, title, rounds, final verdict, merge SHA (or "left open: reason"). Under it: any
judgment notes left unpushed, any notes from the post-cleanup approval, any `design/PORT_TODO.md`
entries added, and any contributor commits that landed mid-run, one line each.

**Retrospective, one line per PR:** which bot findings were about code pushed earlier in the same
loop (a fix that introduced the next finding), and what probe would have caught each before the
push. If the answer repeats across PRs, the fix belongs in this file's "Prove it before you push"
list or in `design/COMMIT-PR-RULES.md`, in the same session.

Then a single line naming anything the next session should know (e.g. an `update-branch` still
running on a PR outside the list). Update memory only if the loop mechanics themselves changed (new
bot login, new label, new stall trick); the per-PR outcome does not belong in memory.
