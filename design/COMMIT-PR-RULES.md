# COMMIT-PR-RULES.md — DRAFT (to discuss)

Parking-lot doc capturing the commit + PR + CodeRabbit discussion. **Not yet baked into PORT_PLAYBOOK.md.** Picks back up next session.

## The constraint that drove this

CodeRabbit (free tier) limits AI review to **~200 files per PR**. The current §22 plan is "one mega-PR at Phase 15" which is multi-thousand files — won't get CodeRabbit review.

## Proposed solution: per-phase PRs with sub-PR splitting

### Per-phase PR rhythm

```
master                          ← final integration target
  └─ port/ara                   ← integration branch (where phase PRs land)
       ├─ port/ara/phase-0.5/a  ← sub-PR for Phase 0.5 part a
       ├─ port/ara/phase-0.5/b  ← sub-PR for Phase 0.5 part b
       ├─ port/ara/phase-1      ← Phase 1 (single PR fits)
       └─ ...
```

Rhythm:
1. AI checks out `port/ara`, creates `port/ara/phase-N` (or sub-branch)
2. Does the phase's work on the sub-branch (commits + pushes per commit)
3. Completes phase, runs §15 gate
4. Tags `phase-N-complete` on sub-branch
5. Opens PR `port/ara/phase-N → port/ara`
6. CodeRabbit reviews
7. User reviews + merges
8. AI starts next phase from updated `port/ara`

Final integration: `port/ara → master` PR at Phase 15 = a fast-forward over already-reviewed phase PRs.

### Phase size audit

| Phase | File count | Fits in 200? |
|---|---|---|
| **Phase 0.5** | 2000+ | **NO — split required** |
| Phase 1 | ~13 | Yes |
| Phase 2-3 | ~30-50 | Yes |
| Phases 4-9 | ~20-80 each | Yes |
| Phase 10 | ~5-10 | Yes |
| Phase 11 | ~30-50 | Yes |
| **Phase 12** | 100-200 | **Marginal — may need split** |
| Phase 13 | ~10-20 | Yes |
| Phase 14 | ~5-10 | Yes |
| Phase 15 | ~5-10 | Yes |

### Phase 0.5 proposed sub-split (16 sub-PRs)

**Key insight: DELETE before RENAME.** Deleting thousands of files first means renames only touch what's left.

| Sub-PR | Touches | Approx files | CodeRabbit |
|---|---|---|---|
| 0.5a — Delete WPF UI | `NINA/`, `NINA.WPF.Base/`, `NINA.CustomControlLibrary/` | ~400-600 (deletes) | **skip-review** (mechanical) — or split by directory |
| 0.5b — Delete MGEN + nikoncswrapper + WiX + Plugin | Whole projects | ~150-200 | skip-review |
| 0.5c — Delete vendor SDKs (Canon/Nikon/ZWO/QHY/etc.) | Per-vendor SDK folders | ~300-500 | skip-review, possibly split by 2-3 vendor batches |
| 0.5d — Delete ASCOM COM glue | Specific files in Equipment | ~30-50 | **review** (substantive) |
| 0.5e — Delete WebView2 references | Per-project edits | ~10-20 | review |
| 0.5f — Strip Stefan branding + license headers | Locale.resx, URLs, README, NOTICE.md, headers | ~50-100 | **review** (text changes user-visible) |
| 0.5g — Rename `NINA.Core` → `OpenAstroAra.Core` | One project | ~30-50 | skip-review |
| 0.5h — Rename `NINA.Astrometry` | Similar | ~20-40 | skip-review |
| 0.5i — Rename `NINA.Profile` | Similar | ~30-50 | skip-review |
| 0.5j — Rename `NINA.Image` | Similar | ~30-50 | skip-review |
| 0.5k — Rename `NINA.Equipment` | Post-vendor-delete | ~50-100 | skip-review |
| 0.5l — Rename `NINA.Sequencer` | Similar | ~80-150 | skip-review (borderline; may need split) |
| 0.5m — Rename `NINA.PlateSolving` | Similar | ~20-30 | skip-review |
| 0.5n — Rename `NINA.Test` | Similar | ~30-60 | skip-review |
| 0.5o — Rename solution + global identifiers | sln, sln.licenseheader, etc. | ~5-15 | review |
| 0.5p — .NET 10 bump + global.json (might absorb Phase 1) | csproj TFM changes | ~10-15 | **review** (semantic) |

### Skip-review pattern

CodeRabbit honors `@coderabbitai ignore` in PR description, OR path-based config in `.coderabbit.yaml`. Mechanical PRs (renames-only, deletes-only) get marked, reducing review burden from 16 PRs to ~4-5 substantive ones.

Substantive PRs that get full CodeRabbit review:
- 0.5d (ASCOM COM removal)
- 0.5e (WebView2 removal)
- 0.5f (branding + headers)
- 0.5o (solution-level identifiers)
- 0.5p (.NET 10 bump)

That's 5 PRs CodeRabbit cares about for Phase 0.5. Tractable.

### Phase 12 (Flutter views) — possible split

If Phase 12 builds the entire client UI in one swing, it'll likely exceed 200 files. Likely sub-splits:

- 12a — App shell + nav rail + theme + StatusIndicator widget (§25 + §53)
- 12b — Wizard flow (§37)
- 12c — Imaging + Framing Assistant tabs
- 12d — Sequencer tab (§38 editor UI)
- 12e — Sky Atlas tab (§36 + Aladin Lite integration)
- 12f — Image Library + frame viewer (§40)
- 12g — Stats dashboard (§50)
- 12h — Settings (all sub-screens)

8 sub-PRs, each focused, each reviewable.

## Things still to decide

- [ ] Confirm per-phase PR rhythm OR alternative (paid CodeRabbit tier? Group multiple phases per PR?)
- [ ] Confirm sub-PR letter scheme (a/b/c/...) and branch naming
- [ ] Confirm skip-review marker syntax (CodeRabbit-specific)
- [ ] Decide on `.coderabbit.yaml` config (path exclusions for known-mechanical changes)
- [ ] Decide on `develop` branch (between `port/ara` and `master`) — yes or no?
- [ ] Should AI auto-continue to next phase after PR merge, OR wait for explicit user nudge?
- [ ] What happens if CodeRabbit flags a real issue mid-port — does AI fix in a follow-up PR or amend the open PR?
- [ ] Push cadence — push after every commit (current expectation) vs batched

## Once decided, baking targets

When we finalize, these playbook sections need edits:

- **§0 rule 9** — tag every phase boundary + open PR (replace "never stop" with "wait for merge")
- **§3 phase plan** — add sub-PR rows for Phase 0.5 (and Phase 12 if confirmed)
- **§19.1 git safety** — allow `port/ara/phase-*` sub-branches; main `port/ara` becomes integration-only
- **§22 final pass** — final PR becomes `port/ara → master` (already specced as such; just confirm)

## Open question for tomorrow

Is per-phase PR-rhythm acceptable given the merge ceremony it adds (16-20 PRs total across the port)? Or would you rather:
- Use a paid CodeRabbit tier to handle bigger PRs
- Skip CodeRabbit for mechanical changes and accept single mega-PRs for review
- Some hybrid

Sleep on it. Continue tomorrow.
