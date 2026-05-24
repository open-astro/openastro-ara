# COMMIT-PR-RULES.md

Authoritative reference for the per-phase + sub-PR rhythm, branch naming, CodeRabbit poll-and-fix loop, and pre-PR gate used by the AI during the port. Read together with `design/PORT_PLAYBOOK.md` — which links here from §0 rule 9 (phase-boundary PR rhythm), §3 (phase plan + sub-PR mapping), §19.1 (git safety + branch allowlist), and §22 (final pass `port/ara → master`). Design phase is complete (see "Status" below); Phase 0.5 execution follows the rhythm specified here.

## The constraint that drove this

CodeRabbit (free tier) limits AI review to **~200 files per PR**. The current §22 plan is "one mega-PR at Phase 15" which is multi-thousand files — won't get CodeRabbit review.

## Proposed solution: per-phase PRs with sub-PR splitting

### Per-phase PR rhythm

```
master                  ← final integration target
  └─ port/ara           ← integration branch (where phase PRs land)
       ├─ phase-0.5a    ← sub-PR for Phase 0.5 part a
       ├─ phase-0.5b    ← sub-PR for Phase 0.5 part b
       ├─ phase-1       ← Phase 1 (single PR fits)
       └─ ...
```

**Git ref naming constraint:** sub-branches use **flat names** (`phase-0.5a`, `phase-1`, `prep-ci`), not nested under `port/ara/...`. Git refs are tree-structured — `port/ara` existing as a branch makes `port/ara/anything` an invalid ref name (`fatal: cannot lock ref ...: 'refs/heads/port/ara' exists; cannot create 'refs/heads/port/ara/...'`). The integration branch keeps the `port/ara` name (per PORT_PLAYBOOK.md §1); sub-branches sit alongside it at the top level.

Rhythm:
1. AI checks out `port/ara`, creates `phase-N` (or sub-branch like `phase-0.5a`, `prep-ci`)
2. Does the phase's work on the sub-branch (commits + pushes per commit)
3. Completes phase, runs §15 gate
4. Tags `phase-N-complete` on sub-branch
5. Opens PR `phase-N → port/ara`
6. CodeRabbit reviews; AI poll-and-fix loop runs (60 s polling; auto-fix trivial + correctness findings via new commits; reasoned replies for disagreements; out-of-scope items → `design/PORT_TODO.md`). **If CodeRabbit returns rate-limit / no-credits, AI does NOT merge** — posts `Held for CodeRabbit @<user> — rate-limited, refill in <X>`, then either waits for the auto-refill window and retriggers via `@coderabbitai review`, or waits for user direction (e.g., billing fix).
7. **AI merges** once the §19.1 merge-gate clears (green CI + **actual CodeRabbit review** posted, not a rate-limit skip + no unresolved actionable findings + clean self-review against scope). Use `gh pr merge --squash --delete-branch` to remove the sub-branch from origin in the same step. If any gate condition is ambiguous, AI posts `Held for human review @<user> — <reason>` and waits instead.
8. AI pulls updated `port/ara`. **If the just-merged sub-PR was the last in a phase** (e.g., `phase-0.5a-6` closes out Phase 0.5a), AI also tags `phase-N-complete` on `port/ara` HEAD and opens a `port/ara → master` promotion PR per PORT_PLAYBOOK.md §22.0 cadence. Then starts the next phase from updated `port/ara` once the promotion PR merges.

Final integration: per §22.0 (revised 2026-05-23), `port/ara → master` is **periodic**, not one-shot — promoted after each phase boundary. The Phase 15 final pass catches whatever tail-end work hasn't been promoted yet. Same §19.1 merge-gate; merge commit (not squash) preserves per-phase history on `master`.

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

