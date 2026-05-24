# OpenAstro Ara — Port TODO log

Append-only list of every `TODO(port)` and `PORT_BLOCKED` left in the codebase during the port, grouped by phase. Also tracks out-of-scope CodeRabbit suggestions deferred for follow-up.

Per PORT_PLAYBOOK.md §0 rule 4 (`Cite when stuck`): when you cannot translate a construct, leave a `// TODO(port): <one sentence>` and a placeholder that compiles, log it here, and move on. Sweep in Phase 15.

Per §0 rule 6 + §15 step 7 + COMMIT-PR-RULES.md CodeRabbit rule "out-of-scope suggestions": when CR suggests a broader refactor or future feature, log it here with the PR reference and reply "Acknowledged — tracked in design/PORT_TODO.md for follow-up".

---

## Phase 0.5a — Fork hygiene / WPF demolition

### Cascade scrubs deferred to later phases

- `NINA.Sequencer.csproj` line with `<ProjectReference Include="..\NINA.WPF.Base\NINA.WPF.Base.csproj" />` is now dangling. Plus NINA.Sequencer's .cs files use WPF.Base types directly. Removal happens at Phase 0.5p (.NET 10 bump) or earlier if a dedicated "Sequencer WPF-removal" pass is needed.
- `NINA.Test.csproj` has the same dangling reference to NINA.WPF.Base.csproj, plus `<ProjectReference Include="..\NINA\NINA.csproj" />`. Test code that depended on the WPF host needs deletion or stubbing.
- `NINA.Plugin.csproj` has dangling reference to NINA.WPF.Base.csproj. Will be removed entirely in Phase 0.5b along with the project itself.
- `NINA.Setup/NINA.Setup.wixproj` + `NINA.SetupBundle/NINA.SetupBundle.wixproj` reference `NINA/NINA.csproj`. Will be removed entirely in Phase 0.5b.

### Source-file `TODO(port)` markers

(none yet — Phase 0.5a is pure delete; no new code introduced)

---

## Out-of-scope CodeRabbit suggestions

(none yet)

---

## Phase 15 sweep candidates

(none yet)
