# Releases, betas and stable branches

This page says how OpenAstro Ara is versioned and released. The scheme is
AlpacaBridge's (its `docs/beta-channel.md`), with one big difference: an Ara
release is **source only**. A release is a git tag, plain-language notes and a
full source tarball. No `.deb`, apt package or app binary is published. You
build the daemon and the app yourself (see [RUNNING.md](RUNNING.md)).

Development stays on `master`. Each minor or major release is cut onto a stable
branch `stable/X.Y`, tested as a beta, hardened with bug fixes only, and then
promoted to a stable release. Day-to-day PR work on `master` does not change.

## Version spellings

One root file, `VERSION`, holds the version. It is one line: `X.Y.Z` for a stable
release or `X.Y.Z~betaN` (N is 1 or more) for a beta. `master` holds the last
stable version (now `0.0.1`, before 1.0). A beta `VERSION` only ever lives on a
`stable/X.Y` branch.

Git refs cannot hold `~`, and Debian needs `~` so a beta sorts below its final
version. So the version has four spellings. `scripts/version.py` and
`scripts/release-tag.py` map one to the other.

| Stage       | `VERSION` file | Git tag         | Shown in the app and API | Client `pubspec.yaml`     | `.deb` version (tagged build) |
| ----------- | -------------- | --------------- | ------------------------ | ------------------------- | ------------------ |
| First beta  | `1.0.0~beta1`  | `v1.0.0-beta.1` | `1.0.0-beta.1`           | `1.0.0-beta.1+<build>`    | `1.0.0~beta1`      |
| Second beta | `1.0.0~beta2`  | `v1.0.0-beta.2` | `1.0.0-beta.2`           | `1.0.0-beta.2+<build>`    | `1.0.0~beta2`      |
| Stable      | `1.0.0`        | `v1.0.0`        | `1.0.0`                  | `1.0.0+<build>`           | `1.0.0`            |
| Hotfix      | `1.0.1`        | `v1.0.1`        | `1.0.1`                  | `1.0.1+<build>`           | `1.0.1`            |
| Next beta   | `1.1.0~beta1`  | `v1.1.0-beta.1` | `1.1.0-beta.1`           | `1.1.0-beta.1+<build>`    | `1.1.0~beta1`      |

The app, `/server/info` (`version`), `/server/versions` (`daemonVersion`) and the
mDNS record all show the middle spelling (SemVer). The `~` form appears only in
`VERSION` and in `.deb` version strings.

The README carries a badge line with the same version, in this exact form:

```markdown
#### [1.0.0-beta.1] - 2026-12-01 · [Changelog](CHANGELOG.md)
```

The app takes its own version from a constant, `kAraAppVersion` in
`client/openastroara_client/lib/version/ara_version.dart`, in the display
spelling. It does not read the platform's version, because Apple platforms can
drop the `-beta.N` and make a beta look stable. Only the build number (after
the `+`) comes from the platform.

`python3 scripts/version.py --check` fails unless `VERSION`, the `pubspec.yaml`
version (before the `+`), `kAraAppVersion` and the badge agree.

**Never edit `VERSION`, the badge, the `pubspec.yaml` version or
`kAraAppVersion` in a feature PR.**
Only a release PR (`/bump-release`) or a merge down changes them.

## Branch model

- `master`: all development. PRs go here as usual and land through the merge
  queue, which squashes.
- `stable/X.Y`: long-lived. It is cut from `master` at the first beta of `X.Y`,
  when `master` holds what the release should contain. The name is
  `stable/X.Y`, not `release/...`, because `/bump-release` uses
  `release/X.Y.Z` for its short-lived release PR branch.
- At most two stable branches exist at once: the current stable and, during a
  beta, the next one.

## Rules for the stable branch

- Bug and stability fixes only: crashes, hangs, races, wrong device behaviour,
  build and install failures, and docs fixes for the release itself. No
  features, no refactors, no new device support. Every fix also passes the
  admission test below.
