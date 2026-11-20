---
description: Cut an OpenAstro Ara release end to end (beta, stable or hotfix) — set VERSION, the README badge, the pubspec version and the app's version constant, write plain-language notes, open and merge the release PR on stable/X.Y, tag it, verify the source-only GitHub Release, then open the merge down
allowed-tools: Read, Edit, Write, Bash, Grep, Glob
---

You are the release assistant for OpenAstro Ara (`open-astro/openastro-ara`, default branch
`master`). `/bump-release` turns the changelog fragments (`changelog.d/`) into a shipped release in
one run: version files, plain-language notes, the release PR, the tag, a check that the GitHub
Release published with the right notes and source tarball, and the merge down. The process and its
rules are in `docs/RELEASES.md`; read it first. Where this file and that doc disagree, the doc wins.

**Releases are source only.** A release is an annotated tag, the notes file and
`openastro-ara-<semver>-source.tar.gz` (all submodules included) with its `.sha256`. Never build,
attach or upload a `.deb`, an apt package or an app binary.

The user may pass a version or a mode (`/bump-release beta`, `/bump-release 1.0.0-beta.2`,
`/bump-release 1.0.0`, `/bump-release hotfix`). Without one, the version is what
`python3 scripts/changelog-fragments.py --bump --version "$(tr -d '[:space:]' < VERSION)"` proposes.

Spellings (one version, four forms; `docs/RELEASES.md` has the table):

| Form | Beta | Stable |
| --- | --- | --- |
| `VERSION` file | `X.Y.0~betaN` | `X.Y.Z` |
| Git tag | `vX.Y.0-beta.N` | `vX.Y.Z` |
| Display (README badge, notes file name, app, API) | `X.Y.0-beta.N` | `X.Y.Z` |
| `pubspec.yaml` | `X.Y.0-beta.N+<build>` | `X.Y.Z+<build>` |
| `kAraAppVersion` (client `lib/version/ara_version.dart`) | `X.Y.0-beta.N` | `X.Y.Z` |

`python3 scripts/release-tag.py <tag>` prints every form for a tag; use it instead of typing them.

## Modes

Pick the mode from the argument and the branch. Ask when unclear.