| Sub-PR | Touches | Approx files |
|---|---|---|
| 0.5a — Delete WPF UI | `NINA/`, `NINA.WPF.Base/`, `NINA.CustomControlLibrary/` | ~400-600 (deletes) — may sub-split by directory if any single PR exceeds 200 |
| 0.5b — Delete MGEN + nikoncswrapper + WiX + Plugin | Whole projects | ~150-200 |
| 0.5c — Delete vendor SDKs (Canon/Nikon/ZWO/QHY/etc.) | Per-vendor SDK folders | ~300-500 — likely sub-split into 2-3 vendor batches |
| 0.5d — Delete ASCOM COM glue | Specific files in Equipment | ~30-50 |
| 0.5e — Delete WebView2 references | Per-project edits | ~10-20 |
| 0.5f — Strip Stefan branding + license headers | Locale.resx, URLs, README, NOTICE.md, headers | ~50-100 |
| 0.5g — Rename `NINA.Core` → `OpenAstroAra.Core` | One project | ~30-50 |
| 0.5h — Rename `NINA.Astrometry` | Similar | ~20-40 |
| 0.5i — Rename `NINA.Profile` | Similar | ~30-50 |
| 0.5j — Rename `NINA.Image` | Similar | ~30-50 |
| 0.5k — Rename `NINA.Equipment` | Post-vendor-delete | ~50-100 |
| 0.5l — Rename `NINA.Sequencer` | Similar | ~80-150 — may sub-split if over 200 |
| 0.5m — Rename `NINA.PlateSolving` | Similar | ~20-30 |
| 0.5n — Rename `NINA.Test` | Similar | ~30-60 |
| 0.5o — Rename solution + global identifiers | sln, sln.licenseheader, etc. | ~5-15 |
| 0.5p — .NET 10 bump + global.json (might absorb Phase 1) | csproj TFM changes | ~10-15 |

### `.coderabbit.yaml` minimal config (DECIDED 2026-05-23)

Committed at repo root. Excludes only truly-generated code + temp files; focuses detailed review on settings files (per Layer 4 of the Settings-registry gate below).

```yaml
# .coderabbit.yaml
# Lives at repo root.
# Policy: review every PR fully (no skip markers).
# This config narrows what gets reviewed (excludes generated code) and
# focuses extra attention on settings files.

reviews:
  profile: chill           # default review depth — adjust if too noisy
  request_changes_workflow: false   # don't auto-block merges; user decides

  auto_review:
    enabled: true
    base_branches:
      - master
      - port/ara           # without this, sub-PRs to port/ara are skipped
                           # (CodeRabbit defaults to default-branch only)

  path_filters:
    # Exclude generated code (don't review machine output)
    - "!client/openastroara_client/lib/api/generated/**"
    - "!**/*.g.dart"
    - "!**/*.freezed.dart"
    - "!**/*.mocks.dart"
    # Exclude temp + backup files (shouldn't be committed anyway)
    - "!**/*.bak"
    - "!**/*.tmp"
    - "!/tmp/**"
    # Exclude built artifacts
    - "!**/bin/**"
    - "!**/obj/**"
    - "!**/build/**"
    # Everything else: full review

  path_instructions:
    # Focused review on settings files per Layer 4 of the registry gate
    - path: "client/openastroara_client/lib/settings/registry.dart"
      instructions: |
        Verify every Setting entry has a meaningful description and at least 3 keywords.
        Flag any entry where keywords look auto-generated or trivially derived from the id.
        Flag any entry whose path[] doesn't match an actual Settings panel hierarchy.
    - path: "client/openastroara_client/lib/screens/settings/**"
      instructions: |
        For every new setting widget (toggle, slider, dropdown, text input, color picker),
        verify a corresponding entry exists in lib/settings/registry.dart with non-empty
        description + keywords. Flag any widget that adds user-facing state without a
        registry entry — this should also be caught by check-settings-registry.mjs but
        catch any miss here.
    - path: "client/openastroara_client/lib/wizard/**"
      instructions: |
        Wizard screens often introduce settings — verify any persisted value is registered.
```