- **Admission test.** A PR targets `stable/X.Y` only when it fixes a defect or
  regression introduced after the previous stable release (the last stable tag
  before `X.Y.0`), so that release did not have the defect. Everything else
  targets `master`: a defect the previous stable release also had, and every
  feature, refactor, test, tooling or docs change. "Fixes a real bug" is not
  enough: a long-standing defect is a `master` fix that ships in the next minor.
  Build, install and release-docs fixes qualify only when the problem is new
  since the previous stable release.
- **1.0 has no previous stable release**, so on `stable/1.0` every defect fix
  passes the admission test. Features, refactors and new device support still
  go to `master`. `stable/1.0` was cut on 2026-11-20 so that the community beta
  holds only what was on `master` then.
- Some fixes go to `master` even when they pass the test, because they undo the
  hardware testing the beta exists to gather: a rewrite of an exposure, slew,
  guiding or sequencer state machine, a new device seam or fake, or a change to
  what the daemon tells a client a device can do. A maintainer may still admit
  such a fix by naming it in an issue on the release's open milestone. That is
  a maintainer decision, never a rule a contributor meets by filing an issue, and
  it never admits a defect the previous stable release also had.
- A PR that misses the bar is retargeted with `gh pr edit --base master` before
  review, not reviewed on the stable branch.
- Fixes land on the stable branch and reach `master` through a merge down.
  **No cherry-picks.**
- A fix PR into `stable/X.Y` runs the same CI and review bot, and adds its
  `changelog.d/` fragment as usual. The fragment reaches `master` in the merge
  down and is used at promotion.
- GitHub closes `Closes #N` issues only for a PR merged into the default branch.
  A PR merged into `stable/**` closes the issues it names with `Closes`, `Fixes`
  or `Resolves` through `.github/workflows/close-linked-issues.yml`
  (`scripts/close-linked-issues.py`). `Refs #N`, other repositories and PR
  numbers are ignored.
- **Bootstrap of a new stable branch.** `close-linked-issues.yml` runs the
  script from the merged commit on the stable branch, so it works from the first
  fix. `issue-labels.yml` runs on issue events, which use the default branch's
  workflows, so it does nothing until the first merge down brings it to
  `master`.
- CI also runs on pushes to `stable/**`. The repository ruleset "stable branch
  protection" on `refs/heads/stable/**` allows pull requests only, blocks
  deletion and force-push, allows only the merge-commit method, and needs the
  same checks as `master`. It lives in GitHub
  settings, not in this repository.
- Each stable branch has its own merge queue, which merges with a merge
  commit. GitHub does not allow a merge queue on a wildcard, so cutting
  `stable/X.Y` also adds a ruleset "stable/X.Y merge queue" on that exact
  branch (merge method MERGE). The "stable branch protection" checks do not
  require a PR to be up to date; the queue tests each PR on top of the ones
  ahead of it. When the review bot approves a PR into `stable/X.Y`,
  `scripts/auto-queue.sh` turns on auto-merge, which puts it in that queue.
- Keep a fix branch for `stable/X.Y` up to date by merging `origin/stable/X.Y`
  into it, never `origin/master`: `master` holds work that must not reach the
  stable branch.

## Merge down

A merge down brings a stable branch's fixes into `master` (or into a newer
stable branch). It follows every beta tag, the stable tag and every hotfix.

- **Head branch.** A short-lived branch cut from `stable/X.Y`, named for the
  receiving branch: `merge-down/X.Y-to-master`, or `merge-down/X.Y-to-X.Z` for
  a newer stable branch. Never use `stable/X.Y` itself as the head: GitHub
  deletes a merged PR's head branch, and updating a PR head merges the base into
  it, which would put `master`'s unreleased work on the stable branch.
- **Merge commit, by a maintainer, outside the queue.** `master`'s merge queue
  squashes, and a squash hides the branch history and makes the next merge down
  conflict again. So a maintainer merges a merge-down PR with the merge-commit
  method and an admin bypass (`gh pr merge <N> --merge --admin`). Never squash
  it, and never cherry-pick instead.
  `scripts/auto-queue.sh` never puts a `merge-down/*` PR in the queue.
