# OpenAstro Ara — Port TODO log

Append-only list of every `TODO(port)` and `PORT_BLOCKED` left in the codebase during the port, grouped by phase. Also tracks out-of-scope CodeRabbit suggestions deferred for follow-up.

Per PORT_PLAYBOOK.md §0 rule 4 (`Cite when stuck`): when you cannot translate a construct, leave a `// TODO(port): <one sentence>` and a placeholder that compiles, log it here, and move on. Sweep in Phase 15.

Per §0 rule 6 + §15 step 7 + COMMIT-PR-RULES.md CodeRabbit rule "out-of-scope suggestions": when CR suggests a broader refactor or future feature, log it here with the PR reference and reply "Acknowledged — tracked in design/PORT_TODO.md for follow-up".

---

## Phase 0.5a — Fork hygiene / WPF demolition

### Cascade scrubs

- ✅ `NINA.Equipment.csproj` ProjectReferences to nikoncswrapper + NINA.MGEN — scrubbed in Phase 0.5b
- ✅ `NINA.Test.csproj` ProjectReferences to NINA.MGEN + NINA.Plugin + NINA.CustomControlLibrary + NINA.WPF.Base + NINA — scrubbed in Phase 0.5b
- ⏳ `NINA.Sequencer.csproj` still has `<ProjectReference Include="..\NINA.WPF.Base\NINA.WPF.Base.csproj" />` (dangling). Plus NINA.Sequencer's .cs files use WPF.Base types directly. Removal happens at Phase 0.5p (.NET 10 bump) or earlier if a dedicated "Sequencer WPF-removal" pass is needed.
- ⏳ `NINA.Test.csproj` and `NINA.Equipment.csproj` still have .cs files using types from MGEN/Plugin/WPF.Base/NINA — compile errors persist; resolved during Phase 0.5p or a dedicated WPF-removal pass on these projects.

### Source-file `TODO(port)` markers

(none yet — Phase 0.5a is pure delete; no new code introduced)

---

## Out-of-scope CodeRabbit suggestions

(none yet)

## CodeRabbit security findings addressed

- **PR #12** (port/ara → master promotion) — zizmor + CodeRabbit flagged `.github/workflows/ci.yml` for (1) mutable `actions/checkout@v4` reference, (2) missing `persist-credentials: false`. Both fixed on `ci-harden` branch. Future workflow steps added at Phase 0.5p / Phase 4 / Phase 11 / Phase 14 should follow the same pattern: actions pinned to a commit SHA + `persist-credentials: false` unless a step explicitly needs git push.

---

## Phase 15 sweep candidates

(none yet)
