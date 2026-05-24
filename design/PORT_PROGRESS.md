# OpenAstro Ara — Port Progress

Single-page status. Updated on every commit. Per PORT_PLAYBOOK.md §20.1, "Currently working on" must point at a specific file, endpoint, or sub-PR — never "various refactoring."

## Current

- **Phase:** 0.5c — Delete vendor SDKs (next up)
- **Started:** 2026-05-23
- **Currently working on:** `ci-harden` follow-up PR addressing PR #12's CodeRabbit security finding (pin `actions/checkout@v4` to SHA + `persist-credentials: false`). Then will open the master promotion PR for phase-0.5b-complete (supersedes PR #12 which was scoped only to 0.5a).

## Completed

- ✅ Pre-Phase-0.5 prep
  - `prep-ci` (PR #2): CI baseline + branch naming + merge authority
  - `rules-tighten` (PR #10): tightened §19.1 (no merge-on-rate-limit) + added §22 periodic master promotion
  - `tracking-files` (PR #11): added the four §1 tracking files (retroactive)
- ✅ Phase 0.5a — Fork hygiene / WPF demolition (tag: `phase-0.5a-complete`)
  - `phase-0.5a-1` (PR #4): .sln deregister + NINA shell files + NINA.CustomControlLibrary/
  - `phase-0.5a-2` (PR #5): NINA/View/Equipment + Imaging
  - `phase-0.5a-3` (PR #6): rest of NINA/View/
  - `phase-0.5a-4` (PR #7): remaining NINA/ subdirs + .gitmodules
  - `phase-0.5a-5` (PR #8): NINA.WPF.Base part 1 (ViewModel + Resources + Interfaces)
  - `phase-0.5a-6` (PR #9): NINA.WPF.Base part 2 (final)
- ✅ Phase 0.5b — Delete MGEN + nikoncswrapper + WiX + Plugin (tag: `phase-0.5b-complete`)
  - `phase-0.5b` (PR #13): 131 deletions + cascade scrubs in NINA.Equipment.csproj + NINA.Test.csproj

## In flight

- ⏳ `ci-harden` — security hardening on `.github/workflows/ci.yml` per PR #12 CR finding
- ⏳ PR #12 `port/ara → master` promotion (phase-0.5a) — open; will be closed and superseded by a phase-0.5b promotion that includes ci-harden + 0.5b

## Next

- After ci-harden merges to port/ara: open new master promotion PR (`port(promote): merge phase-0.5b-complete to master`), close PR #12
- After master promotion: Phase 0.5c — delete vendor SDKs (`NINA.Equipment/SDK/CameraSDKs/*` + vendor concrete impls in `MyCamera/MyFilterWheel/MyFocuser`). Estimated 60-120 files; single PR.
- Long-horizon: 0.5d (COM glue) → 0.5e (WebView2) → 0.5f (Stefan branding) → 0.5g-n (project renames) → 0.5o (sln rename + .gitignore) → 0.5p (.NET 10 bump). Each completes with `phase-0.5X-complete` tag + master promotion.