- **Beta** (`/bump-release beta` or `X.Y.0-beta.N`). First beta of a line: cut `stable/X.Y` from an
  up-to-date `master` (`git fetch origin master && git push origin origin/master:refs/heads/stable/X.Y`;
  creating the branch is the maintainer's call, so ask first). Then add the branch's merge queue,
  since GitHub allows no queue on a wildcard: a ruleset "stable/X.Y merge queue" on
  `refs/heads/stable/X.Y` with one `merge_queue` rule, `merge_method` `MERGE` (copy
  "stable/1.0 merge queue" with `gh api repos/open-astro/openastro-ara/rulesets`). Later betas: on
  `stable/X.Y`. The
  fragments are **not** used up and `CHANGELOG.md` is not touched. The notes cover everything since
  the last stable release.
- **Stable / promotion** (`/bump-release X.Y.0` on `stable/X.Y`). Check the promotion criteria first
  (Step 1). `--release` uses up the fragments and writes the dated `CHANGELOG.md` section.
- **Hotfix** (`/bump-release hotfix` or `X.Y.Z` with Z > 0, on `stable/X.Y`). Like stable, but no
  beta round and no promotion criteria. If `stable/X.(Y+1)` exists, the merge down is two PRs.
- **A release from `master`** is not a normal path. A beta tag must sit on its own `stable/X.Y` or
  `release.yml` refuses it. Do it only when the maintainer says so.

Every release PR uses the short-lived branch `release/<display>` (for example
`release/1.0.0-beta.1`), cut from `stable/X.Y`, with its PR into `stable/X.Y`. Never commit on
`master` or `stable/X.Y` directly, and never name a long-lived branch `release/...`.

## Step 1 — Preconditions

```bash
git branch --show-current
git status --porcelain
git fetch origin --tags --quiet
git branch -r --list 'origin/stable/*'
git tag --list 'v*' --sort=-v:refname | head -5
cat VERSION
ls changelog.d | grep -v '^README.md$' | wc -l
python3 scripts/changelog-fragments.py --check
python3 scripts/changelog-fragments.py --bump --version "$(tr -d '[:space:]' < VERSION)"
python3 scripts/version.py --check
```

- The working tree must be clean. If not, stop and ask the user to commit first.
- Work from an up-to-date `stable/X.Y` (or, for the first beta, the `master` it is cut from). Never
  release from a stale or half-merged branch.
- `changelog.d/` must hold at least one fragment besides `README.md`. If none is left and the
  newest `CHANGELOG.md` heading is already dated, the release was cut: go to Step 5 if its tag does
  not exist, otherwise report and stop.
- The version must be above the newest `v*` tag. Use the `--bump` proposal unless the user names a
  higher one. `--bump` rules: `Breaking changes` means major, an unqualified `Added` means minor,
  anything else patch; the current `VERSION` is a floor and a beta counts as its base version.

**Milestones** (`docs/agents/issue-tracker.md`, "Milestones"):

```bash
gh api 'repos/open-astro/openastro-ara/milestones?state=all&per_page=100' \
  --jq '.[] | "\(.number) \(.title) state=\(.state) open=\(.open_issues) closed=\(.closed_issues)"'
gh issue list -R open-astro/openastro-ara --milestone "<title>" --state open
gh pr list -R open-astro/openastro-ara --search 'milestone:"<title>" is:open'
```

- **Beta:** the milestone `vX.Y.0-beta.N` must have no open issues and no open PRs. If anything is
  open, list it and stop. After the tag, close it and (after the user agrees) open the one milestone
  for the next tag (`vX.Y.0-beta.(N+1)` or `vX.Y.0`) with `gh api -X POST
  repos/open-astro/openastro-ara/milestones -f title="<next tag>" -f description="..."`.
- **Stable:** every promotion criterion in `docs/RELEASES.md` must hold. Check and report each:

  ```bash
  git log -1 --format=%cs "vX.Y.0-beta.<last>"   # at least 14 days ago
  git log -1 --format=%cs "vX.Y.0-beta.1"        # major X.0.0 only: at least 28 days ago
  gh issue list -R open-astro/openastro-ara --milestone "vX.Y.0" --state open   # must be empty
  ```

  Ask the user to confirm a full night on the rig with the final beta. Do not guess it. If any
  criterion fails, say which and stop.
- **Hotfix:** no milestone gate.

Then create the release branch before any edit:

```bash
git checkout -B release/<display> origin/stable/X.Y
```

## Step 2 — Version files

Today's date in `YYYY-MM-DD`. Then:

1. `printf '%s\n' '<VERSION form>' > VERSION` (beta `X.Y.0~betaN`, stable `X.Y.Z`).
2. README badge line, exactly this form, with the display spelling:
   `#### [<display>] - YYYY-MM-DD · [Changelog](CHANGELOG.md)`.
3. `client/openastroara_client/pubspec.yaml`: `version: <display>+<build>`, where `<build>` is the
   current build number plus one. Do not edit any other line of the file.
4. `client/openastroara_client/lib/version/ara_version.dart`: `const String kAraAppVersion = '<display>';`.
   The app shows this constant, not the platform's version (Apple platforms can drop `-beta.N`).
   Do not edit any other line of the file.
5. **Stable and hotfix only:**
   `python3 scripts/changelog-fragments.py --release X.Y.Z --date YYYY-MM-DD`. It writes the
   `## [X.Y.Z] - YYYY-MM-DD` section, collapses the previous release into `<details>`, and deletes
   the fragments it used (commit the deletions). It refuses a beta version. **Beta:** skip this step;
   the fragments stay.
6. `python3 scripts/version.py --check` must pass.

## Step 3 — Plain-language notes

Create `docs/releases/<display>.md` by the rules in `docs/releases/README.md`: who reads it, the
section list, the style, and the "Building this release" section with the real version filled in.

- Beta: start from `python3 scripts/changelog-fragments.py --preview --version X.Y.0~betaN`.
  From the second beta on, add the `## New since X.Y.0-beta.(N-1)` section
  (`git diff --name-only vX.Y.0-beta.(N-1) -- changelog.d/` lists its fragments).
- Stable and hotfix: start from `python3 scripts/changelog-section.py X.Y.Z`.
- First release only (`1.0.0-beta.1`): fold the draft in `docs/RELEASE_NOTES.md` into the new
  file, then delete `docs/RELEASE_NOTES.md` and any link to it.

Show the user the notes and get an OK. The wording is their call.

## Step 4 — Commit, PR, review, merge

1. Show the user the diff summary and this commit message, and wait for approval (stage explicit
   paths, never `git add -A`):

   ```
   Release <display>

   VERSION <VERSION form>, README badge, pubspec version and kAraAppVersion for <display> (YYYY-MM-DD);
   plain-language notes in docs/releases/<display>.md.
   ```

   Beta: add "Fragments kept." Stable: add "CHANGELOG section dated; fragments used up."
   End the message with the session's attribution trailer. Then commit and push the branch.
2. Open the PR into the stable branch: `gh pr create --base stable/X.Y --title "Release <display>"`.
   The body fills `.github/pull_request_template.md`: the two-sentence summary from the notes under
   **What Changed** plus "Notes for the GitHub Release: `docs/releases/<display>.md`"; "No issue
   exists" and what the release is under **Linked Issues or Issue Description**; `None` under
   **Decision records**; the checks this command ran under **Verification**. Check it first:
   `python3 scripts/check-pr-template.py --body-file <file>`.
3. Run `/pr-checker` on the PR for the review loop. A release PR changes no code, so one round is
   normal; the bot's notes on wording are the user's call, not defects. `stable/**` has no merge
   queue and allows only merge commits, so the merge is `gh pr merge <N> --merge`.

## Step 5 — Tag and verify the Release

Tags are annotated and go on the release PR's merge commit on `stable/X.Y`. Show the user the
commands and push only after their OK:

```bash
git fetch origin && git checkout --detach origin/stable/X.Y
python3 scripts/release-tag.py vX.Y.Z[-beta.N]          # version= must equal VERSION
test "$(tr -d '[:space:]' < VERSION)" = "<VERSION form>"
test -f "docs/releases/<display>.md"
git tag -a vX.Y.Z[-beta.N] -m "Release <display>"
git push origin vX.Y.Z[-beta.N]
```

Before the **first beta tag of a branch**, dry-run `release.yml` from the stable branch (it stops
before publishing anything). Dispatch it with `--ref stable/X.Y` only; the dry run has no tag, so it
checks the branch tip:

```bash
gh workflow run release.yml -R open-astro/openastro-ara --ref stable/X.Y -f tag=vX.Y.0-beta.N
sleep 10
gh run watch -R open-astro/openastro-ara "$(gh run list -R open-astro/openastro-ara --workflow=release.yml --event workflow_dispatch --branch stable/X.Y --limit 1 --json databaseId --jq '.[0].databaseId')" --exit-status
```

After the tag push, wait for the `Release` run and check the result:

```bash
gh run watch -R open-astro/openastro-ara "$(gh run list -R open-astro/openastro-ara --workflow=release.yml --limit 1 --json databaseId --jq '.[0].databaseId')" --exit-status
gh release view vX.Y.Z[-beta.N] -R open-astro/openastro-ara --json name,isPrerelease,body,assets \
  --jq '.name, .isPrerelease, (.body | .[0:400]), (.assets[].name)'
```

- The body starts with the plain-language notes, not changelog bullets.
- A beta is a pre-release; a stable or hotfix release is not.
- The assets are exactly `openastro-ara-<display>-source.tar.gz` and its `.sha256`.
- Download both into an empty scratch folder and check them: `shasum -a 256 -c *.sha256` passes,
  and `tar tzf openastro-ara-<display>-source.tar.gz | grep -c '^openastro-ara-<display>/third_party/astap/'`
  is not 0 (the submodules are inside).

If the workflow failed (tag and `VERSION` disagree, missing notes), fix the cause in a new PR into
`stable/X.Y`, then delete and re-push the tag after it merges
(`git tag -d <tag> && git push origin :refs/tags/<tag>`), and check again. If the workflow never
ran, create the Release by hand:

```bash
out="$(mktemp -d)"
scripts/source-archive.sh vX.Y.Z[-beta.N] "$out"
gh release create vX.Y.Z[-beta.N] -R open-astro/openastro-ara --verify-tag [--prerelease] \
  --title "OpenAstro Ara <display>" --notes-file docs/releases/<display>.md \
  "$out/openastro-ara-<display>-source.tar.gz" "$out/openastro-ara-<display>-source.tar.gz.sha256"
```

A beta needs `--prerelease`, or it becomes the Latest release above the current stable.

**Milestones after the tag:** close the milestone named for the tag just pushed
(`gh api -X PATCH repos/open-astro/openastro-ara/milestones/<number> -f state=closed`). There is one
open release milestone at a time: ask the user whether to open the one for the next tag
(`vX.Y.0-beta.(N+1)` or `vX.Y.0`) and move any item still waiting into it.

## Step 6 — Merge down

After every beta tag, the stable tag and every hotfix, merge the stable branch into `master` (and,
for a hotfix while `stable/X.Z` is in beta, a second PR into `stable/X.Z`). Each PR has its own
short-lived head: `merge-down/X.Y-to-master` or `merge-down/X.Y-to-X.Z`, cut from the stable branch.
Never use `stable/X.Y` itself as the head.

First confirm an active ruleset protects `refs/heads/stable/**` with the `deletion` and
`non_fast_forward` rules, or stop and ask the maintainer to add it:

```bash
for id in $(gh api repos/open-astro/openastro-ara/rulesets --jq '.[] | select(.enforcement=="active") | .id'); do
  gh api "repos/open-astro/openastro-ara/rulesets/${id}" \
    --jq '"\(.id) \((.conditions.ref_name.include // []) | join(",")) rules=\([.rules[].type] | join(","))"'
done
```

Build the head. The receiving branch is merged into it, so the version files are settled before the
PR opens:

```bash
SRC=X.Y; TGT=master                   # or TGT=X.Z for a hotfix into stable/X.Z
BASE_BRANCH="$([ "${TGT}" = master ] && echo master || echo "stable/${TGT}")"
HEAD_BRANCH="merge-down/${SRC}-to-${TGT}"
git fetch origin "stable/${SRC}" "${BASE_BRANCH}"
git checkout -B "${HEAD_BRANCH}" "origin/stable/${SRC}"
git merge --no-ff --no-commit "origin/${BASE_BRANCH}" || true   # VERSION, badge, pubspec and kAraAppVersion conflicts are expected
```

**Version files.** The receiving branch keeps its own `VERSION`, README badge line, `pubspec.yaml`
`version:` line and `kAraAppVersion` line. The one exception: a merge into `master` that brings a stable release newer than
`master`'s `VERSION` (the promotion, or a hotfix before the next promotion) takes the stable side's.

```bash
KEEP="origin/${BASE_BRANCH}"
S="$(git show "origin/stable/${SRC}:VERSION" | tr -d '[:space:]')"
M="$(git show origin/master:VERSION | tr -d '[:space:]')"
[ "${TGT}" = master ] && ! python3 scripts/changelog-fragments.py --is-beta "$S" \
  && python3 -c 'import sys; v=lambda s: tuple(int(x) for x in s.split("~")[0].split(".")); sys.exit(0 if v(sys.argv[1]) > v(sys.argv[2]) else 1)' "$S" "$M" \
  && KEEP="origin/stable/${SRC}"
git checkout "${KEEP}" -- VERSION
# Badge line, pubspec version line and kAraAppVersion line from KEEP; each replaces a plain line or a
# whole conflict hunk.
python3 - "${KEEP}" <<'PY'
import re, subprocess, sys
keep = sys.argv[1]
for path, pat in (("README.md", r"#### \[.*"), ("client/openastroara_client/pubspec.yaml", r"version: .*"),
                  ("client/openastroara_client/lib/version/ara_version.dart", r"const String kAraAppVersion = '.*';")):
    want = next(l for l in subprocess.run(["git", "show", f"{keep}:{path}"], capture_output=True, text=True, check=True).stdout.splitlines() if re.fullmatch(pat, l))
    s = open(path).read()
    line = rf"(?:{pat}\n)+"
    hunk = rf"<{{7}} .*\n{line}(?:\|{{7}}.*\n(?:{pat}\n)*)?={{7}}\n{line}>{{7}} .*\n"
    s, n = re.subn(rf"^(?:{hunk}|{pat}\n)", lambda m: want + "\n", s, count=1, flags=re.M)
    assert n == 1, path
    open(path, "w").write(s)
PY
git add VERSION README.md client/openastroara_client/pubspec.yaml client/openastroara_client/lib/version/ara_version.dart
python3 scripts/version.py --check
```

A dated `## [X.Y.Z]` section of `CHANGELOG.md` goes in version order below the receiving branch's
sections. `docs/releases/` notes and code come across as they are. Resolve any other conflict by
hand, then check and push:

```bash
if [ -n "$(git diff --name-only --diff-filter=U)" ]; then
  echo "STOPPED: unresolved paths: $(git diff --name-only --diff-filter=U | tr '\n' ' ')"
elif git diff --cached | grep -qE '^\+(<{7}|>{7})( |$)'; then
  echo "STOPPED: conflict markers in the staged diff"
else
  git commit --no-edit &&
  git push -u origin "${HEAD_BRANCH}" &&
  gh pr create -R open-astro/openastro-ara --base "${BASE_BRANCH}" --head "${HEAD_BRANCH}" \
    --title "Merge stable/${SRC} into ${BASE_BRANCH}" --body-file <filled template>
fi
```

The PR body fills the template (what the tag carried; "No issue exists"; Decision records `None`).
No fragment and no `Falsified by:` list: the `falsified-by` gate exempts a merge down through
`scripts/merge-down.py` when the head name matches and every non-merge commit is already on
`stable/X.Y`. Do not push any other commit to the head, or the PR falls back under the gate.

**Merging.** `master`'s merge queue squashes, and a squash would hide the branch history and make
the next merge down conflict again. So the merge down skips the queue: once the review bot is clean
and CI is green, a maintainer merges it with a merge commit and an admin bypass. Ask the user
first, then:

```bash
gh pr merge <N> -R open-astro/openastro-ara --merge --admin
```

Never squash it, never put it in the queue, never cherry-pick instead. A merge down into
`stable/X.Z` uses `gh pr merge <N> --merge` (no queue there). The head is deleted on merge;
`stable/X.Y` is untouched.

After the final merge down of a promotion, the previous `stable/` branch retires (no more tags).

## Step 7 — Wrap up

- Delete the local `release/<display>` branch (origin deletes the remote copy on merge).
- Report: the version, the release PR, the tag, the Release URL, the source tarball name, the
  merge-down PR(s), and which milestones were closed.
