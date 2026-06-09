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
- ✅ **Sequencer WPF-removal pass** — Resolved in Phase 0.5p2 (PR #242 / promoted #243). All 7 NINA-inherited projects (Core, Astrometry, Profile, Image, Equipment, PlateSolving, Sequencer) + Test now target `net10.0` headless per playbook §5.2/§8. WPF UI files deleted wholesale per §4.2 (~404 files); mediator-VM constraint dropped per §8.1; `BitmapSource` → `byte[]` for type signatures with OpenCvSharp4 wiring deferred per §line-2105; Compat stubs (`Astrometry/Compat/`, `Sequencer/Compat/`, `Image/ImageAnalysis/HeadlessStubs.cs`) preserve legacy call surfaces. `dotnet build OpenAstroAra.sln -c Release` → 0 errors, all CI matrix jobs green. Server csproj wired to all 7 libs in #244 / promoted #245 per playbook §8 Phase 4 scaffold.
- ✅ **NINA.Test.csproj post-Sequencer-removal** — Resolved alongside #242. Test subdirs that depended on deleted WPF/Sequencer code (Sequencer/, SimpleSequencer/, MGEN/, Plugin/, Converters/, ViewModel/, Autofocus/, FlatDevice/, Image/, PlateSolving/, Planetarium/, Focuser/, Rotator/, Dome/, Equipment/SDK/) purged per playbook §4.5. 296 platform-agnostic tests pass on macOS-arm64; 1006 NOVAS/SOFA fixtures filter to `[Platform("Win")]` since the natives are Windows-only Win32 binaries (libnovas.so + libsofa.so packaging is Phase 14e).

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
- ✅ **Filename template `$$TOKEN$$` vs `$TOKEN$`.** Verified post-Phase-0.5p2 (`OpenAstroAra.Core/Model/ImagePattern.cs` now compiles + readable). The canonical render-side token registry `ImagePatternKeys` declares every token as `$$TOKEN$$` (e.g., `Filter = "$$FILTER$$"`, `FrameNr = "$$FRAMENR$$"`, `ExposureTime = "$$EXPOSURETIME$$"`) — matches the double-dollar form the 4 default sources of truth use. Sonnet's flag on PR #141 ("might expect single-dollar") was a false positive against the un-built NINA inheritance; no action needed. Same shape as the `\\` vs `\` separator answer in PR #131.

## Phase 14 hardening candidates

- ✅ **Server AOT `JsonSerializerContext` source-gen.** **Resolved in Phase 14a** — single `AraJsonSerializerContext` partial class with `[JsonSerializable]` for all 133 DTO records + the 7 concrete `CursorPage<T>` instantiations + 8 `IReadOnlyList<T>` collection wrappers. Wired via `TypeInfoResolverChain.Insert(0, ...)` in `Program.cs`. `FileProfileStore.Persist`/`LoadOrDefaults` switched to typed-info overloads; pragmas removed. `dotnet run` smoke now passes end-to-end (verified: `/healthz` 200, GET/PUT `/api/v1/profile/imaging-defaults` round-trip, `profile.json` written to disk).
- ✅ **AOT-safe `JsonStringEnumConverter` per-enum.** Resolved in `phase-15-aot-enum-converters` — replaced the single non-generic `JsonStringEnumConverter(LowerCaseNamingPolicy)` with 8 per-enum `JsonStringEnumConverter<TEnum>(LowerCaseNamingPolicy.Instance)` registrations in `Program.cs`. Each generic instantiation is AOT-traceable; the `IL3050` suppression pragma is removed. .NET 10's `JsonStringEnumConverter<TEnum>` accepts a `JsonNamingPolicy` via its constructor — the earlier TODO assumed that constructor didn't exist.

## Phase 38 — §38k instruction prototypes deferred (need more than a device mediator)

Deferred during the §38k-13…18 equipment-mediator stub layer (PR #315). Each is a registered-prototype gap, not a `// TODO(port)` code marker — the instructions exist and compile; they're simply not wired into `HeadlessSequencerFactory.WithDefaults()` yet because they need a non-device dependency the headless daemon doesn't provide. Register them once the prerequisite lands.

- ✅ **`Dither` + `SwitchFilter`** — **resolved in §38k-22 (#318)**: a `HeadlessProfileService` stub satisfies their `IProfileService` dependency for prototype construction; both registered.
- ✅ **`SynchronizeDome`** — **resolved in §38k-21 (#317)**: added a `HeadlessDomeFollower` stub (the one non-mediator equipment dependency) and registered the instruction. `Enable`/`DisableDomeSynchronization` (dome + telescope only) landed earlier in §38k-18.
- **Full `TakeExposure` capture path** — needs an `IImagingMediator` stub + the image pipeline (OpenCvSharp4 + libraw, §line-2105) + exposure-info plumbing (frame type / gain / binning) beyond the no-arg instruction shape. The §38k-13 `HeadlessCameraMediator` deliberately surfaces `NotSupportedException` from `Capture`/`Download`/`LiveView` until then. **When TakeExposure lands, also reconcile the camera capture-block surface** (flagged by claude[bot] on #315): `RegisterCaptureBlock`/`ReleaseCaptureBlock` are inert no-ops and `IsFreeToCapture` always returns `true` while `Capture` faults — a real impl must make capture-block tracking consistent with the capture lifecycle so an instruction that registers a block and then checks `IsFreeToCapture` sees a truthful answer.
- ✅ **Connect/Disconnect/SwitchProfile capstone** (`ConnectAllEquipment`, etc.) — **resolved in §38k-22 (#318)**: registered all five via the `HeadlessProfileService` stub; `DisconnectAllEquipment`/`DisconnectEquipment` flipped `internal`→`public` (CA1002 on their `Devices` property fixed to `IReadOnlyList<string>`).

## Execution-engine TODOs

The §38 execution engine (`SequencerService`, #319) now **runs** sequences for real through NINA's inherited `Sequencer`. These items refine it as real equipment + data models land:

- **Pause / Resume** (deferred in #319). The headless engine has no pause hook (NINA's was WPF-coupled and stripped), so `SequencerService.PauseAsync`/`ResumeAsync` are accepted no-ops today (they deliberately do NOT emit a `paused` event the run wouldn't honor). To honor pause, add an instruction-boundary pause gate to the execution loop — e.g. drive the root container's top-level items through a wrapper that awaits a pause-`TaskCompletionSource` between items, or thread a pause token NINA's containers check. Re-enable the `sequence.paused`/`sequence.resumed` WS events when real suspension works.
- **Precise `frames_completed`** (deferred in #319). The run-state reports `frames_total` from `SequenceBodyInspector` but `frames_completed` is not yet tracked during real execution (the `IProgress<ApplicationStatus>` callback gives a per-current-item description, not an overall count). Hook instruction-completion (root container `RemoveRunningItem`, or per-item status) to increment it.
- **Profile source-of-truth.** `HeadlessProfileService` (§38k-22) returns a *default* in-memory `Profile`. An *executing* instruction must instead read the user's edited settings. The daemon persists those via `IProfileStore`/`profile.json` (the WILMA REST surface), while NINA instructions read `IProfileService.ActiveProfile`. Reconcile the two (adapter from the store to a live `IProfile`, or make the store the backing of a real `IProfileService`) and swap the DI registration off the stub. Playbook §8.1 says "keep" NINA's `IProfileService` as a singleton.
- **Camera capture-block surface** (from #315): `RegisterCaptureBlock`/`ReleaseCaptureBlock` inert + `IsFreeToCapture` hardcoded `true` while `Capture` faults — make consistent with the real capture lifecycle.
- **Real equipment behavior**: every `Headless*Mediator` / `HeadlessDomeFollower` returns "not connected" / no-op sentinels; real Alpaca-backed wiring swaps in at §14e.
