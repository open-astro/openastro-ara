# OpenAstro Ara — Port Progress

Single-page status. Updated on every commit. Per PORT_PLAYBOOK.md §20.1, "Currently working on" must point at a specific file, endpoint, or sub-PR — never "various refactoring."

## Current

- **Phase:** 0.5a — Fork hygiene / WPF demolition (in flight)
- **Started:** 2026-05-23
- **Currently working on:** phase-0.5a-6 sub-PR (PR #9) — last sub-PR of Phase 0.5a; deletes NINA.WPF.Base remainder. CodeRabbit walkthrough pending after billing fix + empty-commit retrigger.

## Completed

- ✅ Pre-Phase-0.5 prep (no playbook phase number)
  - prep-ci (PR #2, merged): CI baseline + branch naming + merge authority
  - rules-tighten (PR #10, merged): tightened §19.1 + added §22 periodic master promotion
- ⏳ Phase 0.5a — Fork hygiene / WPF demolition (5 of 6 sub-PRs merged; PR #9 pending)
  - phase-0.5a-1 (PR #4, merged): .sln deregister + NINA shell files + NINA.CustomControlLibrary/
  - phase-0.5a-2 (PR #5, merged): NINA/View/Equipment + Imaging
  - phase-0.5a-3 (PR #6, merged): rest of NINA/View/
  - phase-0.5a-4 (PR #7, merged): remaining NINA/ subdirs + .gitmodules
  - phase-0.5a-5 (PR #8, merged): NINA.WPF.Base part 1 (ViewModel + Resources + Interfaces)
  - phase-0.5a-6 (PR #9, in review): NINA.WPF.Base part 2 (final)

## Next

- After current task: tag `phase-0.5a-complete` on `port/ara` HEAD, push tag, open `port/ara → master` promotion PR per playbook §22.0
- After current phase: Phase 0.5b — delete MGEN + nikoncswrapper + WiX + Plugin (~131 files combined; single PR — under the 150 CR cap with margin)
- Long-horizon: Phase 0.5c (vendor SDKs) → 0.5d (COM glue) → 0.5e (WebView2) → 0.5f (Stefan branding) → 0.5g-n (project renames) → 0.5o (sln rename + .gitignore) → 0.5p (.NET 10 bump). Each completes with a `phase-0.5X-complete` tag + `port/ara → master` promotion.
