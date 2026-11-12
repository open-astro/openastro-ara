# Changelog fragments

Every PR that a user would notice adds **one file** here instead of editing
`CHANGELOG.md`. Two PRs can then merge in any order without a conflict, because
no two of them touch the same file. `CHANGELOG.md` is written only by the
release step (`scripts/changelog-fragments.py --release`). The scheme is
AlpacaBridge's (its PR #760), adopted in #1812.

Mechanical PRs (pure renames, pure deletes, doc-only edits to internal design
files) need no fragment. When in doubt, add one.

## File name

`changelog.d/<branch-slug>.md`, where `<branch-slug>` is the branch name after its
last `/` (branch `fix/ara-1751-every-string-in-pt-br` gives
`changelog.d/ara-1751-every-string-in-pt-br.md`). Allowed characters:
`[a-z0-9][a-z0-9._-]*`. One fragment per PR; a later commit on the same PR edits
the same file. Every file in `changelog.d/` other than this README is treated as a
fragment, so keep scratch files elsewhere.

The `unreleased-NNN-*.md` files are the `[Unreleased]` section as it stood when
fragments were adopted, split in its original order.

## Body

One or more `### <Category>` subsections, each with at least one `- ` bullet. A
bullet uses the existing entry style: a bold summary, the area and the issue
(`#N`), then the detail. Write each bullet on one line, however long; do not
wrap it. A nested `- ` bullet, a fenced code block, or a paragraph after a blank
line may follow a bullet; `--check` refuses any other line that continues one. A
fragment has no `#` or `##` heading, no version and no date; the release
supplies them.

Categories, in the order the release writes them:

- `Breaking changes`
- `Added`
- `Changed`
- `Deprecated`
- `Removed`
- `Fixed`
- `Security`

Any of them may be followed by a qualifier in parentheses that names the area,
for example `Added (calibration)` or `Fixed (guiding)`. A qualified category is
written right after its base category. Within a category, entries follow the
fragment file names in sort order.

```markdown
### Fixed (guiding)
- **GoTo stops guiding first** (#1750): what was wrong, what it does now.

### Added (tests)
- **GoTo guiding cases** (#1750): two test cases.
```

## Breaking or not

Ask of every change, fixes included: **could someone who changes nothing but the
version see something that worked on the last release stop working?** A saved
profile or sequence that loaded and now does not, a client that worked against
the last daemon and now gets a refusal or a different answer, a setting or an
endpoint that went away, a step the user has to take after upgrading. If yes,
add a `### Breaking changes` bullet that says what stops working, who is
affected and what to do, in a bold **After upgrading:** sentence. Keep the
`Fixed` or `Changed` bullet that explains the fix.

Ara is pre-1.0 and names each release by hand (`0.0.1-ara.N`), so a Breaking
changes entry does not choose the version; it tells the user.

## Correcting a released entry

An entry belongs to the release its code ships in. Never add one to a dated
section of `CHANGELOG.md`, and never add an `## [Unreleased]` section there:
`--check` refuses it. The one exception is a correction: a PR may edit
`CHANGELOG.md` to fix a wrong released entry, and its description must say so.

## Commands

```bash
python3 scripts/changelog-fragments.py --check     # validate every fragment (CI runs this)
python3 scripts/changelog-fragments.py --preview   # the section the release would write
python3 scripts/changelog-fragments.py --release 0.0.1-ara.1 --date 2026-12-01
```

`--release` writes `## [0.0.1-ara.1] - 2026-12-01` above the most recent release
(above the pre-release history for the first one), collapses the previous release
into a `<details>` block, and deletes the fragments it used. It writes nothing if
a fragment fails `--check`, the version is not `X.Y.Z-ara.N`, the date is not a
real `YYYY-MM-DD`, or the version is already in the changelog.
