---
name: ara-pr-investigation
description: Investigate a pull request against openastro-ara end to end — read the diff, judge whether the change is effective and safe, launch the Ara daemon and Flutter client on the PR's code to show the change in the running app with screenshots, and break down any server-side impact for the user. Use this whenever the user asks to check, investigate, review, vet, verify, try out, or "look at" a PR (a number, a URL, a branch, or "the PRs from <contributor>"), especially contributor/fork PRs such as those from Jewber11, even if they don't say "review". Not for driving a PR to merge (that is /pr-checker) and not for a diff-only bug hunt with no app run (that is /code-review).
---

# ara-pr-investigation

You are investigating one or more PRs on behalf of a maintainer who wants a
**full breakdown** before deciding what to do with them. Most of these PRs come
from outside contributors working on their own hardware and toolchain (often a
Linux box, sometimes no hardware at all), so the questions that matter are:

1. **Is it effective?** Does the change do what the PR says, and is the PR's own
   validation section honest about what was and was not tested?
2. **What does it look like in the client?** Not "the diff touches a widget" but
   the actual screen, in the actual running app, with a capture the user can see.
3. **What does it do to the server?** The daemon runs unattended on a Raspberry
   Pi next to real equipment. A server change needs a much more careful
   explanation than a client change: contracts, state on disk, hardware paths,
   packaging and hardening.

The deliverable is a report (structure in `references/report-template.md`).
This skill **recommends and never acts on the PR**: no merging, labelling,
commenting, or pushing to the contributor's branch. Those are the user's
calls, and the tools for them live in `/pr-checker`.

## Inputs

A PR number, URL, branch, or an author name ("the PRs from Jewber11"). For an
author, list their open PRs (`gh pr list --author <login> --state open`) and
investigate each one in turn, most recently updated first, unless the user
narrowed it. Each PR gets its own report section; finish with a one-paragraph
cross-PR summary (do they overlap? do they conflict with each other?).

## Procedure

### 1. Facts first (read-only, ~10 s)

```bash
.claude/skills/ara-pr-investigation/scripts/pr-facts.sh <n>
```

It prints title/author/fork status, body, files, which CI buckets the change
hits (`dotnet` / `client` / docs), status checks, the bot review verdict (or
the note that a fork PR without the `safe-to-review` label never gets one),
human comments, commits, and how far behind `master` the branch is including
which files master has since touched. Read all of it before opening the diff —
the body's *Validation* section tells you what the contributor could not test,
which is exactly where to look hardest.

### 2. Read the diff with intent

`gh pr diff <n>` (or `git diff <merge-base> refs/pr/<n>` after the facts script
fetched the ref). For each claim in the PR body, find the code that makes it
true and decide: verified, not verified, contradicted. Then look for what the
body does *not* mention: new behaviours, deleted checks, changed defaults,
touched files that don't belong to the stated scope.

Project positions that decide a PR before code quality does (check the
memory index for the current list; today: **no turnkey install/update
scripts** — support is for paying users, the community helps itself, and
`docs/DEPLOY.md`'s APT path is the only documented route). A PR that collides
with one is "parked on policy"; say so at the top of the report and keep the
code review short.

Project rules that turn into findings when violated:

- A new user-facing setting needs a `settings/registry.dart` entry (§61.4); a
  new ⓘ icon needs a `help/registry.dart` entry (§69.4). CI's registry gate
  catches the mechanical part; you check that the text is accurate.
- `design/API_CONTRACT.md` lists the client/daemon contract; a server change to
  a listed endpoint without a doc change is a defect.
- New or changed logic ships with tests (§14.7). Note tests that only assert
  the happy path the body already claims.
- Bare `grep` on this machine is ugrep and silently skips files; use
  `/usr/bin/grep` or the Grep tool when searching.

Use `/code-review`'s discipline for correctness (read callers, trace data, do
not guess), but keep the scope to "does this PR do its job safely". Big
refactors it *could* have done are Notes at most.

### 3. Run it (the part that makes this skill different)

Follow `references/launch-and-capture.md`. In short: worktree on
`refs/pr/<n>`, merge-preview against `origin/master` if the branch is behind,
build, launch the daemon (PR's if `dotnet=true`, else master's) on a throwaway
profile dir, launch the client detached, navigate to the affected screen,
capture golden path + one edge case (+ a `master` "before" when the change
modifies existing UI), **read each capture back** and describe it.

Decide honestly what can be exercised on this host:

| PR touches | What to run |
|---|---|
| Client UI | client on PR code + any daemon; screenshots required |
| Client service/state only (no visible change) | client on PR code; say "no visual change" and show the log/behaviour instead |
| Server endpoint the client consumes | PR daemon + PR client; hit the endpoint with `curl` as well |
| Server hardware / Alpaca / guider paths | PR daemon against the Alpaca simulators (`scripts/get-alpaca-simulators.sh`) or the Pi rig's bridge at `openastro.lan:6800`; state clearly what was and wasn't reachable |
| Packaging / systemd / CI / docs | nothing to launch; review the files against `docs/DEPLOY.md` and the Pi hardening notes in memory |

When a build fails on the PR's code, that is the headline finding; capture the
first error and stop building. When the app runs but the change cannot be
reached (needs a mount, needs a night sky), say what you could confirm and
what stays unverified rather than inferring success.

### 4. Server-impact discussion (whenever `dotnet=true` or `packaging/` changes)

Write this section as if briefing someone about to `apt-get install` the next
`.deb` on their observatory Pi. Cover: what changes for a running daemon,
what it writes or migrates on disk, which hardware paths it touches and whether
they were exercised, and how it interacts with the systemd hardening
(`NoNewPrivileges` kills sudo inside the daemon; `RestrictAddressFamilies`
needs `AF_NETLINK`; both have bitten before, see the `pi-rig-openastro-lan`
memory). Finish with the risk if it is wrong and how to roll back.

### 5. Report, then clean up

Use the template. Verdict first, then **one plain-English paragraph** of what
the PR actually does as a user or the daemon would experience it (the
maintainer asked for exactly this after the first run: the detail was right
but they still had to ask "so what is this doing"), then claims vs reality,
the client walkthrough with captions, the server section, findings (Defects
before Notes, each with `file:line`), process state, policy fit, and one
recommended next step. Keep it
tight — the user asked for a breakdown, not a transcript. Then tear down
(quit app, kill only the daemon you started, remove the worktree and temp ref)
and confirm the main checkout is untouched (`git status --short`).

## Things that go wrong

- **`flutter run` in the background** exits and kills the app on macOS — build
  and `open` the `.app` instead.
- **Clicking inside the app needs Accessibility.** `osascript`/System Events is
  refused from this shell unless the terminal has been granted it; window
  captures still work through `scripts/capture-window.sh` (CoreGraphics). When
  you cannot click, ask the user to navigate and capture after, or say the
  screen could not be reached — do not pretend.
- **Port 5555 already in use** — the user may be running a daemon; use
  `OPENASTROARA_PORT` for yours and tell the client the new port.
- **Bot review "green" but skipped** — the `review` status check passing only
  means a comment exists, and on fork PRs it is SKIPPED. Read the facts
  script's bot section, not the check colour.
- **Gating a step on `dotnet build | grep`** hides failures; check `$?`.
- **Astrometry natives** are not built by `dotnet build`; stage them with
  `scripts/build-astrometry-natives.sh` or the daemon logs a warning and
  rise/set code paths misbehave.
- **Contributor's Flutter version** may differ from the pin in
  `.flutter-version`; a PR that says "analyzer clean on 3.44" was not checked
  on the pinned SDK — CI is the arbiter, mention it if CI and the body disagree.
