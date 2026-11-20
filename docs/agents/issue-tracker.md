# Issue tracker: GitHub

Issues and specs for this repo live as GitHub issues. Use the `gh` CLI for all operations.

## Conventions

- **Create an issue**: `gh issue create --title "..." --body "..."`. Use a heredoc for multi-line bodies.
- **Read an issue**: `gh issue view <number> --json number,title,body,labels,comments`.
- **List issues**: `gh issue list --state open --json number,title,body,labels,comments --jq '[.[] | {number, title, body, labels: [.labels[].name], comments: [.comments[].body]}]'` with appropriate `--label` and `--state` filters.
- **Make an issue a sub-issue of a parent**: `gh issue create --parent <parent> ...`, or `gh issue edit <parent> --add-sub-issue <child>` afterwards (`gh` 2.94+). Older `gh`: `gh api --method POST repos/<owner>/<repo>/issues/<parent>/sub_issues -F sub_issue_id=<child-db-id>` (database id, as in **Blocking** below). Without sub-issues, put `Part of #<parent>` at the top of the child body.
- **Blocking**: GitHub's **native issue dependencies**, the canonical, UI-visible representation. Add an edge with `gh issue create --blocked-by <n>,<n> ...` or `gh issue edit <child> --add-blocked-by <blocker>` (`gh` 2.94+). Older `gh`: `gh api --method POST repos/<owner>/<repo>/issues/<child>/dependencies/blocked_by -F issue_id=<blocker-db-id>`, where `<blocker-db-id>` is the blocker's numeric **database id** (`gh api repos/<owner>/<repo>/issues/<n> --jq .id`, _not_ the `#number` or `node_id`). Where dependencies aren't available, fall back to a `Blocked by: #<n>, #<n>` line at the top of the child body. A ticket is unblocked when every blocker is closed.
- **Count open blockers**: `gh api repos/<owner>/<repo>/issues/<n> --jq .issue_dependencies_summary.blocked_by` (open blockers only; 0 means startable). `gh issue view <n> --json blockedBy` also lists closed blockers, so filter it: `--jq '[.blockedBy.nodes[] | select(.state == "OPEN")] | length'`.
- **Comment on an issue**: `gh issue comment <number> --body "..."`
- **Apply / remove labels**: `gh issue edit <number> --add-label "..."` / `--remove-label "..."`
- **Close**: post the explanation with `gh issue comment <number> --body "..."`, then `gh issue close <number>`. On an already-closed issue, `gh issue close --comment` drops the comment and still exits 0.

The tracker is **`open-astro/openastro-ara`** (the `origin` remote). Clones also carry a `fork` remote (`diegopereiran/openastro-ara`) whose issue numbers do not match upstream, so pass `-R open-astro/openastro-ara` to every `gh issue` command rather than relying on `gh`'s remote inference.

File issues only into the user's own repo. Never file into `mattpocock/skills` (the repo these skills come from) unless the user owns it. In a fork, `gh` can default to the upstream parent, so before the first `gh issue create` of a session, check the target with `gh repo view --json nameWithOwner` and confirm it with the user if it is not their repo.

## Labels every issue carries

Every open issue carries one label from each of three groups. The `Issue labels` workflow (`.github/workflows/issue-labels.yml`, `scripts/check-issue-labels.py`) runs when an issue is opened, reopened, labelled or unlabelled. While a group is missing it keeps **one** comment on the issue naming the missing groups, and deletes the comment once all three are set. It never adds a label or closes an issue. Issues opened by a bot, or labelled `dependencies` or `github_actions`, are exempt.

- **Type**: `bug`, `enhancement`, `documentation` or `question`.
- **Priority**: `P1` (show-stopper: blocks the next release or breaks a real install or night), `P2` (important: correctness or safety before people rely on it), `P3` (should do: product completeness), `P4` (nice to have), `P5` (parked: needs a maintainer decision, or speculative), `P6` (deferred until the end).
- **Size**, as the estimated work for one agent:
  - `XS`: under 250k Opus tokens. A docs change or one-liner, one pass.
  - `S`: 250k to 1M tokens. One file and its test, one CI round.
  - `M`: 1M to 3M tokens. A few files, one or two CI or review rounds.
  - `L`: 3M to 8M tokens. Several subsystems, rig or review loops.
  - `XL`: over 8M tokens. Epic-sized; split it.