### CodeRabbit review policy (DECIDED 2026-05-23): every PR gets reviewed

**No skip-review markers.** Every Phase 0.5 sub-PR — mechanical or substantive, deletes or renames or branding — goes through full CodeRabbit review before merge. Rationale: mechanical PRs can hide real issues (wrong files renamed, missed exclusions, broken references after a rename) that only a careful pass catches. Belt-and-suspenders is worth the review-burden cost for a port of this size.

This means:
- The AI does NOT add `@coderabbitai ignore` to any PR description
- The AI does NOT add `coderabbit-skip` labels (we don't use that workflow)
- All 16+ Phase 0.5 sub-PRs + all 8 Phase 12 sub-PRs + every other phase PR get CodeRabbit reviews
- AI follows the standard CodeRabbit poll-and-fix loop (see "CodeRabbit review loop" section below) on every PR — no fast-path for any PR type

If review burden becomes truly unmanageable on certain PR types in practice, the policy can be revisited mid-port — but the default is **review everything**.

### Phase 12 (Flutter views) — DECIDED 8-PR split (2026-05-23)

**Status: settled.** 8 sub-PRs (12a–12h). Each stays under CodeRabbit's 200-file free-tier limit. Order: 12a → 12b → 12c–12g (independent feature tabs, any order) → 12h last (consolidates §61 search registry across all settings panels).

| Sub-PR | Scope | Approx file count |
|---|---|---|
| **12a** App shell + global infrastructure | §25 visual design (NINA-style layout, nav rail, dark theme), §53 a11y + StatusIndicator widget, §41 mobile companion conditional shell, §30 first-run + saved-server management, §30.7 banner shell (shared by equipment-change + sky-data-missing variants), §32 disconnect modal, §35 emergency stop + alarm modal (§35.5), §46 notification feed (global), §54 bug report flow entry point (Help) | ~120 |
| **12b** Wizard (§37) | All 18 screens / 7 stages, mandatory profile-creation policy (§37 preamble + §30.4), §37.6 sky data downloads integration with §36.12 | ~80 |
| **12c** Imaging + Framing tabs | Main image viewer, exposure controls, histogram, plate-solve overlay, §51 Health Indicator (always visible) + Diagnostic Panel, §64 Live View / loop imaging UI, §45 polar alignment iPolar-style continuous loop UI, §47 mosaic Aladin panel-overlay preview | ~150 |
| **12d** Sequencer tab (§38) | Tree-based instruction editor, conditional logic UI, template instantiation, Resume Target seeding (§40.6), NINA import flow (§38.4), §42 hardware fault recovery surface | ~140 |
| **12e** Sky Atlas tab + Data Manager | §36 Aladin Lite integration, §36.7 Tonight's Sky, §36.8 universal search, §36.9 comet support, **§36.2 Data Manager 4-tab UI** (Sky Imagery / Star Catalogs / Target Thumbnails / Solar System), §36.13 sky-data-missing banner integration | ~130 |
| **12f** Image Library + frame viewer | §40 by-session organization, §40.5 frame viewer + §65 stretch picker + manual sliders, §40.6 Resume Target workflow, §40.7 auto-rating + HFR drift display, §40.8 bulk operations, §43 backup UI, §44 real-time backup stream UI | ~120 |
| **12g** Stats dashboard (§50) | Overview tiles, Targets rollup + per-target detail, Focus & Temperature scatter + regression, Guiding RMS trends, Frame Quality + composite score, Best Frames auto-sort, Calendar heatmap, CSV export | ~100 |
| **12h** Settings + smart search | All settings sub-screens (Equipment, Imaging, Plate Solving, Safety, Diagnostics, Storage, Sky Atlas, Notifications, Profile, PHD2), §29.1.3 ext4 reformat UX, §35 safety policies editor, §51 diagnostics mode picker (notify_only default), §63 PHD2 settings, **§61 smart settings search (⌘K)** cross-cutting all panels | ~150 |

**Data Manager placement decision (2026-05-23):** lives in 12e Sky Atlas, not 12h Settings, because the AI doing the survey-list UI naturally wires the downloader at the same time (cohesion with HiPS surveys + Aladin integration). Settings panel mounts a "Open Data Manager" link only.

**Sub-PRs that exceed 200 files mid-port:** any sub-PR that turns out larger than estimated gets ad-hoc sub-split before opening (e.g., 12d → 12d.1 editor + 12d.2 template instantiation if it bloats). Decision made at PR-prep time, not pre-planned.

### CodeRabbit review loop (AI-driven, 60-second polling)

**Pre-PR gate (always runs before opening the PR):** AI invokes `scripts/pre-pr-check.sh` per playbook §14.4. Exits non-zero on any failure (build error, test failure, format issue, settings-registry miss, etc.). AI fixes failures + re-runs the script + opens the PR only when green. For PRs touching user-visible Flutter UI, AI also captures screenshots per §14.6 and attaches them to the PR description.

After the AI opens any PR (Phase 0.5 sub-PRs, Phase 12 sub-PRs, or any other phase):

1. **AI opens the PR** (with green pre-PR gate + screenshots if applicable), waits for CodeRabbit's initial review pass (~2–10 minutes depending on PR size + queue depth)
2. **AI polls every 60 seconds** for new CodeRabbit comments via `gh api repos/<owner>/<repo>/pulls/<pr>/comments` (and the issue-comments endpoint for top-level review summaries)
3. **AI processes each finding:**
   | Finding type | AI action |
   |---|---|
   | Trivial / nit (formatting, naming, minor clarity) | Fix immediately, push commit, post reply: *"Fixed in `<sha>`"* |
   | Real bug or correctness issue | Fix immediately, push commit, post reply: *"Addressed in `<sha>`"* |
   | Disagreement (CodeRabbit suggests something wrong, contradicts the playbook spec, or is beyond PR scope) | Post reply with reasoning + link to the relevant playbook section; do not change code |
   | Out-of-scope suggestion (broader refactor, future feature) | Append entry to `design/PORT_TODO.md` with PR reference; reply: *"Acknowledged — tracked in design/PORT_TODO.md for follow-up"* |
4. **CodeRabbit re-reviews after each fix push.** AI handles round-2 findings the same way. If the same issue ping-pongs more than twice, AI defers: posts *"Deferring this to human review — see comments above"* and stops auto-fixing that thread
5. **Quiescence detection:** when 3 consecutive polls (= 3 minutes idle) return no new comments, AI posts *"Ready for human review @<user>"* on the PR and stops polling
6. **User reviews + merges** at their convenience. AI does not merge anything, ever (this rule is permanent — see §19.1 git safety in the playbook)
7. **After user merge**, AI pulls the updated integration branch (`port/ara`) and starts the next sub-PR from there

**Parallel work:** while waiting for CodeRabbit on an open PR, the AI may start drafting the next sub-PR's work in the same session. Polling for the open PR continues in the background via the harness's natural notification flow — no busy-waiting needed.

**Failure modes:**
- **CodeRabbit doesn't respond within 30 minutes** → AI posts *"CodeRabbit hasn't responded in 30 min; flagging for manual review"* and stops polling; user investigates (rate-limit, queue, etc.)
- **Push hooks fail during a fix commit** → AI does NOT use `--no-verify`; reports the failure to the user via PR comment + waits for guidance
- **Repeated CI failure on a fix** → after 2 failed fix attempts on the same finding, AI defers to user

This loop applies to all PRs the AI opens, not just Phase 12 sub-PRs. Phase 0.5 sub-PRs, individual phase PRs, and any follow-up PRs all use the same workflow.

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

## Help-registry gate (BAKED — parallel to settings-registry gate)

**Status: settled rule. Applies immediately starting Phase 12 alongside the settings-registry gate.**

The help registry (`client/openastroara_client/lib/help/registry.dart`, specced in `PORT_PLAYBOOK.md` §69) follows the **same enforcement model** as the settings registry: pre-commit hook + CI check + PR template checkbox + CodeRabbit path instructions. Mechanism is fully described in §69.4; this section just affirms it applies to community contributions identically to maintainer commits, with no opt-out.

Detection script: `scripts/check-help-registry.mjs --staged` (parallel to `check-settings-registry.mjs`). Same activation timing (Phase 12 onward), same blocking behavior on fail, same per-PR template checkboxes.

The two gates are independent — a PR may fail one and pass the other; both must pass to merge.

Settings registry covers *findability* of controls; help registry covers *explainability* of controls. Together they enforce §0.5's discoverability pillar mechanically rather than relying on reviewer vigilance.

### Applies to

- The AI during Phase 12 + Phase 12 sub-PRs of the port
- All community contributor PRs post-v0.0.1
- Maintainer commits (no special privilege; the rule is uniform)
- Same rule, same enforcement, same gate

There is intentionally no opt-out mechanism. The gate exists because reviewer vigilance is unreliable; mechanical enforcement is the only way to guarantee §0.5 compliance over a project lifetime.

## Things still to decide

- [x] ~~Confirm per-phase PR rhythm OR alternative~~ → **Decided 2026-05-23**: per-phase + sub-PR splitting, free CodeRabbit tier, no paid escalation
- [x] ~~Confirm sub-PR letter scheme (a/b/c/...) and branch naming~~ → **Decided 2026-05-23**: letter scheme `a-h`. Branch naming originally specified as `port/ara/phase-N/<letter>`; revised in prep-ci PR to flat names (`phase-Na`, e.g., `phase-12e`) after discovering Git refs forbid `port/ara/...` while `port/ara` itself is a branch.
- [x] ~~Confirm skip-review marker syntax~~ → **Decided 2026-05-23**: **no skip-review markers; every PR gets reviewed**. Belt-and-suspenders catches subtle issues that mechanical PRs can hide. See "CodeRabbit review policy" section above.
- [x] ~~Decide on `.coderabbit.yaml` config~~ → **Decided 2026-05-23**: minimal exclusions only. Exclude truly-generated code (`client/openastroara_client/lib/api/generated/**`, `**/*.g.dart`) + temp/backup files (`*.bak`, `*.tmp`). Enable detailed-review focus on settings files (`lib/settings/registry.dart`, `lib/screens/settings/**`, profile-schema files) per Layer 4 of the Settings-registry gate above. Nothing else excluded — every other path goes through full review per the policy above.
- [x] ~~Decide on `develop` branch~~ → **Decided 2026-05-23**: **no**. The integration branch `port/ara` already plays that role; adding `develop` adds a merge step without benefit for a single-developer port. Final phase-15 PR goes `port/ara → master` directly.
- [x] ~~Should AI auto-continue to next phase after PR merge~~ → **Decided 2026-05-23**: AI pulls updated `port/ara` after user merges, then auto-starts the next sub-PR (no human nudge needed between sub-PRs within the same phase). Between phases (e.g., after Phase 12 fully merges and Phase 13 begins), AI auto-starts unless user has paused
- [x] ~~What happens if CodeRabbit flags a real issue mid-port~~ → **Decided 2026-05-23**: AI fixes via additional commits on the same sub-branch (not a follow-up PR; not an amend that destroys history). See "CodeRabbit review loop" section above for the full pattern
- [x] ~~Push cadence — push after every commit vs batched~~ → **Decided 2026-05-23**: push after every commit (visibility for user + enables CodeRabbit incremental review)

## Once decided, baking targets

When we finalize, these playbook sections need edits:

- **§0 rule 9** — tag every phase boundary + open PR (replace "never stop" with "wait for merge")
- **§3 phase plan** — add sub-PR rows for Phase 0.5 (and Phase 12 if confirmed)
- **§19.1 git safety** — allow flat-named `phase-*` sub-branches (e.g., `phase-0.5a`); main `port/ara` becomes integration-only
- **§22 final pass** — final PR becomes `port/ara → master` (already specced as such; just confirm)

---

## Status: design phase complete (2026-05-23)

**Decisions made 2026-05-23:**
- Phase 12 split shape confirmed (8 sub-PRs, 12a–12h, augmented mapping with all session decisions integrated; see table above)
- Sub-PR letter scheme + branch naming confirmed (originally `port/ara/phase-N/<letter>`; revised in `prep-ci` PR to flat `phase-N<letter>` due to a Git ref constraint — see per-phase rhythm section)
- AI auto-continues to next sub-PR after user merge (no nudge between sub-PRs)
- CodeRabbit poll-and-fix workflow specced (60 s polling, AI handles trivial + correctness findings, user handles merge)
- Push cadence: after every commit

**Decisions made 2026-05-23 (continued):**
- **No skip-review markers** — every PR gets full CodeRabbit review, including mechanical Phase 0.5 deletes/renames (belt-and-suspenders for subtle issues)
- `.coderabbit.yaml` minimal config (excludes generated code + temp files only; detailed-review focus on settings files)
- **No `develop` branch** — sub-PRs land on `port/ara` directly; Phase 15 final PR `port/ara → master`

**Baked into `PORT_PLAYBOOK.md` (2026-05-23):**
- ✅ §0 rule 9 — "Tag every phase boundary; open the PR; merge it; continue." References this doc for the full rhythm + CodeRabbit loop. **Policy revised 2026-05-23** (later same day, in `prep-ci` PR #2): AI merges sub-PR + final PRs under the §19.1 merge-gate; auto-continues to next sub-PR after merge. Original 2026-05-23 decision was "AI never merges, ever"; reversed when the user granted full merge authority.
- ✅ §3 phase plan — Phase 0.5 sub-PR rhythm references the 16-sub-PR mapping (0.5a–0.5p); Phase 12 references the 8-sub-PR mapping (12a–12h); cross-cutting sub-PR rhythm paragraph added after the phase list.
- ✅ §19.1 git safety — branch allowlist now permits `port/ara` (integration) + flat-named `phase-N[<letter>]` sub-PR feature branches (e.g., `phase-0.5a`, `phase-12h`) plus a small set of named prep branches (e.g., `prep-ci`). **Merge policy revised 2026-05-23** in `prep-ci` PR #2: original "AI never merges, ever" rule replaced with "AI merges under a strict merge-gate" (green CI + CodeRabbit quiescent ≥3 min + no unresolved findings + clean self-review). User retains override. Tag scheme extended to `phase-N-<letter>-complete` for sub-PRs.
- ✅ §22 final pass — final PR confirmed `port/ara → master` (no `develop` branch); release notes step updated to use `CHANGELOG.md` per §33.7 + Keep-a-Changelog format + fresh `[Unreleased]` placeholder; mobile-deferred-to-v0.1.0 noted in CHANGELOG sections.

The design phase is now fully complete. Phase 0.5 execution can begin per the rhythm above.

---

## Future scope — extend to community contributor workflow + Claude Code skills

Beyond the AI-driven port, this document needs a v2 pass covering **post-v0.0.1 community contributions** — when external users start adding features, fixing bugs, and shipping improvements. The same CodeRabbit-aware PR-splitting discipline should apply.

Open items for that v2 pass:

- [ ] **Community contributor PR template** — `.github/PULL_REQUEST_TEMPLATE.md` enforcing: linked issue, scope statement, CodeRabbit-fittable size, settings-registry-updated checkbox (per §61.4), tests-added checkbox, screenshot/video for UI changes
- [ ] **Branch naming convention for community PRs** — `feature/<short-name>`, `fix/<short-name>`, `chore/<short-name>`. Distinct from the port's flat `phase-N<letter>` pattern to keep histories clean.
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
