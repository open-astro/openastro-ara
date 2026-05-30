# OpenAstro Ara — Port TODO log

Append-only list of every `TODO(port)` and `PORT_BLOCKED` left in the codebase during the port, grouped by phase. Also tracks out-of-scope CodeRabbit suggestions deferred for follow-up.

Per PORT_PLAYBOOK.md §0 rule 4 (`Cite when stuck`): when you cannot translate a construct, leave a `// TODO(port): <one sentence>` and a placeholder that compiles, log it here, and move on. Sweep in Phase 15.

Per §0 rule 6 + §15 step 7 + COMMIT-PR-RULES.md CodeRabbit rule "out-of-scope suggestions": when CR suggests a broader refactor or future feature, log it here with the PR reference and reply "Acknowledged — tracked in design/PORT_TODO.md for follow-up".

---

## Phase 0.5a — Fork hygiene / WPF demolition

### Cascade scrubs

- ✅ `NINA.Equipment.csproj` ProjectReferences to nikoncswrapper + NINA.MGEN — scrubbed in Phase 0.5b
- ✅ `NINA.Test.csproj` ProjectReferences to NINA.MGEN + NINA.Plugin + NINA.CustomControlLibrary + NINA.WPF.Base + NINA — scrubbed in Phase 0.5b
- ✅ **Phase 0.5p-followup buildfix** (`phase-0.5p-followup-buildfix`, 2026-05-26) — Core scrubs (Notification.cs CustomDisplayPart removal, MyMessageBox.cs MyMessageBoxView removal, +System.Management package for WMI usage in Logger.cs/SerialPortProvider.cs), Astrometry DB-greenfield reconciliation (DatabaseInteraction.cs stubbed to GetUT1_UTC + GetDisplayAlias only; consumers AstroUtil.cs/TopocentricCoordinates.cs cleaned), Equipment vendor file deletions (EDCamera + MGENGuider + ASCOMInteraction + 4 flat device drivers + 3 orphaned test files), Phase 6 Alpaca discovery bug fix (`deviceType:` → `deviceTypes:` + missing `logger:` param). After this PR, `dotnet build OpenAstroAra.sln -c Release` is green except for the Sequencer item below.
- ⏳ **Sequencer WPF-removal pass** — `OpenAstroAra.Sequencer.csproj` references and `.cs` source files still use NINA.WPF.Base types (IImageHistoryVM, IImageSaveMediator, etc.). 96 compile errors as of the phase-0.5p-followup-buildfix PR. This is a substantial pass equivalent in scope to a Phase sub-PR — needs its own branch (`phase-0.5p-followup-sequencer` or similar) and explicit user authorization before starting.
- ⏳ `NINA.Test.csproj` post-Sequencer-removal: tests that depend on Sequencer types will also need scrubbing once Sequencer's WPF dependencies are resolved.

### Source-file `TODO(port)` markers

(none yet — Phase 0.5a is pure delete; no new code introduced)

---

## Out-of-scope CodeRabbit suggestions

- **PR #71** (port/ara → master promotion) — CR flagged `ExposureControlsPanel` for missing filter-slot control wired to `params.filterSlot` / `setFilterSlot`. This is a feature add (new UI control), not a bug fix; tracked here to land in a focused Phase 12c follow-up sub-PR alongside the rest of the exposure-controls editable wiring.

## CodeRabbit security findings addressed

- **PR #12** (port/ara → master promotion) — zizmor + CodeRabbit flagged `.github/workflows/ci.yml` for (1) mutable `actions/checkout@v4` reference, (2) missing `persist-credentials: false`. Both fixed on `ci-harden` branch. Future workflow steps added at Phase 0.5p / Phase 4 / Phase 11 / Phase 14 should follow the same pattern: actions pinned to a commit SHA + `persist-credentials: false` unless a step explicitly needs git push.

---

## Phase 15 sweep candidates

- ✅ **Consolidate `_SwitchRow` into shared editable-field widget set.** Resolved in PR #104 (Phase 12h.2-switch) — lifted into `lib/widgets/settings/editable_field.dart` as `SettingsSwitchRow` with optional `hint` slot. All 6 panel copies removed.

## Phase 14 hardening candidates

- ⏳ **Server AOT `JsonSerializerContext` source-gen.** Discovered during Phase 12h.6a smoke test: `dotnet run` in Development mode hits `System.NotSupportedException: JsonTypeInfo metadata for type 'SequenceCreateRequestDto' was not provided by TypeInfoResolver of type 'OpenApiJsonSchemaContext'` on the first request. `OpenAstroAra.Server.csproj` has `<PublishAot>true</PublishAot>` but the project has no `JsonSerializerContext` with `[JsonSerializable]` attributes for any DTO. CI does not catch this (server-build job only runs `dotnet build` + `dotnet publish -p:PublishAot=false` + Docker — no `dotnet run` smoke). Latent since Phase 7 (first DTOs landed); surfaces in dev-mode OpenAPI introspection on every endpoint group. Fix is mechanical: add one `partial class AraJsonSerializerContext : JsonSerializerContext` annotated with `[JsonSerializable(typeof(...))]` for every DTO record across `Contracts/` (~70 DTOs as of Phase 12h.6a), wire `opts.SerializerOptions.TypeInfoResolverChain.Insert(0, AraJsonSerializerContext.Default)` in `Program.cs`. Out-of-scope for any feature sub-PR — needs its own hardening branch in Phase 14 alongside the §14.3 AOT publish matrix work.