When you file an issue, set all three groups. Triage state labels (`needs-triage`, `ready-for-agent`, ...) are separate; see `triage-labels.md`.

## Milestones

A release has one open milestone at a time, named for the next tag: `vX.Y.Z-beta.N`, then `vX.Y.Z` (the process is in `docs/RELEASES.md`). It holds everything that tag waits on: P1 and P2 fixes, one issue per hardware check, regressions found during the betas, and any fix a maintainer admits to `stable/X.Y` by name. The tag is pushed only when it has no open items; then it closes and the milestone for the next tag opens.

Rules:

- A regression found in a beta goes on the open milestone. Its fix PR targets `stable/X.Y` only when the defect is new since the previous stable release (the admission test in `docs/RELEASES.md`; for 1.0 every defect qualifies); otherwise it targets `master` and the issue leaves the milestone.
- Do not put feature work on a release milestone. Features ship from `master` in the next minor.
- Add an issue with `gh issue edit <n> -R open-astro/openastro-ara --milestone "v1.0.0-beta.1"`. List what is left with `gh issue list -R open-astro/openastro-ara --milestone "v1.0.0-beta.1" --state open`.
- A PR merged into `stable/**` does not close its issues through GitHub; `.github/workflows/close-linked-issues.yml` closes the ones it names with `Closes`, `Fixes` or `Resolves`.

## Pull requests as a triage surface

**PRs as a request surface: no.** _(Set to `yes` if this repo treats external PRs as feature requests; `/triage` reads this flag.)_

When set to `yes`, PRs run through the same labels and states as issues, using the `gh pr` equivalents:

- **Read a PR**: `gh pr view <number>`, then `gh pr view <number> --comments` (off a TTY, `--comments` prints only the comments, not the body), and `gh pr diff <number>` for the diff.
- **List external PRs for triage**: `gh api --paginate 'repos/{owner}/{repo}/pulls?state=open' --jq '.[] | select(.author_association | IN("OWNER","MEMBER","COLLABORATOR") | not) | {number, title, author: .user.login, author_association, labels: [.labels[].name]}'`.
- **Comment / label / close**: `gh pr comment`, `gh pr edit --add-label`/`--remove-label`, `gh pr close`.

GitHub shares one number space across issues and PRs, so a bare `#42` may be either: resolve with `gh pr view 42` and fall back to `gh issue view 42`.

## When a skill says "publish to the issue tracker"

Create a GitHub issue.

## When a skill says "fetch the relevant ticket"

Read it as in **Read an issue** above.

## Wayfinding operations

Used by `/wayfinder`. The **map** is a single issue with **child** issues as tickets.

- **Map**: a single issue labelled `wayfinder:map`, holding the Notes / Decisions-so-far / Fog body. `gh issue create --label wayfinder:map`.
- **Child ticket**: an issue linked to the map as a GitHub sub-issue (see **Make an issue a sub-issue of a parent**). Where sub-issues aren't enabled, add the child to a task list in the map body and put `Part of #<map>` at the top of the child body. Labels: `wayfinder:<type>` (`research`/`prototype`/`grilling`/`task`). Once claimed, the ticket is assigned to the driving dev.
- **Blocking**: see **Blocking** under Conventions.
- **Frontier query**: list the map's open children (`gh issue list --state open`, scoped to the map's sub-issues / task list), drop any with an open blocker (see **Count open blockers**, or an open issue in the `Blocked by` line) or an assignee; first in map order wins.
- **Claim**: `gh issue edit <n> --add-assignee @me`, the session's first write.
- **Resolve**: `gh issue comment <n> --body "<answer>"`, then `gh issue close <n>`, then append a context pointer (gist + link) to the map's Decisions-so-far.
