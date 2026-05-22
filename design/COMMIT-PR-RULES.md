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

## Settings-registry gate (BAKED — applies to port AND community)

**Status: settled rule. Not parked. Applies immediately starting Phase 12 of the port and all community contributions thereafter.**

### The rule

**Every new user-facing setting MUST be added to `client/openastroara_client/lib/settings/registry.dart` in the same commit that introduces the setting. Commits and PRs that fail this check are BLOCKED — not warned, blocked.**

Per §0.5 (design principles, pillar 3) and §61.4 (PR review rule): *a setting that isn't searchable doesn't merge.* NINA's single worst UX failure is settings sprawl with no search. ARA refuses to ship that, and we enforce it mechanically rather than relying on reviewer vigilance.

### What counts as a "user-facing setting"

Any of the following triggers the gate:

- A new Flutter widget in `lib/screens/settings/**` that takes a `value` + `onChanged` pattern (toggles, sliders, dropdowns, text inputs, color pickers, etc.)
- A new field in the profile JSON schema (server-side) that is editable from WILMA
- A new key written to `flutter_secure_storage` or `shared_preferences` that holds a user-meaningful preference (excludes session-state caches, tokens, transient UI state)
- A new control surface in the wizard (§37) that captures a value persisted to profile

Excluded from the gate (don't need registry entries):

- Internal cache values, performance tuning constants, debug-only flags
- Display-only widgets (read-only status indicators, current-value labels with no edit affordance)
- Modal-local form state that doesn't persist

When in doubt: register it. The cost of an extra registry entry is zero; the cost of an undiscoverable setting is a regression against §0.5.

### Enforcement — defense in depth

Four layers, each independently gating:

**Layer 1 — Local pre-commit hook** (`.husky/pre-commit` or `lefthook.yml`, run by every developer including the AI):

```bash
# .husky/pre-commit (or equivalent)
node scripts/check-settings-registry.mjs --staged
```

The check script parses `registry.dart`, scans the diff for new settings widgets, fails the commit if any uncovered settings are detected. Output names the specific files + widgets that need registry entries.

**No bypass.** `--no-verify` is already prohibited by §19.1 git safety; this rule extends that to "you cannot ship a setting without registering it."

**Layer 2 — CI check on every PR** (GitHub Actions, `.github/workflows/ci.yml`):

Same `check-settings-registry.mjs` runs in CI against the PR diff. A failing check blocks the PR's required-check status; PR cannot merge until green.

**Layer 3 — PR template checkbox** (`.github/PULL_REQUEST_TEMPLATE.md`):

```markdown
## Settings registry (mandatory checkbox if applicable)

- [ ] This PR adds NO new user-facing settings (skip remaining boxes)
- [ ] This PR adds new user-facing settings AND all are registered in `lib/settings/registry.dart` with `id` + `label` + `description` + `keywords` + `path` + `type`
- [ ] Registry entries include cross-links via `relatedSettings` where applicable
- [ ] Search verified: each new setting is findable by typing common keywords in WILMA's ⌘K search
```

Manual checkbox is the third layer — CI catches what humans miss; PR template catches what CI misses (e.g., a setting registered with empty keywords would pass CI but fail user discoverability).

**Layer 4 — CodeRabbit review focus**:

`.coderabbit.yaml` includes a path-based instruction: any PR that touches `lib/screens/settings/**` OR `lib/wizard/**` OR profile-schema files MUST have CodeRabbit review the registry diff. CodeRabbit instructed to flag settings without meaningful descriptions / keywords (not just structurally-present entries).

### Detection script behavior (`check-settings-registry.mjs`)

Parses two sources:

1. **Registry source of truth** — `client/openastroara_client/lib/settings/registry.dart`. Extracts all `Setting(id: '...', label: '...', ...)` entries via simple AST or regex parse.
2. **Settings UI source** — `client/openastroara_client/lib/screens/settings/**/*.dart` + `lib/wizard/**/*.dart`. Scans for setting widget patterns.

Detection rules:

| Detection | Action |
|---|---|
| New settings widget without a registry entry | **FAIL** — print widget location + suggested registry skeleton |
| Registry entry without any corresponding UI usage (stale entry) | **WARN** (not fail — may be a future setting being staged) |
| Registry entry with empty `description` OR empty `keywords` list | **FAIL** — discoverability requires both |
| Registry entry's `path` doesn't match any actual Settings panel hierarchy | **FAIL** — broken deep-link |
| Duplicate `id` across registry entries | **FAIL** — registry IDs must be unique |

The script exits non-zero on any FAIL. CI / pre-commit hook surfaces the output verbatim.

### Suggested registry skeleton output

When the hook detects a missing registration, it prints a copy-pasteable skeleton:

```
✗ FAIL: 1 new setting widget found without registry entry

  File: lib/screens/settings/guider/dither_settings.dart:47
  Widget: Slider with profilePath 'guider.new_dither_widget_pixels'

  Add this to lib/settings/registry.dart:

    Setting(
      id: 'guider.new_dither_widget_pixels',
      label: 'TODO: short user-facing label',
      description: 'TODO: 1-2 sentence explanation of what this controls and why a user would change it',
      keywords: ['TODO', 'add', 'searchable', 'terms'],
      path: ['Settings', 'Guider', 'PHD2'],  // verify this matches the actual panel
      type: SettingType.intRange(min: 0, max: 50),
      defaultValue: 5,
      profilePath: 'guider.new_dither_widget_pixels',
      relatedSettings: [],
    ),
```

Lowers the friction of compliance to "copy, fill in 4 fields, commit again."

### Coverage during the port itself

This rule activates at **Phase 12 of the port** (Flutter views, when Settings UI starts being built). For earlier phases:

- Phase 0.5 — no Settings UI yet; rule does not apply
- Phases 1-10 — server-side only; profile schema changes touched here MUST be reflected in the registry by the time Phase 12 ships, but no per-commit gate yet
- Phase 11 — first-run + handshake only; no settings panels
- **Phase 12 — gate is live.** Pre-commit hook and CI check must pass on every Phase 12 sub-PR (12a-12h).

Phase 12 sub-PR 12h (Settings) is the natural home for the registry's initial bulk-population — every setting touched by earlier phases lands in the registry as part of 12h. After 12h, the gate becomes the steady-state enforcement for all future work.

### Documentation

`CONTRIBUTING.md` (created post-v0.0.1, see future-scope section below) reproduces this rule prominently. Until then, the port playbook §0.5 + §61.4 + this document are the canonical references.

### Applies to

- The AI during Phase 12 + Phase 12 sub-PRs of the port
- All community contributor PRs post-v0.0.1
- Maintainer commits (no special privilege; the rule is uniform)
- Same rule, same enforcement, same gate

There is intentionally no opt-out mechanism. The gate exists because reviewer vigilance is unreliable; mechanical enforcement is the only way to guarantee §0.5 compliance over a project lifetime.

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

---

## Status: parked — coming back

**Status as of 2026-05-21:** the per-phase PR rhythm + sub-PR splitting design above is **approved in principle** by user. Not yet baked into `PORT_PLAYBOOK.md` because we still need to decide the "Things still to decide" list above. Picking back up in a future session.

Approval covers:
- Per-phase PR rhythm (16-20 PRs across the port)
- Phase 0.5 sub-split into 16 sub-PRs with delete-before-rename ordering
- Phase 12 sub-split into 8 sub-PRs (12a–12h)
- CodeRabbit skip-review markers on mechanical PRs
- Final integration: `port/ara → master` PR at Phase 15

---

## Future scope — extend to community contributor workflow + Claude Code skills

Beyond the AI-driven port, this document needs a v2 pass covering **post-v0.0.1 community contributions** — when external users start adding features, fixing bugs, and shipping improvements. The same CodeRabbit-aware PR-splitting discipline should apply.

Open items for that v2 pass:

- [ ] **Community contributor PR template** — `.github/PULL_REQUEST_TEMPLATE.md` enforcing: linked issue, scope statement, CodeRabbit-fittable size, settings-registry-updated checkbox (per §61.4), tests-added checkbox, screenshot/video for UI changes
- [ ] **Branch naming convention for community PRs** — `feature/<short-name>`, `fix/<short-name>`, `chore/<short-name>`. Mirrors port's `port/ara/phase-N/X` pattern but namespaced differently to keep histories clean.
- [ ] **Auto-split heuristics** — if a community PR exceeds CodeRabbit's 200-file limit, what guidance do we give? "Split before submitting" via labelled `needs-split` workflow, or accept and use paid tier?
- [ ] **Claude Code skill recommendations** for community contributors — the available Claude Code skills (`code-review`, `security-review`, `fewer-permission-prompts`, `verify`, `update-config`, `init`, etc.) should be documented in `CONTRIBUTING.md` as recommended workflow:
  - Before opening a PR, run `/security-review` on the diff
  - For PRs touching UI, use `/verify` to confirm the change works in the actual app
  - For PRs touching the settings registry, run `/code-review` with focus on §61.4 compliance
  - For PRs adding new sections to design docs, follow the existing playbook section style
- [ ] **Pre-commit hooks for contributors** — beyond the already-baked settings-registry gate (see "Settings-registry gate" section above — settled, applies to community too): additional lint rules enforcing no `--no-verify`, no force pushes to main/master, license-header presence on new C# files
- [ ] **`.coderabbit.yaml`** — path-based skip-review config that mechanical changes can opt into via PR labels or file-path patterns. Carry from port phase into community phase.
- [ ] **Issue templates** (`.github/ISSUE_TEMPLATE/`) — bug-report (auto-filled with §54 bug-report-submission zip), feature-request (mapped to §55 roadmap tiers), driver-quirk-report (auto-routed upstream per §52.5)
- [ ] **Release cadence post-v0.0.1** — semver discipline, RELEASE_NOTES.md entry per release, GitHub Releases pipeline (already in §14.3 CI), how community PRs feed into next-release vs current-release branches
- [ ] **Maintainer workflow** — who reviews, merge criteria, how long PRs sit before stale-bot pings, etc. Light-touch at first (small project); formalize as community grows.

The unifying theme: **community contributors should follow the same discipline the AI follows during the port.** Same PR sizes, same review markers, same registry requirements, same hooks. That keeps `master` clean across the lifetime of the project, not just through Phase 15.

This v2 pass also extends the `design/` directory — likely adds `CONTRIBUTING.md` (at repo root, not under `design/`), `MAINTAINING.md`, and Claude Code skill recipes under `design/skills/` or similar.

**Status:** captured here for future session. Not in scope for the port itself — comes after v0.0.1 ships.
