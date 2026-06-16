# COMMIT-PR-RULES.md

Authoritative reference for the per-phase + sub-PR rhythm, branch naming, code-review loop, and pre-PR gate used by the AI during the port. Read together with `design/PORT_PLAYBOOK.md` — which links here from §0 rule 9 (phase-boundary PR rhythm), §3 (phase plan + sub-PR mapping), §19.1 (git safety + branch allowlist), and §22 (direct-to-master merge model). Design phase is complete (see "Status" below); execution follows the rhythm specified here. **Workflow note (2026-06-02):** the `port/ara` integration branch is retired — every PR now branches from `master` and merges directly back to `master` (see PORT_PLAYBOOK §22.0 + `design/PORT_DECISIONS.md`). Historical "Decided" entries below that mention `port/ara` are preserved as a record of what was true at the time.

**Workflow note (2026-06-09) — PR size relaxed + merge-gate now mechanically enforced.** Two updates from the maintainer:
1. **PR size is no longer constrained to tiny single-step sub-PRs.** The original "≤200 files / one sub-PR per step" target (see "Phase size audit" + the Phase 0.5/12 sub-split tables below) was about keeping reviews tractable. In practice the current reviewer (claude[bot]/Sonnet) handles large diffs well — the analyzer-compliance work (PR #313, ~885 files) and the §38k-13…18 stub-layer PR (#315, one PR covering six § sub-steps) both reviewed cleanly. So **bundle related work and multiple commits into one coherent PR** when it reads as a single logical change; don't force smallness. Every *other* rule still holds verbatim: branch from master, push per commit, full review on every PR, and the merge-gate below.
2. **`master` is now a protected branch** (set up 2026-06-09). Required status checks: `Analyzer gate`, `review`, `Server build`, and the three `Client (analyze + test)` OSes; strict/up-to-date required; PR-before-merge; force-push + deletion blocked; `enforce_admins=false` (solo-admin emergency override retained). This makes the §19.1 merge-gate **mechanical**, not just policy — a PR literally cannot merge until those checks are green. **Reminder:** a green `review` *check* is necessary but NOT sufficient — the gate requires the review *comment body* to carry **no unaddressed findings** (learned on #314, where a green check still had a stale-doc finding to fix). Read the review, don't just watch the status dot.

## The constraint that drove this

Review capacity is limited by PR size. The current §22 plan is "one mega-PR at Phase 15" which is multi-thousand files — won't get effective review.

## Proposed solution: per-phase PRs with sub-PR splitting

### Per-phase PR rhythm

```
master                           ← the one long-lived branch; every PR merges directly here
  ├─ phase/0.5a-plugin-strip     ← sub-PR for Phase 0.5 part a
  ├─ phase/0.5b-mgen-strip       ← sub-PR for Phase 0.5 part b
  ├─ phase/1-net10-bump          ← Phase 1 (single PR fits)
  └─ ...
```

**Branch naming:** `phase/<N>[-<letter>]-<short-name>` — slash namespace, hyphenated words (e.g., `phase/0.5a-plugin-strip`, `phase/12h-settings`, `phase/38k-13-focuser-mediator`). Each branches from `master` and merges back to `master`. The slash namespace is valid because no branch is literally named `phase` (the old flat-name workaround — `phase-0.5a` — was forced only while `port/ara` existed as a branch and made `port/ara/...` illegal; retired 2026-06-02 along with the integration branch).

Rhythm:
1. AI branches from up-to-date master: `git checkout master && git pull && git checkout -b phase/<N>-<short-name>`
2. Does the phase's work on the branch (commits + pushes per commit)
3. Completes phase, runs §15 gate
4. At a **phase boundary**, tags `phase-N-complete` (sub-phase tags `phase-N-<letter>-complete` where the sub-phase is a coherent milestone)
5. Opens PR **targeting `master`**
6. Review poll-and-fix loop runs (AI watches for the review comment; auto-fix trivial + correctness findings via new commits; reasoned replies for disagreements; out-of-scope items → `design/PORT_TODO.md`). If no review appears within ~15 min, AI falls back to the built-in `/review` self-review as the gate signal.
7. **AI merges** once the §19.1 merge-gate clears (green CI + **review pass posted** — a structured review with no unaddressed findings, or a clean `/review` fallback; + ≥3 min quiescence + clean self-review against scope). Use `gh pr merge --delete-branch` (squash for multi-commit PRs that should land as one logical change; merge commit where per-commit granularity matters — §19.1) to remove the branch from origin in the same step. If any gate condition is ambiguous, AI posts `Held for human review @<user> — <reason>` and waits instead.
8. AI pulls updated `master` and starts the next sub-PR from there. **Whether the just-merged PR was the last in a phase** is determined by the phase sub-PR list in PORT_PLAYBOOK.md §3 plus the per-phase sub-split table in this document (e.g., `phase-12h` is the last under Phase 12's row); at a phase boundary AI ensures the `phase-N-complete` tag is pushed. There is no separate promotion step — the merge to `master` *is* the integration.

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

### Phase 12 (Flutter views) — DECIDED 8-PR split (2026-05-23)

**Status: settled.** 8 sub-PRs (12a–12h). Each stays under review limits. Order: 12a → 12b → 12c–12g (independent feature tabs, any order) → 12h last (consolidates §61 search registry across all settings panels).

| Sub-PR | Scope | Approx file count |
|---|---|---|
| **12a** App shell + global infrastructure | §25 visual design (NINA-style layout, nav rail, dark theme), §53 a11y + StatusIndicator widget, §41 mobile companion conditional shell, §30 first-run + saved-server management, §30.7 banner shell (shared by equipment-change + sky-data-missing variants), §32 disconnect modal, §35 emergency stop + alarm modal (§35.5), §46 notification feed (global), §54 bug report flow entry point (Help) | ~120 |
| **12b** Wizard (§37) | All 18 screens / 7 stages, mandatory profile-creation policy (§37 preamble + §30.4), §37.6 sky data downloads integration with §36.12 | ~80 |
| **12c** Imaging tab | Main image viewer, exposure controls, histogram, plate-solve overlay, §51 Health Indicator (always visible) + Diagnostic Panel, §64 Live View / loop imaging UI, §45 polar alignment iPolar-style continuous loop UI. (Framing/§47 mosaic moved to the Planning tab — see 12e + the reconciliation note below) | ~150 |
| **12d** Sequencer tab (§38) | Tree-based instruction editor, conditional logic UI, template instantiation, Resume Target seeding (§40.6), NINA import flow (§38.4), §42 hardware fault recovery surface | ~140 |
| **12e** Planning tab + Data Manager | §36/§25.5 Planning tab (merged Sky Atlas + Framing): Aladin Lite integration, Explore mode, §36.7 Tonight's Sky, §36.8 universal search, §36.9 comet support, Frame mode (§47 FOV + mosaic overlay), **§36.2 Data Manager 4-tab UI** (Sky Imagery / Star Catalogs / Target Thumbnails / Solar System), §36.13 sky-data-missing banner integration | ~130 |
| **12f** Image Library + frame viewer | §40 by-session organization, §40.5 frame viewer + §65 stretch picker + manual sliders, §40.6 Resume Target workflow, §40.7 auto-rating + HFR drift display, §40.8 bulk operations, §43 backup UI, §44 real-time backup stream UI | ~120 |
| **12g** Stats dashboard (§50) | Overview tiles, Targets rollup + per-target detail, Focus & Temperature scatter + regression, Guiding RMS trends, Frame Quality + composite score, Best Frames auto-sort, Calendar heatmap, CSV export | ~100 |
| **12h** Settings + smart search | All settings sub-screens (Equipment, Imaging, Plate Solving, Safety, Diagnostics, Storage, Planning/Optics, Notifications, Profile, PHD2), §29.1.3 ext4 reformat UX, §35 safety policies editor, §51 diagnostics mode picker (notify_only default), §63 PHD2 settings, **§61 smart settings search (⌘K)** cross-cutting all panels | ~150 |

**Data Manager placement decision (2026-05-23):** lives in 12e Planning, not 12h Settings, because the AI doing the survey-list UI naturally wires the downloader at the same time (cohesion with HiPS surveys + Aladin integration). Settings panel mounts a "Open Data Manager" link only.

**Sky Atlas + Framing → Planning merge (PORT_DECISIONS §36/§25.5, 2026-06-15):** the original split put Framing under 12c (Imaging) and Sky Atlas under 12e. These merged into a single **Planning tab** — one Aladin Lite surface with Explore / Tonight's Sky view modes + a Frame overlay toggle — so there is only one Chromium/webview_cef instance and target-find → frame is one uninterrupted flow. The tab shipped post-port as focused **v0.1.0 slices** rather than the monolithic 12c/12e PRs:

- **Frame-mode FOV overlay** consuming `/api/v1/profile/optics` (pixel-scale + field rectangle).
- **Optics Settings section + camera→optics auto-populate** (sensor geometry cached in the profile on first connect, §30.7 invalidation on swap, "Refresh from connected camera").
- **Mosaic panel-grid preview** (cols × rows × overlap) in Frame mode.
- **Tonight's Sky** — `ITonightSkyService` + `GET /api/v1/planning/tonight` (server) and the ranked `TonightSkyPanel` (client).

Still open under this banner: **"Build Mosaic Sequence"** (1e — needs server `IMosaicService`) and the **Tonight's Sky planetarium overlays** (horizon/solar-system/time-slider — needs Full DE440). See PORT_PLAYBOOK §36.7 / §47.2 implementation-status notes.

**Sub-PRs that exceed 200 files mid-port:** any sub-PR that turns out larger than estimated gets ad-hoc sub-split before opening (e.g., 12d → 12d.1 editor + 12d.2 template instantiation if it bloats). Decision made at PR-prep time, not pre-planned.

### Code review policy (DECIDED 2026-05-29, reviewer revised same day): every PR gets reviewed

**No skip-review markers.** Every Phase 0.5 sub-PR — mechanical or substantive, deletes or renames or branding — goes through full code review before merge. Rationale: mechanical PRs can hide real issues (wrong files renamed, missed exclusions, broken references after a rename) that only a careful pass catches. Belt-and-suspenders is worth the review-burden cost for a port of this size.

**Reviewer history** (intentionally short, intentionally documented because this is the kind of thing that's confusing to onlookers reading old PRs):

| Period | Reviewer | Outcome |
|---|---|---|
| 2026-05-23 → 2026-05-28 | CodeRabbit (`coderabbitai[bot]`) | Free-tier rate-limit (1 review/hr/org) incompatible with cadence; dropped. |
| 2026-05-29 (briefly) | Augment Code (`augmentcode[bot]` / `app/augmentcode`) | Worked for 2 PRs (#105, #106) then exhausted its internal Gemini quota mid-session. Also authored one autonomous PR (#107) that overclaimed scope. Dropped. |
| 2026-05-29 (briefly) | Gemini via `google-github-actions/run-gemini-cli` | Hit `gemini-3-flash` free-tier daily cap (20 req/day) on the very PR introducing it. Workflow file removed before merging. |
| 2026-05-29 → present | **Sonnet (Anthropic Claude, running on user's account)** | Posts structured reviews as `joeytroy` user comments. No separate quota meter. Caught real bugs on PR #108 that self-review missed. **Current.** |

This means:
- All 16+ Phase 0.5 sub-PRs + all 8 Phase 12 sub-PRs + every other phase PR get full review
- AI follows the standard poll-and-fix loop (see "Review loop" section below) on every PR — no fast-path for any PR type

### Review loop (AI-driven)

**Pre-PR gate (always runs before opening the PR):** AI invokes `scripts/pre-pr-check.sh` per playbook §14.4. Exits non-zero on any failure (build error, test failure, format issue, settings-registry miss, etc.). AI fixes failures + re-runs the script + opens the PR only when green. For PRs touching user-visible Flutter UI, AI also captures screenshots per §14.6 and attaches them to the PR description.

After the AI opens any PR (Phase 0.5 sub-PRs, Phase 12 sub-PRs, or any other phase):

1. **AI opens the PR** (with green pre-PR gate + screenshots if applicable).
2. **AI processes each finding:**
   | Finding type | AI action |
   |---|---|
   | Trivial / nit (formatting, naming, minor clarity) | Fix immediately, push commit, post reply: *"Fixed in `<sha>`"* |
   | Real bug or correctness issue | Fix immediately, push commit, post reply: *"Addressed in `<sha>`"* |
   | Disagreement (reviewer suggests something wrong, contradicts the playbook spec, or is beyond PR scope) | Post reply with reasoning + link to the relevant playbook section; do not change code |
   | Out-of-scope suggestion (broader refactor, future feature) | Append entry to `design/PORT_TODO.md` with PR reference; reply: *"Acknowledged — tracked in design/PORT_TODO.md for follow-up"* |
3. **Sonnet re-reviews after each fix push** if the user invokes it again; otherwise AI handles round-2 findings via the same poll-and-fix loop.
4. **AI merges** under the §19.1 merge-gate (policy revised 2026-05-23).
5. **After AI merge**, AI pulls updated `master` and starts the next sub-PR from there

**Triggering a re-review:** Sonnet runs on the user's invocation (out-of-band of GitHub comments). AI does not @-mention any bot — if Sonnet's review is needed and hasn't appeared, AI either waits or falls back to `/review` per the 2026-05-26 fallback policy.

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

**Layer 4 — Reviewer focus**:

Sonnet is instructed (via the user's prompt) to flag settings without meaningful descriptions / keywords (not just structurally-present entries), in addition to the standard correctness/style review.

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

The help registry (`client/openastroara_client/lib/help/registry.dart`, specced in `PORT_PLAYBOOK.md` §69) follows the **same enforcement model** as the settings registry: pre-commit hook + CI check + PR template checkbox + reviewer instructions. Mechanism is fully described in §69.4; this section just affirms it applies to community contributions identically to maintainer commits, with no opt-out.

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

- [x] ~~Confirm per-phase PR rhythm OR alternative~~ → **Decided 2026-05-23**: per-phase + sub-PR splitting
- [x] ~~Confirm sub-PR letter scheme (a/b/c/...) and branch naming~~ → **Decided 2026-05-23**: letter scheme `a-h`. Branch naming originally specified as `port/ara/phase-N/<letter>`; revised in prep-ci PR to flat names (`phase-Na`, e.g., `phase-12e`) after discovering Git refs forbid `port/ara/...` while `port/ara` itself is a branch.
- [x] ~~Confirm skip-review marker syntax~~ → **Decided 2026-05-23**: **no skip-review markers; every PR gets reviewed**. Belt-and-suspenders catches subtle issues that mechanical PRs can hide. See "Code review policy" section above.
- [x] ~~Decide on `develop` branch~~ → **Decided 2026-05-23**: **no**. The integration branch `port/ara` already plays that role; adding `develop` adds a merge step without benefit for a single-developer port. Final phase-15 PR goes `port/ara → master` directly.
- [x] ~~Should AI auto-continue to next phase after PR merge~~ → **Decided 2026-05-23**: AI pulls updated `port/ara` after user merges, then auto-starts the next sub-PR (no human nudge needed between sub-PRs within the same phase). Between phases (e.g., after Phase 12 fully merges and Phase 13 begins), AI auto-starts unless user has paused
- [x] ~~What happens if the reviewer flags a real issue mid-port~~ → **Decided 2026-05-23**: AI fixes via additional commits on the same sub-branch (not a follow-up PR; not an amend that destroys history). See "Review loop" section above for the full pattern
- [x] ~~Push cadence — push after every commit vs batched~~ → **Decided 2026-05-23**: push after every commit (visibility for user + enables incremental review)

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
- Poll-and-fix workflow specced (AI handles trivial + correctness findings, user handles merge)
- Push cadence: after every commit

**Decisions made 2026-05-23 (continued):**
- **No skip-review markers** — every PR gets full review, including mechanical Phase 0.5 deletes/renames (belt-and-suspenders for subtle issues)
- **No `develop` branch** — sub-PRs land on `port/ara` directly; Phase 15 final PR `port/ara → master`

**Baked into `PORT_PLAYBOOK.md` (2026-05-23):**
- ✅ §0 rule 9 — "Tag every phase boundary; open the PR; merge it; continue." References this doc for the full rhythm + review loop. **Policy revised 2026-05-23** (later same day, in `prep-ci` PR #2): AI merges sub-PR + final PRs under the §19.1 merge-gate; auto-continues to next sub-PR after merge. Original 2026-05-23 decision was "AI never merges, ever"; reversed when the user granted full merge authority.
- ✅ §3 phase plan — Phase 0.5 sub-PR rhythm references the 16-sub-PR mapping (0.5a–0.5p); Phase 12 references the 8-sub-PR mapping (12a–12h); cross-cutting sub-PR rhythm paragraph added after the phase list.
- ✅ §19.1 git safety — branch allowlist now permits `port/ara` (integration) + flat-named `phase-N[<letter>]` sub-PR feature branches (e.g., `phase-0.5a`, `phase-12h`) plus a small set of named prep branches (e.g., `prep-ci`). **Merge policy revised 2026-05-23** in `prep-ci` PR #2: original "AI never merges, ever" rule replaced with "AI merges under a strict merge-gate" (green CI + reviewer quiescent ≥3 min + no unresolved findings + clean self-review). User retains override. Tag scheme extended to `phase-N-<letter>-complete` for sub-PRs.
- ✅ §22 final pass — final PR confirmed `port/ara → master` (no `develop` branch); release notes step updated to use `CHANGELOG.md` per §33.7 + Keep-a-Changelog format + fresh `[Unreleased]` placeholder; mobile-deferred-to-v0.1.0 noted in CHANGELOG sections.

The design phase is now fully complete. Phase 0.5 execution can begin per the rhythm above.

---

## Future scope — extend to community contributor workflow + Claude Code skills

Beyond the AI-driven port, this document needs a v2 pass covering **post-v0.0.1 community contributions** — when external users start adding features, fixing bugs, and shipping improvements. The same review-aware PR-splitting discipline should apply.

Open items for that v2 pass:

- [ ] **Community contributor PR template** — `.github/PULL_REQUEST_TEMPLATE.md` enforcing: linked issue, scope statement, size, settings-registry-updated checkbox (per §61.4), tests-added checkbox, screenshot/video for UI changes
- [ ] **Branch naming convention for community PRs** — `feature/<short-name>`, `fix/<short-name>`, `chore/<short-name>`. Distinct from the port's flat `phase-N<letter>` pattern to keep histories clean.
- [ ] **Auto-split heuristics** — if a community PR exceeds capacity, what guidance do we give? "Split before submitting" via labelled `needs-split` workflow?
- [ ] **Claude Code skill recommendations** for community contributors — the available Claude Code skills (`code-review`, `security-review`, `fewer-permission-prompts`, `verify`, `update-config`, `init`, etc.) should be documented in `CONTRIBUTING.md` as recommended workflow:
  - Before opening a PR, run `/security-review` on the diff
  - For PRs touching UI, use `/verify` to confirm the change works in the actual app
  - For PRs touching the settings registry, run `/code-review` with focus on §61.4 compliance
  - For PRs adding new sections to design docs, follow the existing playbook section style
- [ ] **Pre-commit hooks for contributors** — beyond the already-baked settings-registry gate (see "Settings-registry gate" section above — settled, applies to community too): additional lint rules enforcing no `--no-verify`, no force pushes to main/master, license-header presence on new C# files
- [ ] ~~**`.coderabbit.yaml`**~~ — obsolete: CodeRabbit removed from the org 2026-05-29. The current reviewer (Sonnet) doesn't read a per-repo config; reviewer behavior is set per-invocation by the user.
- [ ] **Issue templates** (`.github/ISSUE_TEMPLATE/`) — bug-report (auto-filled with §54 bug-report-submission zip), feature-request (mapped to §55 roadmap tiers), driver-quirk-report (auto-routed upstream per §52.5)
- [ ] **Release cadence post-v0.0.1** — semver discipline, RELEASE_NOTES.md entry per release, GitHub Releases pipeline (already in §14.3 CI), how community PRs feed into next-release vs current-release branches
- [ ] **Maintainer workflow** — who reviews, merge criteria, how long PRs sit before stale-bot pings, etc. Light-touch at first (small project); formalize as community grows.

The unifying theme: **community contributors should follow the same discipline the AI follows during the port.** Same PR sizes, same review markers, same registry requirements, same hooks. That keeps `master` clean across the lifetime of the project, not just through Phase 15.

This v2 pass also extends the `design/` directory — likely adds `CONTRIBUTING.md` (at repo root, not under `design/`), `MAINTAINING.md`, and Claude Code skill recipes under `design/skills/` or similar.

**Status:** captured here for future session. Not in scope for the port itself — comes after v0.0.1 ships.