- **Version files.** The receiving branch keeps its own `VERSION`, README badge,
  `pubspec.yaml` version and `kAraAppVersion`. So `master` never carries a beta version, and a
  dev build of `master` never reports the same version as a published beta.
  The one exception: a merge into `master` that brings a stable release newer
  than `master`'s `VERSION` (the promotion, or a hotfix before the next
  promotion). That release is now the newest, so its `VERSION`, badge,
  `pubspec.yaml` version and `kAraAppVersion` come across.
- `docs/releases/` notes and code come across as they are. A dated
  `## [X.Y.Z]` section of `CHANGELOG.md` goes in version order below the
  receiving branch's sections.
- **Falsified-by.** The PR's commits were already reviewed on the stable
  branch, so the `falsified-by` gate exempts a merge down
  (`scripts/merge-down.py`): only when the head name matches, every commit other
  than merge commits is already on `stable/X.Y`, and no merge commit adds test
  changes. A new commit on the head puts the PR back under the gate.

`/bump-release` has the exact commands.

## Lifecycle of one release

1. **Cut.** Branch `stable/X.Y` from `master`. A release PR on that branch sets
   `VERSION` to `X.Y.0~beta1`, the README badge, `pubspec.yaml` and
   `kAraAppVersion` to `X.Y.0-beta.1`, and adds `docs/releases/X.Y.0-beta.1.md`, started from
   `python3 scripts/changelog-fragments.py --preview --version X.Y.0~beta1`.
   The `changelog.d/` fragments are not used up.
2. **Beta.** A maintainer tags the release PR's merge commit on `stable/X.Y`
   with an annotated tag `vX.Y.0-beta.N` and pushes it. `release.yml` checks the
   tag sits on its own `stable/X.Y` and matches `VERSION`, then makes a GitHub
   pre-release from the notes file with the source tarball attached. Then merge
   down into `master`.
3. **Fix.** A bug found in the beta that the previous stable release did not
   have is fixed by a PR against `stable/X.Y` (see the admission test). It is not
   fixed on `master` first and cherry-picked. A defect the previous release also
   had goes to `master`.
4. **Re-beta.** Each batch of fixes becomes the next beta: a release PR sets
   `X.Y.0~beta(N+1)`, adds its notes file, and the merge commit is tagged. Merge
   down after every beta, so each merge down stays small.
5. **Promote.** When the promotion criteria below hold, `/bump-release` runs on
   `stable/X.Y`: `VERSION` becomes `X.Y.0`,
   `python3 scripts/changelog-fragments.py --release X.Y.0 --date YYYY-MM-DD`
   writes the dated `CHANGELOG.md` section and deletes the fragments, and the
   merge commit is tagged `vX.Y.0`. `release.yml` makes a normal (Latest)
   release.
6. **Final merge down.** Merge `stable/X.Y` into `master` once more. It carries
   the dated changelog section, the new `VERSION`, badge, `pubspec.yaml`
   version and `kAraAppVersion` (the exception above), and the deleted fragments. If `master`
   touched the same lines as a stable fix, the maintainer resolves the conflict
   by hand.
7. **Hotfix.** A regression in a stable release becomes `X.Y.1` on the same
   branch: fixed on `stable/X.Y`, released, then merged down. No beta round is
   needed unless the maintainer wants one. If `stable/X.(Y+1)` already exists,
   merge the hotfix into both it and `master`, as two PRs with two heads
   (`merge-down/X.Y-to-master` and `merge-down/X.Y-to-X.(Y+1)`).
8. **Retire.** `stable/X.Y` keeps getting hotfixes while the next branch is in
   beta. It retires when the next minor or major is promoted, not when the next
   branch is cut. A retired branch stays for history and gets no more tags.

## Milestones

