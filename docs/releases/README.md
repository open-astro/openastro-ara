# Release notes

One file per release: `docs/releases/<semver>.md`, named with the spelling the
app shows (`1.0.0-beta.1.md`, `1.0.0.md`, `1.0.1.md`). `release.yml` uses the
file as the body of the GitHub Release for that tag, with a link to the
`CHANGELOG.md` section added. The file must be on the tagged commit.
`/bump-release` writes it; [RELEASES.md](../RELEASES.md) has the process.

`CHANGELOG.md` is the technical record. These notes are for people.

## Who reads it

An amateur astronomer deciding whether to build this release tonight. They know
their gear and the app. They do not know the code.

- No issue or PR numbers. No class, method, file, endpoint or flag names. No
  section numbers from design docs.
- Say what the person sees and what to do, not how it was fixed.
- Short sentences. Bold the first words of each bullet. No em dashes (use a
  comma or a full stop). No heading deeper than `##`.
- Leave out internal work (CI, tests, review tooling, refactors), or sum it up
  in one closing line under the fixes.

## Shape

Skip any section that would be empty, and keep this order.

1. `# OpenAstro Ara X.Y.Z` (or `X.Y.Z-beta.N`), then two sentences on what the
   release is about. A beta's first line says it is a beta, for people who want
   to try the next release early and report problems, and that it belongs only on
   a rig where a problem tonight is acceptable.
2. `## New since X.Y.Z-beta.(N-1)` (second and later betas only): just the
   changes since the previous beta, for testers already on it. Find them with
   `git diff --name-only vX.Y.Z-beta.(N-1) -- changelog.d/`. The rest of the
   file is the full picture since the last stable release, restated.
3. `## Read this first`: every Breaking changes entry. For each: what changed,
   what the person must do, and where in the app. Required for a major release.
4. `## New things you can do`: new features, grouped by area (planning, focus,
   polar alignment, guiding, imaging, Simple Mode, the phone app).
5. `## Gear`: newly supported devices or device behaviour, by brand.
6. `## Things that just work better`: improvements a person will notice.
7. `## Fixes worth knowing about`: bugs a person could have hit, one line each.
8. `## Building this release`: the commands below, with the real version filled
   in, plus any step the breaking changes need after the upgrade.

## Building this release (template)

```markdown
## Building this release

This release is source only. Download `openastro-ara-X.Y.Z-source.tar.gz` and
its `.sha256` from this release's Assets, or clone the tag:

    git clone --branch vX.Y.Z --recurse-submodules https://github.com/open-astro/openastro-ara.git

Do not use GitHub's "Source code" links: they leave out the parts the build
needs. Then follow docs/RUNNING.md. Build the app and the daemon from this same
release; the app warns you when their versions do not match.
```

Settings and saved data on the rig survive a rebuild. Say so if a release
changes that.

## Getting the text

Start from the changelog, then translate it with the rules above:

```bash
# beta: the fragments are not used up, so preview them
python3 scripts/changelog-fragments.py --preview --version X.Y.Z~betaN
# stable: after --release has written the dated section
python3 scripts/changelog-section.py X.Y.Z
```

The wording is the maintainer's call. Show them the draft before the release PR.