A release has one open GitHub milestone at a time, named for the next tag
(`vX.Y.Z-beta.N`, then `vX.Y.Z`). It holds everything that tag waits on: the
P1 and P2 fixes, the hardware checks (one issue per check), regressions found
during the betas, and any fix a maintainer admits to the stable branch by name.

- The tag is pushed only when the milestone has no open items.
- When the tag is pushed, close the milestone and open the next one for the
  next tag (the next beta, or `vX.Y.Z` itself). Move any item that waits for
  that next tag into it.
- Features never go on a release milestone. They ship from `master` in the
  next minor.

## Promotion criteria

All of these hold before a beta is promoted to stable:

- At least 14 days since the last beta tag, with no open regression on the
  `vX.Y.Z` milestone.
- For a major release (`X.0.0`), also at least 28 days since its first beta
  tag, so a major release spends at least four weeks in beta.
- At least one full night on the rig by a maintainer with the final beta.
- No open issue on the `vX.Y.Z` milestone. Ara has no ConformU step
  (that is AlpacaBridge's driver test); its hardware checks are the issues on
  this milestone.

A hotfix needs none of these.

## What a release contains (source only)

A GitHub Release for tag `vX.Y.Z` (or `vX.Y.Z-beta.N`, marked pre-release) holds:

- **The notes**: `docs/releases/<semver>.md`, written in plain language (see
  [releases/README.md](releases/README.md)), with a link to the
  `CHANGELOG.md` section.
- **`openastro-ara-<semver>-source.tar.gz`**: the full source with every git
  submodule (`third_party/erfa`, `third_party/supernovas`,
  `third_party/astap`), unpacking into one folder `openastro-ara-<semver>/`.
  `scripts/source-archive.sh` builds it, the same way every time.
- **`openastro-ara-<semver>-source.tar.gz.sha256`**: its checksum.

GitHub also adds its own "Source code (zip)" and "(tar.gz)" links to every
release. **Do not use them**: they leave out the submodules, so the build fails.

Nothing else is attached. There is no `.deb`, no apt upload and no app package.

## Building a release

Either download the tarball and check it:

```bash
sha256sum -c openastro-ara-1.0.0-beta.1-source.tar.gz.sha256
tar xzf openastro-ara-1.0.0-beta.1-source.tar.gz
cd openastro-ara-1.0.0-beta.1
```

or clone the tag with its submodules:

```bash
git clone --branch v1.0.0-beta.1 --recurse-submodules https://github.com/open-astro/openastro-ara.git
cd openastro-ara
```

Then follow [RUNNING.md](RUNNING.md) to build and run the daemon and the app,
and [DEPLOY.md](DEPLOY.md) to set the daemon up on a Raspberry Pi.

## The app and the daemon must match

Build the app and the daemon from the **same release**. The app reads the
daemon's version when it connects. When the major or minor number differs (for
example app `1.1.0`, daemon `1.0.2`), the About panel and the first-run screen
show a warning that blocks nothing: "Server 1.0.2 and app 1.1.0 differ; update
both from the same release." A different patch number (`1.0.1` with `1.0.2`)
gets no warning.

## Dev and CI builds

CI builds a `.deb` from untagged commits for testing only. Its version comes
from `scripts/nightly-version.py`: `<next>~~nightly<YYYYMMDDHHMM>.g<sha7>`, where
`<next>` is the release that code will ship in. The double `~` makes it sort
below that release's first beta too. On `stable/X.Y` between betas (VERSION
`X.Y.Z~betaN`) it is `X.Y.Z~betaN+nightly<YYYYMMDDHHMM>.g<sha7>`, which sorts
after beta N and before beta N+1. On `master` it is never below `X.(Y+1).0`
once `stable/X.Y` or a `vX.Y.*` tag exists, because `master` is already past
that line. These packages stay as CI artifacts. They are **never published** as
a release, to apt or anywhere else.

## Who does what

The maintainers cut stable branches, tag betas and stable releases, promote,
and merge the merge-down PRs. Contributors send fix PRs against `stable/X.Y`
like any other PR, when the fix passes the admission test.
