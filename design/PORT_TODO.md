# OpenAstro Ara — Port TODO log

Append-only list of every `TODO(port)` and `PORT_BLOCKED` left in the codebase during the port, grouped by phase. Also tracks out-of-scope CodeRabbit suggestions deferred for follow-up.

Per PORT_PLAYBOOK.md §0 rule 4 (`Cite when stuck`): when you cannot translate a construct, leave a `// TODO(port): <one sentence>` and a placeholder that compiles, log it here, and move on. Sweep in Phase 15.

Per §0 rule 6 + §15 step 7 + COMMIT-PR-RULES.md CodeRabbit rule "out-of-scope suggestions": when CR suggests a broader refactor or future feature, log it here with the PR reference and reply "Acknowledged — tracked in design/PORT_TODO.md for follow-up".

---

## §68 AlpacaBridge integration — client-side follow-ups (server complete)

**§68 server-side is done** (gate core #410, 30s cache #411, connect-block 503 #412, warn-band WS event #413) **and the §68.2 warn banner shipped client-side (#414).** The remaining §68 work is **client-side (Flutter)** + a couple of structural prerequisites, tracked here:

- ✅ **§68.2 — dismissible "newer AlpacaBridge recommended" banner (DONE, #414).** `AlpacaBridgeWarningBanner` + `alpacaBridgeWarningProvider` consume the `equipment.alpaca_bridge_outdated_warn` WS event and show a per-version-dismissible advisory banner on the Equipment settings panels.
- **§68.2 — 503 connect modal — BLOCKED on a client equipment-connect path.** Surface the **503 `alpaca_bridge_outdated`** connect failure as the §68.2 modal. **Prerequisite (architectural gap, 2026-06-13 finding):** the client has **no manual Alpaca-device connect** — only the guider has a connect call (`guider_api.dart`); the 10 Alpaca devices have discovery + selection (`alpaca_selection_state.dart`) + an auto-connect-**setting** (`equipment_connection_state.dart`, persisted to the profile) but **no POST `/equipment/{type}/connect` wrapper and no "Connect" button**. So there's nowhere a client-initiated 503 can surface yet. Whether a manual connect UI is v0.0.1 or the §52.1 auto-connect-on-boot model makes it moot is a **scope decision for the user.**
- **§68 gate altitude — CONFIRMED correctly placed at the REST endpoint (2026-06-13 finding).** `ConnectGatedAsync` gates `POST /equipment/{type}/connect`. Checked: there is **no daemon-side auto-connect-on-boot** implemented (`equipment_connection_state.dart`/`StoreBackedProfileService` only *persist* the auto-connect setting; nothing acts on it at startup), so the REST endpoint is the **only** equipment-connect path today and the gate guards it correctly. **When §52.1 auto-connect-on-boot is built, it must route through the same gate** (or the equipment-layer `ConnectAsync` should host the §68.1 handshake) so a boot-time auto-connect can't bypass it.
- **§68.2 — wizard Screen 2 missing-bridge UX.** When no AlpacaBridge/Alpaca devices are discoverable, show the install command (`sudo apt install alpaca-bridge`) + [Retry detection]; block [Next] until a device is discovered. (Server already returns the discovery list; this is wizard UI.)
- **§68.4 — §61 search-registry entries — needs a registry-design decision.** `equipment.alpacabridge.version` + `equipment.alpacabridge.troubleshoot` keyword entries. **Blocker:** the `Setting` registry (`settings/registry.dart`) models *settable fields* (each entry needs `type`/`defaultValue`/`profilePath`, and the registry CI gate cross-references entries against editable panel widgets). An AlpacaBridge version/troubleshoot pointer is **informational/navigational, not settable**, so it doesn't fit `Setting` and would likely fail the gate. Needs either an "informational entry" registry kind or a Help-registry (§69) home — a small design call before adding.
- **§68.5 — wizard-advance integration test** (`alpacabridge_missing_blocks_wizard_advance`) is client-side; the server-side §68.5 cases (version-below-minimum → 503, warn-band → event) are covered by `EquipmentConnectGateTest` + `AlpacaBridgeGateNotifierTest`.

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

## Merge-gate audit follow-ups (2026-06-17)

A retrospective audit of merged PRs (the bot's verdict-at-merge vs. the §19.1 gate) found **no unaddressed high-severity defect** — the recall-biased reviewer's near-permanent "⚠️ Issues found" sign-off is not itself a defect signal, and every high-severity keyword in a final verdict was negated / avoided-by-design / an explicit non-blocker. It did surface these **low-severity, bot-acknowledged-non-blocker** hardening items, logged here and being remediated as focused follow-up PRs (each driven to the bot's ✅ Approved before merge):

- **#384 — dark-library build service: disposal/cancellation gap.** The §63.6 guider dark-library build op has a disposal/cancellation gap "worth addressing before a live hardware test exposes it." Observe cancellation + dispose resources cleanly on abort. (low–med)
- **#390 — WS event-stream: faulted/slow TCP drain silently drops the error.** In the §60.9 client WS transport, a slow or faulted TCP drain surfaces a future error that is silently dropped rather than logged/surfaced. (low)
- **#425 — backup `listSnapshots` silently drops malformed entries.** `whereType<Map<String,dynamic>>()` skips a malformed snapshot entry with no log line, making it invisible. Log when an entry is skipped. (low)
- **#349 — image preview guards.** Add the `GetPixels` guard + a full-preview max-dimension resize alongside the grayscale-thumbnail guard in the §65 render path. (low)
- **#399 — latent dialog error-drop.** The guider calibration dialog's error contract is correct today, but if `_run` is ever refactored to rethrow, errors would be silently dropped. Make it robust to that. (very low / latent)

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
- ✅ **Full `TakeExposure` capture path** — **resolved in §14e capture-path PRa+PRb (#343 + follow-up)**: real `CameraService` pipeline (expose → download → §72 FITS → §28 catalog) + re-ported `TakeExposure` executing through `IImagingMediator.CaptureImage` on the same pipeline. The #315 capture-block note is addressed: `IsFreeToCapture` truthfully reflects the shared in-flight capture gate (Register/Release stay inert — no headless consumer registers blocks). Still §2105-gated: the in-memory render path (`CaptureAndPrepareImage`/`PrepareImage`/live view, OpenCvSharp4 + libraw) — `TakeExposure` deliberately discards the returned `IExposureData`.
- ✅ **Connect/Disconnect/SwitchProfile capstone** (`ConnectAllEquipment`, etc.) — **resolved in §38k-22 (#318)**: registered all five via the `HeadlessProfileService` stub; `DisconnectAllEquipment`/`DisconnectEquipment` flipped `internal`→`public` (CA1002 on their `Devices` property fixed to `IReadOnlyList<string>`).

## Execution-engine TODOs

- **Sequencer captures share the "manual capture" session** (flagged on #344, §14e capture-path PRb). `CameraService.CaptureImage` (the `IImagingMediator` sequencer path) routes every frame through `EnsureManualCaptureSessionAsync`, so a named-target sequence run's lights land in the same catalog session as ad-hoc manual REST snapshots. Acceptable for PRb (the capture pipeline is the deliverable; no session-orchestration layer exists yet), but a §40/§50 catalog-organisation concern: a real run should open and own a per-target session and stamp its frames with it. Resolve when the execution engine grows session lifecycle — a sequence run creates a session keyed on target/start-time, and `TakeExposure` passes that session id down through `CaptureImage`.
- **REST manual capture is LIGHT-only by design** (flagged on #344, §14e capture-path PRb). `CaptureInBackgroundAsync` (the fire-and-forget REST path) hardcodes `"LIGHT"`/`FrameType.Light`; `ExposureRequestDto` has no `ImageType` field, so the REST snapshot endpoint cannot produce darks/flats/bias. This is **deliberate for v0.0.1**: the manual-capture endpoint (§60.5) is for ad-hoc light/test snapshots, while calibration frames are sequencer-driven (§38.7 templates + §39 auto-flats/dark-library + calibration sessions) where the frame type is carried on the `CaptureSequence`. If a future REST "capture a calibration frame" affordance is wanted, add an optional `ImageType` (default LIGHT) to `ExposureRequestDto` and thread it into `CaptureCoreAsync` (which is already frame-type-parameterized — only the REST entry point hardcodes it).
- **FITS keyword-convention audit** (flagged on #344). The capture path writes `GAIN` and `OFFSET` for the camera's electronic gain/offset — the NINA/Siril/PixInsight/ASTAP convention, and what ARA's own `FITSHeader` reader round-trips. A reviewer noted these aren't in the core FITS standard dictionary (some pipelines also look for `PEDESTAL`/`BLKLEVEL`). No change for v0.0.1 — interop with the astrophotography ecosystem + ARA's reader wins, and `PEDESTAL` carries a *different* semantic (a data value to subtract). Revisit only as part of a deliberate full FITS-header-convention audit (alongside `IMAGETYP` casing, `DARKFLAT` vs `DARK`, etc.) if/when broader tool compatibility is assessed.
- **§28 `ExposureSeconds` is `int` across the frames schema + DTOs** (flagged on #343). Sub-second calibration exposures (bias/short darks) round up to 1s in the catalog (the FITS `EXPTIME` header preserves the exact value). Widening to `double` touches the SQLite column affinity, `FrameDto`/`FrameListItemDto`, the stats aggregations and the WILMA codegen wire shape — do it as one dedicated pass before the calibration workflows (§40) ship. **Same pass: make `Gain` nullable end-to-end** (the capture path currently carries `Gain ?? -1` as a magic sentinel into the int column, also flagged on #343).

**§38 client editor — container depth (follow-ups to the container palette slice).** The editor can now add `Sequential`/`Parallel` containers, nest instructions inside them (daemon template shape, verbatim round-trip), ✅ **rename a container**, ✅ **edit its loop conditions** (`LoopCondition` iterations + `TimeSpanCondition` duration), and ✅ **edit its triggers** (`MeridianFlipTrigger` — catalog + nina_dom ops + state + field-panel Triggers UI). The container model is now structurally complete (child items + name + conditions + triggers), matching NINA's Advanced Sequencer.

**Filter picker — daemon-authoritative filter list (follow-up).** The `SwitchFilter` filter picker now sources its filter names from the in-memory `filterWheelLabelsProvider` (the §37.4/§46.2 slot labels). These are app-local defaults until a daemon round-trip exists — there is currently no server endpoint returning the active profile's `FilterWheelSettings.FilterWheelFilters` (nor is the `/api/v1/profile/filter-wheel/labels` round-trip wired). The picker writes a minimal `FilterInfo` (`{$type, _name, _position}`) and the daemon's `SwitchFilter.MatchFilter` re-resolves it to the full profile filter by name, so the stored body is correct regardless; but the *list the user chooses from* should come from the daemon (profile filters, or the connected wheel's slot list via `FilterWheelService.GetAsync`). When that endpoint lands, hydrate `filterWheelLabelsProvider` (or a dedicated filter-list provider) from it and the picker picks it up unchanged.

**Field editor — conditional field relevance.** ✅ **Done** (#542): `InstructionField` gained an optional `enabledWhen(node)` predicate over the field's sibling node, and the editor greys-out + `Semantics(enabled:false)`/`IgnorePointer`-disables a field whose siblings make it inert. Applied to a `TimeCondition`'s Hours/Minutes/Seconds (grey under a sky-event provider) and `MinutesOffset` (grey in Custom-time mode). General mechanism (not a `_ConditionCard` special-case), reusable for any future relevance rule.

Remaining for fuller coverage: (1) **object-valued conditions** — ✅ **done**: `TimeCondition` (#542 — a `SelectedProvider` sky-event/clock-time picker via the `timeProvider` field type, clock H/M/S greyed under a provider) and the altitude conditions `AltitudeCondition`/`AboveHorizonCondition` (#543 — nested `WaitLoopData`: coordinates + comparator + offset, via the `waitLoopData` composite field type reusing the extracted `_CoordinatesEditor`). (2) ✅ **`ReconnectTrigger`** — done: catalogued as `Reconnect Device` with a `SelectedDevice` stringEnum over the grounded device-name set (`reconnectDeviceNames`), rendered via the trigger card. (3) ✅ **`TimeSpanCondition` field clamping** (was #531) — done: `InstructionField` gained `min`/`max`, bounded number/integer fields route through the clamped `_NumField`, and the loop conditions set bounds (TimeSpan minutes/seconds 0–59, hours ≥ 0; Loop iterations ≥ 1).
The §38 execution engine (`SequencerService`, #319) now **runs** sequences for real through NINA's inherited `Sequencer`. These items refine it as real equipment + data models land:

- **Pause / Resume** (deferred in #319). The headless engine has no pause hook (NINA's was WPF-coupled and stripped), so `SequencerService.PauseAsync`/`ResumeAsync` are accepted no-ops today (they deliberately do NOT emit a `paused` event the run wouldn't honor). To honor pause, add an instruction-boundary pause gate to the execution loop — e.g. drive the root container's top-level items through a wrapper that awaits a pause-`TaskCompletionSource` between items, or thread a pause token NINA's containers check. Re-enable the `sequence.paused`/`sequence.resumed` WS events when real suspension works. **Client contract until then:** the endpoints return `202 Accepted` but do nothing, and run-state never reports `Paused` — **WILMA must not expose a pause control** (or must grey it out) so the user isn't misled into thinking a run paused. When real pause lands, surface it to the client (re-enable the button + the `paused`/`resumed` events).
- **Shutdown completed-vs-stopped race** (flagged on #319, accepted). If a run finishes its execution at the exact instant `IHostedService.StopAsync` runs, the run can be marked `Stopped` (and emit `sequence.stopped`) rather than `Completed`. Accepted: it's a one-instant shutdown coincidence, the run did stop, and the §28.2 reconciler keys off the checkpoint (cleared by the worker's `finally`), so post-restart reconciliation is unaffected. A fully race-free fix would need a CAS/lock on the Running→Completed vs →Stopped transition; not worth the machinery for a shutdown-only edge. (The companion null-`Worker` window was *fixed* in #319 — the worker Task is now assigned synchronously in `StartAsync`.)
- **§60.9 WS contract — terminal-without-`started`** (flagged on #319). If an abort/stop arrives in the window between `StartAsync` spawning the worker and the worker executing, the run emits `sequence.aborted`/`sequence.stopped` **without** a preceding `sequence.started` (the started event is intentionally skipped for a run that never entered `Running`). Document in the §60.9 protocol spec that consumers must tolerate a terminal event with no preceding `started`, and ensure WILMA's WS state machine doesn't assume strict `started → terminal` ordering.
- **`frames_total` vs instruction count — API semantic** (flagged on #319). The §38 run-state DTO (`SequenceRunStateDto.FramesTotal`/`FramesCompleted`, predates #319) is populated from the instruction count, but "frames" implies camera exposures, not instructions. Before any external WS consumer ships, decide the contract: either rename to `instructions_total`/`instructions_completed`, or add a separate `frames_*` pair that counts real exposures once the `TakeExposure` capture path lands (a target sequence's "frames" = its exposure instructions). Renaming after consumers ship is breaking.
- **Progress-emit back-pressure** (perf, flagged on #319). The `IProgress<ApplicationStatus>` callback in `SequencerService.RunWorkerAsync` does `_ = EmitAsync("sequence.progress", …)` fire-and-forget — one task per progress tick, no back-pressure. Harmless today (no-equipment instructions don't fire rapid progress), but once equipment-bound instructions report at camera-capture rates this can queue many concurrent publishes. Add a debounce or a single-in-flight guard for `sequence.progress` (lifecycle events stay unthrottled) when the capture path lands.
- **⚠️ Globalization-invariant gotcha (reference).** The AOT server container runs in **globalization-invariant mode** (no ICU). `new CultureInfo("xx")` for any *named* culture throws `CultureNotFoundException` there. Fixed in `OpenAstroAra.Profile/ApplicationSettings.cs` via a `SafeCulture(name)` helper (named culture when ICU is present, invariant fallback otherwise) — this was crashing `new Profile()` and thus the whole sequencer-factory construction in the e2e container (caught by the §38 execution-engine PR #319's `IHostedService` eager construction). **Any future inherited NINA code that constructs a named culture must use the same fallback pattern**, or it will crash the daemon. The server-e2e CI job exercises invariant mode, so such regressions fail CI.
- ✅ **Precise `frames_completed` + `current_instruction_index`** — **resolved in #320.** The worker flattens the deserialized tree to its leaf instructions and counts terminal-status (`FINISHED`/`FAILED`/`SKIPPED`) leaves for `frames_completed` + the first `RUNNING` leaf for `current_instruction_index`, on each `IProgress` tick + a final settle. `frames_total` = leaf count. Exact for linear sequences; looped containers reflect the current iteration (ties into the `frames_total`-vs-instruction-count API item above).
- ✅ **Profile source-of-truth (§14e PR20)** — `StoreBackedProfileService` replaces the `HeadlessProfileService` stub at the DI registration point: one live `Profile` hydrates from `IProfileStore` (profile.json) at startup and on every settings PUT (new `IProfileStore.Changed` event), via the unit-tested `ProfileStoreMapper` (site→astrometry, PHD2→guider, autofocus→focuser, storage→image-file incl. FileType, plate-solve numerics incl. arcsec→arcmin threshold, safety-policy meridian fields). Sections with no NINA-profile counterpart stay store-only (documented per mapping). Multi-profile management remains inert (ARA v0.0.1 is single-profile per §37).
- **Camera capture-block surface** (from #315): `RegisterCaptureBlock`/`ReleaseCaptureBlock` inert + `IsFreeToCapture` hardcoded `true` while `Capture` faults — make consistent with the real capture lifecycle.
- **Real equipment behavior**: the `Headless*Mediator` / `HeadlessDomeFollower` stubs return "not connected" / no-op sentinels; real Alpaca-backed wiring swaps in at §14e, device by device.
  - ✅ **SafetyMonitor REST service (§14e PR3)** — `SafetyMonitorService` (real `ISafetyMonitorService`) connects to a discovered Alpaca SafetyMonitor and reports live `State`/`IsSafe`. The §6 REST surface for SafetyMonitor is now real.
  - ✅ **SafetyMonitor mediator unification (§14e PR4)** — `SafetyMonitorService` now implements `ISafetyMonitorMediator` too (surface in `SafetyMonitorService.Mediator.cs`); Program.cs registers one singleton for both interfaces, so `WaitUntilSafe` reads the live device. `GetInfo()` is the real member (bounded sync `IsSafe` read; mirrors the REST guards; never throws post-Dispose). The mediator `Connect()`/`Disconnect()`/command/event members mirror the stub (connection is driven via REST). **Still TODO per device type** for the other 11.
  - ✅ **§32.4 cache for the device read (PR5)** — `SafetyMonitorService` now runs a background `Timer` that reads `IsSafe` every `RefreshInterval` (2s) while Connected and caches it; `GetAsync` (REST) and `GetInfo` (mediator) serve the cached value under a single lock, with **no per-poll blocking HTTP read** (so no thread-per-poll accumulation). Seeded on connect; refresh guarded against overlap (`Interlocked`) and process-crash (timer-callback catch-all). This unblocks replicating the real-service pattern to the other 11 device types. (Flagged by claude[bot] on #323/#324.)
  - ✅ **ObservingConditions REST service (§14e PR6)** — `ObservingConditionsService` (real `IObservingConditionsService`) connects to a discovered Alpaca ObservingConditions device and serves live §32.4-cached weather sensors. Read-only, REST-only (the weather mediator isn't consumed by any instruction). Second application of the SafetyMonitor template.
  - ✅ **Switch REST service (§14e PR7)** — `SwitchService` (real `ISwitchService`) connects to a discovered Alpaca Switch, serves live §32.4-cached ports, and **writes** a port via `SetValueAsync` (first device control action). REST-only; the `ISwitchMediator` unification (the `SetSwitchValue` sequence instruction) is a follow-up.
  - ✅ **Focuser REST service (§14e PR8)** — `FocuserService` (real `IFocuserService`) connects to a discovered Alpaca Focuser, serves static capabilities + §32.4-cached runtime, and moves via `MoveAsync` (202-Accepted background `Move`, range-validated). REST-only; the `IFocuserMediator` unification (MoveFocuser/autofocus) is a follow-up.
  - ✅ **Rotator REST service (§14e PR9)** — `RotatorService` (real `IRotatorService`) connects to a discovered Alpaca Rotator, serves §32.4-cached runtime (mechanical/sky angle), and rotates via `MoveAsync` (202-Accepted, `[0,360)` validated, mechanical/sky per `UseSkyAngle`). REST-only; the `IRotatorMediator` unification is a follow-up.
  - ✅ **FilterWheel REST service (§14e PR10)** — `FilterWheelService` (real `IFilterWheelService`) connects to a discovered Alpaca FilterWheel, serves slots (read once) + §32.4-cached runtime (current slot; `Position == -1` → moving), and changes slot via `ChangeFilterAsync` (202-Accepted, slot-validated). REST-only; the `IFilterWheelMediator` unification (SwitchFilter) is a follow-up.
  - ✅ **FlatDevice REST service (§14e PR11)** — `FlatDeviceService` (real `IFlatDeviceService`) connects to a discovered Alpaca CoverCalibrator, serves §32.4-cached runtime (cover/light/brightness), and drives the cover + calibrator light via `ApplyFlatPanelAsync` (202-Accepted; brightness-validated). REST-only; the mediator unification is a follow-up.
  - ✅ **Dome REST service (§14e PR12)** — `DomeService` (real `IDomeService`) connects to a discovered Alpaca Dome, serves §32.4-cached runtime (state + azimuth + shutter/home/park flags; `ShutterState` mapped), and drives it via four 202-Accepted ops (`SlewAsync` `[0,360)`-validated, `ParkAsync`, `OpenShutterAsync`, `CloseShutterAsync`) through a shared `RunControl` launcher; `AbortSlew` precedes disconnect. REST-only; the `IDomeMediator` unification is a follow-up.
  - ✅ **Telescope REST service (§14e PR13)** — `TelescopeService` (real `ITelescopeService`) connects to a discovered Alpaca Telescope, serves read-once capabilities (`CanSlew/Sync/Park/Unpark/SetTracking/PulseGuide/FindHome` + sidereal rates) + §32.4-cached runtime (state + RA/Dec + tracking/parked/at-home), and drives it via three 202-Accepted ops (`SlewAsync` goto/sync, RA `[0,24)`/Dec `[-90,90]`-validated; `ParkAsync`; `UnparkAsync`) + two prompt synchronous writes (`SetTrackingAsync`, `AbortSlewAsync` — §57 panic stop); `AbortSlew` precedes disconnect. REST-only; the `ITelescopeMediator` unification is a follow-up.
  - ✅ **Focuser mediator unification (§14e PR14)** — `FocuserService` now also serves `IFocuserMediator` (`FocuserService.Mediator.cs`; one singleton backs both, replacing `HeadlessFocuserMediator`). `GetInfo()` serves the §32.4 cache (never throws post-Dispose); `MoveFocuser`/`MoveFocuserRelative`/`MoveFocuserByTemperatureRelative` drive the live device with a bounded settle-wait and return the final position, so `MoveFocuserAbsolute`/`Relative`/`ByTemperature` execute against real hardware. `ToggleTempComp` writes through. First control-device mediator unification (SafetyMonitor #324 was read-only).
  - ✅ **Rotator mediator unification (§14e PR15)** — `RotatorService` now also serves `IRotatorMediator` (`RotatorService.Mediator.cs`, replacing `HeadlessRotatorMediator`), reusing the hardened focuser move path: `GetInfo()` from the §32.4 cache (never throws post-Dispose); `Move`/`MoveMechanical`/`MoveRelative` drive the live device (sky vs mechanical) with a cancellation+wall-clock-bounded blocking move + settle-wait + direct angle read-back; `Sync` writes through; `GetTarget*Position` normalize to `[0,360)`. `MoveRotatorMechanical` executes against real hardware.
  - ✅ **Dome mediator unification (§14e PR16)** — `DomeService` now also serves `IDomeMediator` (`DomeService.Mediator.cs`, replacing `HeadlessDomeMediator`). Added a read-once caps cache + raw ASCOM shutter capture so `GetInfo()` reports `CanFindHome`/`CanSetAzimuth`/… + the NINA `ShutterState`. `OpenShutter`/`CloseShutter`/`Park`/`FindHome`/`SlewToAzimuth` drive the live device and block on a bounded wait for their terminal condition (returns `true` only when reached). Dome-following (`EnableFollowing`/`SyncToScopeCoordinates`/`IsFollowingScope`) stays stubbed — the `IDomeFollower` subsystem (§38k-21) is separate. `OpenDomeShutter`/`CloseDomeShutter`/`ParkDome`/`FindHomeDome`/`SlewDomeAzimuth` execute against real hardware.
  - **Guider follow-ups (§63, from the guider-a #345 self-review):** (1) ✅ **RMS** — *done in guider-a*: `GuiderService` accumulates raw RA/Dec errors from the `GuideEvent` stream into a bounded 200-step window and `GetAsync` reports `RmsTotal/Ra/Dec` (root-mean-square, pixels). Possible refinement: also expose arcsec via `PixelScale`. (2) **Terminal-status surface for guide ops**: `StartGuiding`/`StopGuiding`/`Dither` are fire-and-forget `Task.Run` returning 202, so a `Dither` rejected because the guider isn't yet GUIDING (returns false, no exception) is indistinguishable from success to a polling client — the proper fix is a §60.9 WS event (`guider.dither.{complete,rejected}` etc.) once the WS guider channel lands, not a synchronous result. (3) **Arg-passing via shared profile**: `ConnectAsync`/`DitherAsync` write Host/Port/DitherPixels onto the singleton `ActiveProfile.GuiderSettings` that `PHD2Guider` reads asynchronously — racy under *concurrent* guider requests, but mitigated by §27 single-client for v0.0.1; a cleaner design passes these as method args when the guider mediator unification (guider-c) reworks the surface. (4) **`IGuiderMediator` unification** (guider-c) replacing `HeadlessGuiderMediator` so the sequencer's StartGuiding/StopGuiding/Dither drive the live guider.
  - **Remaining device services**: Camera/Guider/PolarAlign REST services are still `Placeholder*` (202-Accepted no-ops) — Camera gates on the image pipeline (§2105), Guider on PHD2 (§63), PolarAlign on camera+plate-solve orchestration. **Mediator follow-ups: complete.** Telescope (#337), Switch (#338) and FilterWheel (#339, with the device→profile filter import) landed; `IFlatDeviceMediator`/`IWeatherDataMediator` have **no consuming sequence instruction** in the port (the only `IFlatDeviceMediator` consumers are the Connect-capstone instructions, which call the inert `Connect()`/`Disconnect()` lifecycle members) so their headless stubs are the correct, final wiring until a flat-wizard/connect orchestration ships.

## §26 / §2105 OpenCvSharp4 — version-pin BLOCKER (found 2026-06-10)

The playbook §26.2 pins all five OpenCvSharp4 packages at `4.11.0.20250507`. **That pin is invalid** — verified against NuGet (2026-06-10):
- `OpenCvSharp4` (managed): `4.11.0.20250507` exists; latest is `4.13.0.20260602`.
- `OpenCvSharp4.runtime.linux-arm64` (the **production Pi target**): tracks the managed package (4.13.x current); **does NOT have `4.11.0.20250507`** — its versions use a different sequence.
- `OpenCvSharp4.runtime.linux-x64` (CI host + x64 docker): **frozen at `4.10.0.20240717`** (no updates since 2024).
- `OpenCvSharp4.runtime.osx`: **does not exist**; `OpenCvSharp4.runtime.osx_arm64` stops at `4.8.1`.

OpenCvSharp4 requires the native-runtime OpenCV version to match the managed binding, so **there is no single version that works across linux-arm64 + linux-x64 + osx-arm64**. Consequences/decisions needed (revises §26.2 — user-authoritative):
1. **Version strategy**: pin to a managed+linux-arm64-aligned version (production works) and accept linux-x64/osx native are stale/absent — meaning OpenCvSharp4 code can't be unit-tested on the macOS dev box or the linux-x64 CI host, only on linux-arm64 (e.g. via the §14d qemu-arm64 docker e2e). OR find an older common version (~OpenCV 4.10) where main + linux-x64 + linux-arm64 all align, trading off currency.
2. **csproj**: the §26.2 "5 runtime refs at one version" block must change to reality (per-platform versions, or arm64-only).
3. This gates the whole §2105 render-stub migration (`RenderedImage`/`BaseImageData`/`ImageArrayExposureData`/`ExposureData` — ~10 `NotImplementedException("pending OpenCvSharp4 wiring")` methods) + the separate libraw RAW path. Note much of preview/thumbnail/stretch is already served by the §65 SkiaSharp pipeline; the unique consumer of the in-memory OpenCvSharp render path is Live View (§64), whose v0.0.1 scope is itself an open question.

### §2105 follow-up (2026-06-10, hard NuGet data): OpenCvSharp4 runtimes are FUNDAMENTALLY unalignable — consider SkiaSharp instead

Verified there is **no single OpenCvSharp4 version whose native runtimes exist for all of linux-arm64 + linux-x64 + osx-arm64**:
- `runtime.linux-arm64`: only modern (4.11.x+/4.13.x) — no 4.8.x, no 4.10.x.
- `runtime.linux-x64`: only `4.10.0.20240717` — no 4.8.x, no 4.11+.
- `runtime.osx_arm64`: only `4.8.1` (stops 2023).
- `runtime.win`: `4.8.0.x` + others.

OpenCvSharp4 requires the native runtime's OpenCV version to match the managed binding, so OpenCvSharp4 code **cannot native-load on the macOS-arm64 dev box AND the linux-x64 CI host AND the linux-arm64 Pi with one pin** — it's only runnable on linux-arm64 (slow qemu docker, not a unit-test env). Writing/iterating the §2105 render stubs against an un-runnable backend is a poor foundation for "the single biggest technical risk."

**Recommendation: reconsider §26's OpenCvSharp4 choice in favor of SkiaSharp for §2105.** SkiaSharp is already a proven, cross-platform-clean dependency in this repo (the §65 stretch/preview pipeline, #208 — `SKBitmap`/`JpegEncoder`, works on dev + CI + arm64). The §2105 render needs (16→8-bit, JPEG/thumbnail encode, resize, basic transforms) are all SkiaSharp-native; the one OpenCvSharp-specific op is **debayering** (`Cv2.CvtColor(BayerRG2RGB)`), which can be done with a small bilinear-debayer kernel over the raw `ushort[]`/`byte[]` buffers (or via libraw for DSLR RAW). This would un-stub `Image/ImageData/*` (`RenderImage`/`Debayer`/`RenderBitmapSource`/`GetThumbnail`/`Stretch`) on a testable-everywhere backend and avoid the OpenCvSharp packaging trap entirely. **Revises §26 (user-authoritative) — flag for decision.** If Live View (§64) is deferred to v0.1.0, most of this can be deferred with it (the §65 SkiaSharp preview already serves the catalog).

## §15 / §17.2 dependency-license audit (2026-06-10) — RELEASE-SAFE, no copyleft contamination

Audited all NuGet PackageReferences across the solution for MPL-2.0-distribution compatibility:
- **Distributed (daemon) deps**: all permissive (MIT / Apache-2.0 / BSD / MS-PL / ISC / public-domain) EXCEPT two inherited-from-NINA non-MIT ones, both compatible: **Accord.NET (LGPL-2.1)** — separate dynamically-linked .NET assemblies, satisfies LGPL's relink allowance; and **FreeImage** (VVVV.FreeImage) — used under its permissive **FIPL** arm (FIPL-or-GPL dual). **No GPL/AGPL/SSPL/commercial-licensed code in the distributed daemon.** Both now called out in `NOTICE.md`.
- **Test-only deps with license caveats (NOT distributed)**: `FluentAssertions` is pinned `[7.0.0]` — deliberately the last Apache-2.0 version before v8 went commercial (Xceed paid); `Moq 4.20.72` is post-SponsorLink. Both fine + test-only.
- **Action remaining (release-time only)**: generate `3rd-party-licenses.txt` from the final package graph when the .deb pipeline lands (per §15) — this audit confirms it won't surface a blocker.

## §65 JpegEncoder.EncodeThumbnail null-resize guard (2026-06-10, from PR #349 review)

`JpegEncoder.EncodeThumbnail` (grayscale, pre-existing since #208) calls `srcBitmap.Resize(...)` and passes the result straight to `SKImage.FromBitmap` with no null check. SkiaSharp's `Resize` returns `null` under memory pressure / on an unsupported size, which would NRE rather than surface a clear error. PR #349 added the same guard to the new `EncodeColorThumbnail` (`?? throw InvalidOperationException`); the grayscale twin should get the identical guard in a small follow-up (out of #349's OSC-debayer scope). Low severity — only triggers under allocation failure.

## Client "active server" selector — servers.last is ambiguous with multiple saved servers (2026-06-10, from PR #350 review)

The client picks the server for API calls via `savedServersProvider` → `servers.last` (insertion order). This is the **existing codebase convention** — `lib/state/settings/equipment_connection_state.dart:99` does `ProfileApi(servers.last)`, and PR #350 mirrored it in `last_frame_state.dart` + `imaging_tab.dart`. With >1 saved server this targets the wrong host. v0.0.1 is effectively single-server, so it's latent. Proper fix: introduce a canonical "active/current server" provider and route ALL per-server API construction through it (equipment connection, profile, frames, exposure, …) in one pass — out of scope for a single feature PR. Track here until that app-wide change lands.

## §63.3 guider-d follow-ups (deferred from the crash-recovery PR, 2026-06-11)

guider-d landed the §63.3 crash-detection + auto-restart core: `IGuiderProcessSupervisor` (systemctl seam over the `openastro-phd2` unit) + `GuiderRecoveryCoordinator` (backoff decision tree), triggered from `GuiderService.OnConnectionLost`. Deliberately scoped OUT (each a clean follow-up):
- **Active hang-detection poll** (§63.3 "3 consecutive RPC failures → down"): a periodic `get_app_state` ping (every 10 s idle / 2 s guiding) to catch a *hung* daemon whose socket is alive but RPC is unresponsive — the current trigger is the passive socket-drop event (`PHD2ConnectionLost`), which catches the common "process died" case but not a wedged-but-connected daemon. Needs a public ping on `PHD2Guider` + a poll loop in `GuiderService`.
- **ARA client auto-reconnect after recovery**: once the supervisor reports the unit back `active`, recovery currently emits "reconnect to resume" and stops — it does not re-establish ARA's own PHD2 client connection. Auto-reconnect intertwines with the connect-generation logic; safer as its own change.
- **§42.2 mid-sequence fault flow**: pause the running sequence at a safe point on a mid-guiding guider crash (vs. the current notify-only). Belongs with the sequencer fault-handling work.

### §63.3 guider-d additional deferrals (from the #351 review, 2026-06-11)
- **`_recovering` stale-flag race (ultra-narrow).** If `ConnectAsync`/`DisconnectAsync` cancels a recovery pass, the background task is still unwinding (OCE caught, `finally` not yet run). If the new connection then dies and `OnConnectionLost` fires before that `finally` clears `_recovering`, `BeginRecoveryLocked` skips starting a fresh recovery. Window doesn't close on its own but is extremely unlikely (old task is at a catch/finally boundary). Low priority.
- **`inactive` nudge on deliberate admin stop.** A manual `systemctl stop openastro-phd2` drops the socket → `OnConnectionLost` → recovery → sees `inactive` → nudges a restart (fighting the admin's intent). Acknowledged in-code; only reachable from an unexpected drop. Add a guard (e.g. skip the nudge for a clean `inactive` with no recent crash signal) only if silent auto-restart there proves undesirable in the field.
- **Restart permission-failure is silent.** `RequestRestart` is fire-and-forget (mirrors §13), so a `systemctl restart` rejected for missing NOPASSWD/polkit exits non-zero with nothing logged. If a "restart was rejected" signal is wanted, make it await the child + log Warning on non-zero exit (changes the seam from `void` to `Task`).

## Parked blockers (2026-06-11) — recorded per "note physical blockers + keep going"

**Physical / hardware (can't do autonomously — revisit with hardware):**
- **v0.0.1-ara.1 release tag + RPi smoke test** (Phase 15 terminus) — needs a physical Pi + user authority to tag. The autonomous terminus.
- **Live guider integration tests** (guider-e push, guider connect/guide RMS) — `openastro-guider` was NOT on the LAN at scan time (subnet 192.168.1.0/24 had no :4400; only an unrelated Virata-EmWeb device on :8080). Unit-test mappers/serialization; run live integration when the guider is back up.
- **Mount-dependent flows** (slew/track/polar-align loop, dome) — testable against the ASCOM OmniSim, but real-sky polar-align needs a mount + sky. Build + sim-test; defer on-sky verification.

**Code-blocked (buildable, sequenced):**
- **guider-e §63.5 param push** is blocked on ARA's profile lacking the §63.5 guider-config fields — `IGuiderSettings` only has PHD2 host/port/scale/history/ROI/profile-id, NOT guide focal length / pixel size / RA-Dec aggressiveness / min-motion / dec-guide-mode / calibration. Prereq: extend `IGuiderSettings` + the §37 profile section + AraJsonContext + the Flutter wizard Screen 10, THEN push (the guider-e-1 named-object RPC classes are ready). Tracked as task: "guider-e profile-model extension + §63.5 param push".

**Tracking correction:** §38.7 disk-shipped starter templates (lrgb-dso/narrowband-shoo/comet) are **DONE** (real NINA-schema bodies in `OpenAstroAra.Server/templates/` + csproj Content publish entry + `StarterTemplateTest`), despite the stale "Future §38 sub-PRs" listing.

## §2105 in-memory render — remaining stubs (2026-06-11, after PR1-3)

SkiaSharp decision made (§26 revised) + the thin §65-wrapper stubs un-stubbed and merged:
RenderBitmapSource + RenderImage (#354), GetThumbnail + ReRender (#355), Stretch via a new
parameterized `Stretcher.Stf` (#356). Also fixed a real §65 AutoStf over-brightness bug (median
was landing ~0.65 instead of 0.25) caught in #354's review — improves every preview.

Remaining §2105 stubs (each a meatier follow-up, all still dead code until Live View §64):
- ✅ **`RenderedImage.Debayer(...)` → `IDebayeredImage`** — DONE (#357). Full-res bilinear kernel
  (`Stretcher.Debayer.Bilinear`) + `DebayeredImage : RenderedImage, IDebayeredImage` producing `LRGBArrays`.
- ✅ **`RenderedImage.DetectStars(...)` → star detection** — DONE (#358). From-scratch `StarDetector`
  (median+MAD background → median+k·σ threshold → optional 3×3 median pre-filter → 8-connected flood-fill
  blobs → flux-weighted centroid + Half-Flux-Radius; rejects noise specks / edge-truncated / saturated /
  frame-spanning blobs; `MaxNumberOfStars` cap brightest-first). Pure managed code (honours the §26 no-OpenCvSharp4
  decision). The `IStarDetection`/`IStarAnnotator` interfaces in `HeadlessStubs.cs` stay as the (unused) DI seam.
- ✅ **`RenderedImage.UpdateAnalysis(...)`** — DONE (#358). Publishes HFR/HFRStDev/DetectedStars/StarList
  onto `RawImageData.StarDetectionAnalysis` (flows into the FITS HFR/StarCount pattern keys).
  - **Annotation still deferred:** `DetectStars(annotateImage: true)` is a documented no-op (logs a Debug line) —
    drawing the star overlay onto the rendered buffer needs a §2105 annotator (SkiaSharp draw path). Not on the
    v0.0.1 critical path (annotation is a Live-View/§64 nicety); wire it when Live View lands.
  - ✅ **Atomic analysis publish + thread safety:** DONE (#358 round-7/8). `IStarDetectionAnalysis.SetAll(...)`
    writes all four backing fields under a lock (full memory barrier + torn-read safety for the doubles on
    32-bit ARM) before raising any `PropertyChanged` outside it, so a §59-autofocus / Live-View observer on
    another thread always reads a consistent, visible view. `UpdateAnalysis` uses it. +atomicity test.
  - **Background sample is column-aliased (minor, post-v0.0.1):** `StarDetector.BackgroundStats` strides the
    subsample linearly from index 0, so it samples a few fixed columns across all rows. On strongly vignetted
    frames (dark corners) this can bias the median/MAD and thus the detection threshold. A 2D scattered-grid
    sample (step in both x and y) would be more representative. Not critical for v0.0.1; the threshold is robust
    on typical flat-ish backgrounds. (#358 round-8 review.)
- **`BaseImageData.RenderBitmapSource` Bayered note**: renders the raw mosaic (grey CFA) until the render path
  calls Debayer for OSC display (the data path exists as of #357; the display wiring is Live-View-gated).
- Also still stubbed (lower priority, libraw/DSLR): `ExposureData.CreateRAWExposureData`, `BaseImageData.SaveTiff`,
  `BaseImageData.FromFile` (non-FITS/XISF), `ImageArrayExposureData.FromBitmapSource`.

**§2105 in-memory render is otherwise COMPLETE (#354–#358)** — only libraw RAW decode + on-image star
annotation remain, both Live-View-gated (v0.1.0 scope).

## §59 autofocus — follow-ups (2026-06-11, after the curve-fit sub-PR #359)

`FocusCurveFit.FitParabolic` (`OpenAstroAra.Core/Model/FocusCurveFit.cs`) landed the §59.8 parabolic fit
(weighted LS → best-focus vertex + R² + usable/in-range flags) on #358's HFR. Remaining §59 work:
- ✅ **Hyperbolic fit + §59.8 fallback selection:** DONE (#360). `FocusCurveFit.FitHyperbolic`
  (`HFR = √(a² + b²·(x−c)²)`, linearised as a parabola in HFR² so it reuses the conditioned solve) +
  `FitBest` (parabola, fall back to hyperbola when R² < 0.85 / unusable, keep the higher-R² usable fit).
  Note: a clean *symmetric* curve is fit by a parabola at R² ≈ 0.93, so the fallback fires only on genuinely
  messy data — documented + tested.
- ✅ **Trendline fit:** DONE (#361). `FocusCurveFit.FitTrendlines` — split at the min-HFR sample into two
  arms, weighted-LS line per arm, intersect for best focus. Robust on rounded/flat-bottomed curves (reads
  the straight arms, not the vertex curvature). Note: the combined TREND*PARABOLIC / TREND*HYPERBOLIC enum
  variants (which average a trendline estimate with a smooth-curve estimate) are not implemented — low value
  over the three standalone fits; revisit only if a profile needs them.
- **Live focuser V-curve sweep (§59.8 / §59.3 Phase-1 calibration) — PHYSICAL BLOCKER (no focuser on the rig):**
  the orchestration that steps a connected Alpaca focuser through 9 positions, captures + runs `StarDetector`
  HFR at each, feeds `FocusCurveFit`, moves to the predicted best, and verifies. Needs a focuser to live-validate;
  the user's Alpaca server is camera-only. Build the orchestration against `IFocuserMediator` (already unified,
  #14e PR14) + the capture path, but defer end-to-end validation. Backlash auto-discovery (§59.7) rides on this.
- **Smart Focus (§59.2–59.4):** the feature-vector model (donut diameters, asymmetry, telescope-type extractor)
  + inverse-mapping calibration table. Research-grade "better than NINA" feature — likely v0.1.0, not v0.0.1.
- **AF triggers + sequencer wiring (§59.5):** sequence-start / temp-Δ / HFR-drift / post-flip / first-filter
  triggers consulting `/diagnostics/current` (§59.9). Sequencer-integration work, gated on the live sweep.

### §2105 PR4 Debayer — ✅ DONE (shipped in #357)
Full-resolution bilinear debayer landed in PR #357: `OpenAstroAra.Stretch/Debayer.Bilinear(mosaic, w, h, pattern)`,
`IDebayeredImage`/`DebayeredImage`/`LRGBArrays`, and `RenderedImage.Debayer(...)` wired (Lum = weighted RGB). Dead
code until Live View (§64). (Entry was left in "ready to implement" state; corrected here.)

## §58 meridian flip — follow-ups (2026-06-11, after the trigger re-port #362)

`MeridianFlipTrigger` decision logic re-ported headless (#362) from `840893eb8^`, behind the new
`IMeridianFlipExecutor` seam. Remaining §58 work:
- **§58.4 flip orchestration (the executor impl):** replace `PlaceholderMeridianFlipExecutor` (throws) with the
  real headless workflow — pause imaging → wait out `pause_after` → flip slew (`ITelescopeMediator`) →
  plate-solve re-center (§28.2 tolerance, ≤3 retries) → optional re-focus (`refocus_after_flip` policy, §59
  AF) → restart guiding (`IGuiderMediator`, auto-restore vs full recal) → §58.5 side-of-pier verification. Its
  own sub-PR; needs telescope/camera/guider/focuser mediators + plate-solver. Move the `Recenter`/
  `AutoFocusAfterFlip` precondition checks (currently dropped from the trigger's `Validate`) here.
- **Side-of-pier projection test matrix:** the original NINA test had a large `[TestCase]` matrix exercising
  `ShouldTrigger` with `UseSideOfPier=true` + projected `ExpectedPierSide`. Re-port deferred — it needs
  `TelescopeInfo.Coordinates`/`SiderealTime` fixtures that yield a known expected pier side. The #362 tests
  cover the no-pier timing decision + early-return guards + Validate.
- **§58.6 profile schema gaps:** the playbook `meridian_flip` block has fields ARA's `IMeridianFlipSettings`
  doesn't expose yet (`mode`, `max_wait_after_min` naming, `recenter_after_flip`, `refocus_after_flip` enum,
  `guider_recal` enum, `skip_target_if_below_floor`, `first_flip_confirmed`). Map when the orchestration lands.

### §58 trigger — minor follow-ups (from #362 review)
- **Vernal-equinox zero-coordinate heuristic:** `MeridianFlipTrigger.Execute` treats a flip target of exactly
  `RA == 0 && Dec == 0` as "unset" and substitutes the mount's current position (inherited from NINA). This
  misfires for a real object near the vernal equinox (RA 0h, Dec 0°) — spurious warning + a wrong flip
  coordinate. Low likelihood; if it ever matters, gate the heuristic on a tolerance or an explicit "unset" flag.

## §18.I plate-solving — integration map + slices (2026-06-11)

The solver backends (ASTAP/Platesolve2/3) + algorithms (`ImageSolver`/`CaptureSolver`/`CenteringSolver`) were
ported in §0.5 but **never wired into a callable service** (no `ICenteringSolver`/`IImageSolver` caller existed).
Wiring started:
- ✅ **`PlateSolveService` (§18.I sub-PR 1, #363):** `IPlateSolveService.SolveImage(image, approxCoords, …)` —
  builds the configured solver via `IPlateSolverFactory` (now DI-registered, `PlateSolverFactoryProxy`) +
  assembles `PlateSolveParameter` from the active profile (focal length, pixel size, search/downsample). Image-in
  → solution-out core, unit-tested with a mocked `IImageSolver`. DI: `IPlateSolveService` + `IPlateSolverFactory`.
- **Next slices:** a REST solve endpoint (`POST /api/v1/platesolve/solve` against a captured frame id); the §28
  **centering loop** (`CenteringSolver`: capture → solve → sync → re-slew); then the §58.4 flip recenter consumes it.

**ASTAP backend (org fork `github.com/open-astro/ASTAP`, see [[reference-astap-fork]]) — integration is CLI, no API:**
`ASTAPSolver` (a `CLISolver`) shells out to the `astap_cli` binary at the profile's `ASTAPLocation` with
`-f/-fov/-r/-ra/-spd/-z` and reads the `.ini`/`.wcs` output — so **no ARA code change** is needed to integrate.
The work is OPS: build the headless `command-line_version/astap_command_line` from the fork via
`lazbuild -B astap_command_line_linux_aarch64.lpi` (Raspberry Pi deploy; `debian_package_scripts/` → a `.deb`)
or `…_darwin_M1.lpi` (Mac dev), install a star DB, and set `ASTAPLocation`. Needs Lazarus+FPC to build.

### plate-solve — blind-solver fallthrough (from #363 review)
`PlateSolverFactory.GetBlindSolver` maps `BlindSolver.AstrometryNet` → ASTAP (AstrometryNet was removed per
§18.I "local solvers only"). Since `PlateSolveService` always calls `GetBlindSolver`, a profile still configured
for AstrometryNet silently uses ASTAP as both primary + blind solver. Harmless (ASTAP is the intended backend),
but the profile/wizard should drop the AstrometryNet blind option, or the factory should log the substitution.

### plate-solve LoadImageDataAsync null detection deps (from #364 round-2)
`SqliteFrameRepository.LoadImageDataAsync` passes the real `IProfileService` to the `BaseImageData` but leaves
`starDetection`/`starAnnotator` `null!`. Safe for the solve path (CLISolver → `SaveToDisk` → `GetImagePatterns`
reads the `StarDetectionAnalysis` property, not the service; `RenderImage()` is never called). When the §28
centering loop / on-image annotation calls `RenderImage()` or `DetectStars` on a repo-loaded frame, wire it
through a DI-registered `IImageDataFactory.CreateBaseImageData` (the concrete factory lives in BaseImageData.cs
but isn't registered yet) instead of the `null!` ctor args.

  - **Why not just inject IImageDataFactory now (#364 round-3):** the concrete `ImageDataFactory`
    (`BaseImageData.cs`) needs `IPluggableBehaviorSelector<IStarDetection>` + `<IStarAnnotator>`, and neither
    those selectors nor any `IStarDetection`/`IStarAnnotator` impl is registered in the headless Server DI yet
    (star-detection behaviour selection was never wired headless). So the clean factory path needs that
    selector chain built first — until then the `null!` (safe for the solve path) stands.

  - **Concrete guard option (#364 round-5):** when the §28 centering loop (the first consumer that would call
    `RenderImage()`/`DetectStars()` on a repo-loaded frame) lands, add a guard to `BaseImageData` — e.g. throw
    `NotSupportedException` from `RenderImage`/`DetectStars` when constructed without star services — so a
    missing-star-service frame fails loud + local instead of an opaque NRE. (Out of scope for the solve
    endpoint, which never calls those.)

### §58.9 unattended meridian-flip safety layers (from §58.4 executor)
The §58.4 `MeridianFlipExecutor` (Server) ships the core recovery sequence — stop guiding → pass meridian →
flip slew → recenter → resume guiding → settle → §58.5 side-of-pier check — faithfully ported from NINA's
`MeridianFlipVM.DoMeridianFlip`. Its failure path is NINA's (restore tracking + resume guiding + halt the
sequence). The enhanced **§58.9 four-layer unattended-safety pipeline** is a deliberate follow-up:
- **Layer 1 — pre-flight flight check** (~2 min before flip): endpoint-altitude prediction (skip target if
  below the hard floor), mount-health probe, required-equipment-connected check (+ §42.3 hot-reconnect), sane
  predicted slew duration.
- **Layer 2 — in-slew watchdog**: sample mount state every 5 s — abort on stalled position (15 s no-change),
  hard timeout (min of 3× predicted / 5 min), Alpaca fault events, or unchanged pier side after `IsSlewing`
  clears. The executor currently fails the flip only when `telescopeMediator.MeridianFlip` returns false/throws.
- **Layer 3 — post-flip verification gate**: the recenter is currently *best-effort* (a failed solve logs +
  continues, matching NINA). §58.9 makes it a HARD gate (imaging does NOT resume on solve failure; solved
  position must be within ±2° of intent).
- **Layer 4 — safe-rest state on any failure**: park (if `CanPark`) else stop tracking + stop guider; §35.5
  looping alarm audio. The executor instead re-enables tracking (NINA behaviour) to keep the target.

Also follow-ups: **§58.7 failure notifications** (the executor logs via `Logger`; wiring `INotificationService`
for the slew-retry/solve-fail/guider-recal-fail Critical/Urgent notifications is deferred to avoid coupling the
unit-testable core to the notification surface), **§58.8 first-flip 60 s confirmation**, **§58.10 unattended-hours
severity escalation**, and the **re-focus-after-flip** step (gated on the live focuser AF V-curve sweep, itself a
focuser-hardware blocker). The §58 profile block (`refocus_after_flip` policy, `guider_recal` mode,
`first_flip_confirmed`) is not yet on ARA's profile model — these land with the profile-schema extension.

### IDomeFollower.TriggerTelescopeSync has no cancellation token ✅ DONE (2026-06-12)
`IDomeFollower.TriggerTelescopeSync` now takes a `CancellationToken` (matching `WaitForDomeSynchronization(token)`
on the following path), threaded from the caller's token at all three call sites (`MeridianFlipExecutor.SynchronizeDome`,
`CenteringSolver`, and the `SynchronizeDome` sequence item) so a non-following dome sync is cancellable mid-slew.
`HeadlessDomeFollower` (the only impl) ignores it (no-op stub); a real dome-following impl will honor it.

### §39 calibration — dark matching by temperature ✅ DONE (2026-06-12)
`SqliteCalibrationService.MatchingDarksAvailable` now matches darks to lights by `(exposure_seconds, gain,
ROUND(temperature_c, 0))` — temperature bucketed to the nearest whole degree. `temperature_c` is `NOT NULL`
(an uncooled camera records the 0.0 sentinel that `CameraService` coalesces a missing CCD temperature to), so
uncooled lights/darks still bucket-match. +3 tests (temp-mismatch rejects, within-degree bucketing, uncooled
sentinel). Remaining §39 follow-up: the matching-flats generation returns a PLAN (one step per light filter);
enqueuing it as a runnable §38 flat sequence is still a separate follow-up. Flats match by `filter` only (correct).

### §39 calibration — ListSessions is O(N) queries per page (from #370 review)
`SqliteCalibrationService.ListSessionsAsync` runs `BuildSessionDtoAsync` per session = 4 queries each (header,
per-filter, flats EXCEPT, darks EXCEPT). A 50-session page ≈ 201 queries. Acceptable at v0.0.1 scale over the
embedded SQLite file (in-process, sub-ms each → tens of ms for a page), but a catalog with hundreds of nights
would benefit from batching the per-filter + coverage queries via `IN ($ids)` / a single GROUP BY pass and
assembling the DTOs in code. Defer until catalog sizes warrant it. The cursor is also a plain integer OFFSET (same pattern as the §28 frame repo), so a session inserted between page fetches can repeat/skip a row; the eventual keyset-pagination migration over captured_utc covers both.

## §63.5 guider-e-2 — follow-ups (2026-06-11, from the #372 push review)
The §63.5 on-connect push (`PHD2Guider.GuiderEngineConfig.cs`, #372) shipped the core map-and-push; two
review-surfaced refinements are deferred (neither blocks correctness — both are "make a future config option
expressible"):

- **Per-axis minimum-move.** `BuildGuiderEngineConfigMessages` pushes the single `IGuiderSettings.MinimumMove`
  to *both* RA and Dec (`set_algo_param {axis, "minMove"}`). PHD2 itself keeps independent RA/Dec min-move, and
  a backlash-heavy Dec axis often wants a larger value than RA. Splitting ARA's `MinimumMove` into
  `RAMinimumMove` / `DecMinimumMove` (profile + DTO + editor + the two push lines) is the clean extension —
  deferred until there's a concrete ask, since the shared value is a sane default and the wire path already
  sends per-axis messages (only the source field is shared).

- **`DisconnectPHD2Equipment()` has no `CancellationToken`.** `PushGuiderEngineConfigAsync(ct)` threads `ct`
  through every `SendMessage` and the per-message loop, but the `set_profile_setup` pre-disconnect calls
  `DisconnectPHD2Equipment()` (inherited signature, no token), so a Connect cancelled *during* that disconnect
  won't observe the cancel until the next `ThrowIfCancellationRequested`. Bounded (disconnect is fast +
  best-effort, wrapped so a failure can't abort Connect), but threading a token through `DisconnectPHD2Equipment`
  would make the cancel prompt. Inherited-interface change → its own follow-up, not the push PR.

## §63.4 guider-e-3 — profile-name mapping: shipped + deferred (2026-06-12)
**Shipped:** e-3a (#375) — the `Phd2CreateProfile` / `Phd2SetProfileByName` RPC classes + the pure
`PHD2Guider.AraGuiderProfileName` slug helper. e-3b (this PR) — the connect-path wiring:
`ResolveAraProfileSelection` (pure, unit-tested) + `EnsureAraGuiderProfileAsync` select-or-create on connect,
honoring the `PHD2ProfileId` override precedence and pushing into the right profile before the §63.5 params.
(Note: the earlier "PHD2Guider.cs is ISO-8859-1, edit with perl" worry was **wrong** — `PHD2Guider.cs` is clean
UTF-8 (its copyright uses ASCII `(c)`, de-mojibaked in Phase 3); only `PHD2Methods.cs` still carries the © and
needs byte-preserving edits. The connect-path swap used the Edit tool safely; md5 of line 4 verified unchanged.)

**Still deferred (e-3c / v0.1.0):**
- **Slug-collision disambiguation.** ✅ DONE (guider-e-3c) — the connect path now uses the id-suffixed
  `AraGuiderProfileName(name, profileId)` → `ara-<slug>-<id8>` (first 8 hex of the ARA profile's stable Guid),
  so same-slug profiles (`"C-14"`/`"C 14"`, or anything collapsing to `ara-default`) get distinct PHD2 profiles.
  Chose the deterministic id-suffix over the PORT_TODO sketch's "store the resolved `PHD2ProfileName` back on the
  ARA profile" because the Equipment-layer guider has read-only `IProfileService` and can't persist a profile
  mutation back through `IProfileStore` — the id-suffix needs no stored state. +tests.
- **Slug length cap (from #375 review).** No length cap; the daemon docs don't state a max profile-name length,
  so a cap would be a guess (and truncation reintroduces the collision above). Find the real openastro-guider
  limit (read the daemon source / test an over-long `create_profile`) and cap there if one exists, paired with
  the disambiguator so truncated names stay unique.
- **Send-time validation done; copy-source still latent.** e-3b guards the empty name at the send site (and the
  resolver never yields one). ARA never sets a clone source, so `create_profile`'s mutually-exclusive
  `copy_from`/`copy_from_id` stay unset by construction — if a future caller ever sets a copy source, enforce
  at-most-one there (still not a `Debug.Assert` on the DTO — that would throw during deserialization).
- **Profile lifecycle (§63.4 table) beyond connect-time select/create:** `delete_profile` on ARA-profile delete,
  `clone_profile` on ARA-profile clone, `rename_profile` on rename — likely v0.1.0 (need ARA profile-lifecycle
  hooks that don't exist headless yet).

## §29 disk-space monitor — follow-ups (2026-06-12, after the warn-only monitor)
The §29 active disk-space gap is now closed by `DiskSpaceMonitor` (a `BackgroundService` that warns via a §51
diagnostic + the §54 `OnDiskSpaceLow` notification when the save volume runs low; opens one issue on a downward
transition, clears it on recovery via the new `IDiagnosticsService.ClearOpenEventsByTypeAsync`). Deferred:

- **Configurable thresholds.** ✅ DONE — `StorageSettingsDto.MinFreeDiskWarnGb` / `MinFreeDiskCriticalGb`
  (optional, back-compat defaults 10/2), surfaced under Settings → Session → Storage; the monitor reads them
  live each tick via the pure `ResolveThresholdBytes` (falls back to the 10/2 GiB defaults on a non-positive or
  inverted pair). Client + server validate critical < warn.
- **Hard-stop option (§29.x).** ✅ DONE — `SafetyPoliciesDto.OnDiskSpaceCritical` (`warn` default / `abort`),
  surfaced under Settings → Safety → Policies. On entering Critical with `abort`, the monitor calls the new
  `ISequencerService.AbortActiveRunsAsync` to halt running sequences + fires a critical notification. ("Pause"
  was considered but the headless engine's `PauseAsync` is still a deferred no-op, so only warn/abort ship; add
  "pause" when the engine grows a real pause hook.)
- **Per-frame pre-capture check.** The monitor polls on an interval (60 s); a burst of large frames could fill
  the disk between ticks. A cheap pre-capture free-space check in the capture path (warn/skip) would tighten the
  window — fold into the §42.2 capture-path hardening rather than the standalone monitor.

## §63.6 guider-e-4 — dark-library push (2026-06-12)
**Shipped:** e-4a — named-object RPC request classes (`PHD2Methods.DarkLibrary.cs`): `build_dark_library`
(`frame_count` 1..50 def 5, optional `min/max_exposure_ms`, `clear_existing`, optional `notes`, `load_after`
def true), `set_dark_library_enabled {enabled}`, `get_calibration_files_status` (no params),
`delete_calibration_files {delete_dark_library?, delete_defect_map?}` — serialization-locked (+5 tests).

**Shipped:** e-4b-1 — guider-client invocation (`PHD2Guider.DarkLibrary.cs`): `BuildDarkLibraryAsync` +
`GetCalibrationFilesStatusAsync`, typed responses (`Phd2BuildDarkLibraryResult`, `Phd2CalibrationFilesStatus`),
and **send-site validation** (`BuildDarkLibraryRequest`: reject `FrameCount` outside 1..50, reject exposure
bounds outside 1..600000 ms, reject `min > max`) → clear error before the socket, not the daemon's opaque
`-32602`. `GuiderRpcException` carries the failing method/code for the service layer. +13 tests (validation +
response deserialization). Blocking build uses a 2-h `SendMessage` timeout (worst realistic build
50 frames × ~5 exposures × long exposure ≈ 83 min, so 30 min would time out mid-capture).

**SCOPE CORRECTION (e-4b-1 finding):** `build_dark_library` is a **blocking RPC with NO progress events** — the
guider event stream (GuideStep/Settling/SettleDone/StarLost/Alert/… per §63.2) carries no dark-library-progress
event, so the §63.8 "PHD2 streams build progress" premise below does **not** hold. Achievable design = a
202-Accepted background build that fires **started + complete** (no granular % bar).

**Shipped:** e-4b-2 — service + REST surface. `GuiderService.BuildDarkLibraryAsync` (202-Accepted background
`Task.Run`, validates synchronously → 400 on bad request / 409 when disconnected) + `GetCalibrationFilesStatusAsync`
(→ `CalibrationFilesStatusDto`, null when disconnected). Endpoints `POST /equipment/guider/darklibrary/build` +
`GET /equipment/guider/darklibrary/status` (`EquipmentEndpoints.cs`, extracted handler for the error mapping). WS
events `guider.dark_library.started` / `…complete` / `…failed` via `IWsBroadcaster` (best-effort; NO granular
progress — see scope correction). +6 tests (service validation/connection contract + endpoint status mapping).

**e-4b-2 leftover (deferred):** record `calibration_state.guider.dark_library` on completion — there is no
server-side calibration-state store yet (the §39 calibration view derives from the frame catalog, not a guider
artifact record), so this waits until such a store exists or the client reads it live from the status endpoint.

**e-4b-3 (client UI) — partially unblocked (WS transport landed 2026-06-12).** The cover-the-scope build modal +
building/done indicator wants the §60.9 WS stream to observe `guider.dark_library.started/complete/failed`.
**Phase 12c.3 WS client — slice 1 (transport) shipped:** `lib/services/ws_event_stream.dart` (`WsEventStream`) +
`lib/models/ws_event.dart` — connects `ws://host:port/api/v1/ws` (header `X-Ara-WS-Version: 1`, via
`web_socket_channel`/`IOWebSocketChannel`), parses `{type,ts,seq,payload}` envelopes to a broadcast `Stream<WsEvent>`,
reconnects with backoff + resumes from the last-seen seq (first-frame `{"resume_token":"<seq>"}`), skips the
resume-response control frame + malformed frames. Injectable channel-seam (`WsSocket`/`WsConnector`); +6 tests.
**Remaining for e-4b-3:** WS-client slice 2 — a Riverpod provider wiring the active server (`savedServersProvider →
servers.last`) to a `WsEventStream` (lifecycle via `ref.onDispose`) + routing `diagnostics.*` → the existing
`DiagnosticsSnapshot` contract (`diagnostic_panel.dart` stub) and `guider.dark_library.*` → the build modal.
Slice 2 should also add a **connection-state signal** to `WsEventStream` (connected / reconnecting / disconnected) —
the `events` stream alone can't tell consumers the link dropped (from #390 review); the diagnostics panel + build
modal need a disconnected indicator. Web
support needs a query-param version fallback (browser WS can't set the header). The guider isn't yet connectable
from the client either (no equipment-guider control service) — also needed before the build button is useful.

**e-4b-2 review follow-up (from #384 r5, deferred to e-4b-3):** the build POST returns 409 for both "guider not
connected" and "build already in progress"; give the concurrent-build case a distinct signal (423 Locked or a
structured problem-detail `type`) when the build UI lands and needs to tell them apart.

**Shipped:** e-4c-a — defect-map RPC request/response shapes (`PHD2Methods.DarkLibrary.cs`):
`build_defect_map_darks {exposure_ms def 3000, frame_count def 10, notes?, load_after def true}` →
`{profile_id, defect_map_path, defect_count, exposure_ms, frame_count}`, and `set_defect_map_enabled {enabled}`.
Serialization-locked (+4 tests). `rebuild_defect_map` is omitted — it's in the design reference but not the
daemon's handler list (no handler yet).

**Shipped:** e-4c-b-1 — guider-client invocation (`PHD2Guider.DarkLibrary.cs`): `BuildDefectMapDarksAsync` +
`BuildDefectMapDarksRequest` send-site validation (frame 1..50, exposure 1..600000 ms, notes ≤ 500). Refactored
the shared daemon-limit constants to `Calibration*` names and extracted a shared `ValidateNotes` helper (used by
both the dark-library + defect-map builders) to keep it DRY. +5 tests. (`get_calibration_files_status` already
covers the defect-map status fields from e-4b-1.)

**Shipped:** e-4c-b-2 — `GuiderService.BuildDefectMapDarksAsync` (202-Accepted background op) + `POST
/equipment/guider/defectmap/build` (extracted handler → 400 on bad request / 409 disconnected-or-busy). Generalized
the e-4b-2 build machinery: a **single shared calibration-build gate** (`_calibrationBuildInProgress` — darks and
defect-map can't run at once on one camera) + a shared `EmitCalibrationEventAsync`; WS `guider.defect_map.started/
complete/failed`. +6 tests (service validation/connection + endpoint 202/400/409). `GET …/darklibrary/status`
already covers defect-map status.

**Shipped:** e-4c-c — the enable/disable toggles. `PHD2Guider.SetDarkLibraryEnabledAsync` / `SetDefectMapEnabledAsync`
(both return the status object via a shared `SendCalibrationStatusRpcAsync`), `GuiderService.SetDark/DefectMapEnabledAsync`
(→ `CalibrationFilesStatusDto`; not-connected → 409, daemon-reject → 422), and `POST …/darklibrary/enabled` +
`POST …/defectmap/enabled` (synchronous — return the updated status, not a 202 build). Extracted a shared `MapStatus`
helper (dedupes the status→DTO mapping). +7 tests. **The §63.6 guider calibration surface is now server-complete**
(build + status + enable/disable, for both darks and defect maps).

**e-4c remaining (deferred):** the client "Also build defect map" affordance + the build/toggle/status UI (gated on
the same WS client + dark-library UI as e-4b-3).

**WS slice 5 (#395) — diagnostics live updates: reconnect-replay gap (deferred, needs server-side work).** The
client `DiagnosticsAccumulator` folds the live `diagnostics.*` stream but has no replay across a WS reconnect:
`WsEventStream` auto-reconnects (the stream stays non-null, so the notifier's `build()` never re-runs), so any
event the server emits while the socket is down is lost. The hazardous case is a missed `diagnostics.cleared` —
the issue stays stuck in `_open` and the §51 pill reads amber/red indefinitely with no recovery short of a server
switch. The fix is **server-side history-on-connect** (replay the open-diagnostics set on a new WS connection / on
resume), then have the client reset its roll-up from that snapshot. There's an in-code `TODO` at
`diagnostics_state.dart`'s notifier `build()`. Tracked here per #395 review.

**Virtual-observatory bench (§42.2) — guider fault scenarios landed (bench-4, ✅).** `GuiderFakeIntegrationTest`
now drives the real `GuiderService`/`PHD2Guider` through two device-fault paths against `FakeGuider`: a lost guide
star (`StarLost` → `star_lost`, link stays Connected) and a dropped guider link (`FakeGuider.DropConnections` closes
the live socket → `PHD2ConnectionLost` → `Error` + §63.3 recovery begins). The recovery *outcome* (Unsupervised
off-systemd, Recovered/Failed on a host) stays unit-covered by `GuiderRecoveryCoordinatorTest`; the integration test
asserts only the fault-detection transition. **Next: bench-5** — the docker-compose chassis on colima/arm64 that runs
the proxy + fake-guider scenarios in a standing Linux lane (see the Drop-fault note below). The §42.2 *mid-sequence*
fault flow (pause the running sequence on a mid-guiding drop, vs. notify-only) remains a sequencer-side follow-up
(tracked separately above).

**Virtual-observatory bench (§42.2) — Drop fault Linux/arm64 stability (standing lane landed, bench-5 ✅).** The
`AlpacaFaultProxy` connection-drop fault (`DropConnectionAsync`: ContentLength64=64 → write 1 byte → SafeAbort)
relies on the managed `HttpListener` holding the connection open long enough for the client to observe the partial
response before the reset. Verified passing on macOS/arm64 AND inside a `dotnet/sdk:10.0` aarch64 Linux container
(all proxy tests green, 2026-06-12, PR #401). **bench-5 now makes that container repeatable**: `bench/` holds a
hermetic `docker compose` lane (`bench/run.sh`) that builds + runs the bench suite (29 tests) on `linux/arm64` and
exits with the test code — verified green on colima/aarch64. If the kernel ever delivers the byte before `Abort()`
or `Abort()` no-ops post-`Flush()` on a future runtime, run `bench/run.sh` to catch it. A future nicety: wire this
lane into CI if/when a hosted arm64 Linux runner is available (GitHub's hosted runners are x64 today).

**bench-5 Dockerfile — restore-cache split deferred (classic-builder limitation).** `bench/Dockerfile.linux-arm64`
uses a single `COPY . .` before `dotnet build`, so any source edit busts the implicit NuGet restore cache. The
idiomatic fix is a two-stage copy (csproj/props/sln → `restore` → full source → `build --no-restore`), but the
path-preserving form needs BuildKit's `COPY --parents` (`# syntax=docker/dockerfile:1.7-labs`), and the local
`docker-compose` 5.x here drives the **classic** builder (image label `builder=classic`), where `--parents`/syntax
directives are unavailable and a naive `COPY **/*.csproj ./` flattens the tree and breaks `dotnet restore <path>`.
The lane is occasional-use (pre-release / regression check, not per-commit CI), so the cold restore is low-cost for
now. Revisit the two-stage split when the lane runs under a BuildKit-enabled runner (the same CI move noted above).
Surfaced 2026-06-13 by the #408 review.

**bench-5 — substring test filter replaced with a `bench` category (✅ done, bench-6).** The lane used to select its
three fixtures with a substring filter (`FullyQualifiedName~AlpacaFaultProxyTest|...`) duplicated between the Dockerfile
ENTRYPOINT and the README, which could silently pick up a future `…Helper` class and drift between the two copies.
Now the three fixtures carry `[Category("bench")]` and the ENTRYPOINT filters `TestCategory=bench` — one source of
truth, no substring fragility, and a new bench fixture is picked up just by tagging it. The compose service also got a
fixed `image: openastroara-bench:linux-arm64` tag so repeated `--build` runs don't leave dangling images. Done in
bench-6 off the #408 review notes.

**Virtual-observatory bench — request-header forwarding is in (PR #401), response-header forwarding is not.**
`ForwardAsync` now forwards inbound request headers (minus hop-by-hop) to the upstream device. The *response*
direction still only copies Content-Type; if a bench-3+ scenario ever needs the daemon to see a specific upstream
response header, extend the forward symmetrically. Low priority (Alpaca responses are JSON-envelope-only today).

**Guider connect — full connect→Connected lifecycle against a minimal guider (bench-3 finding, ✅ RESOLVED).**
The combination of the §63.1 connect-as-service fix (#403), the read-driven `RunListener` (#404, replacing the macOS-fragile
`GetActiveTcpConnections` poll), and the `SendMessage` async-read timeout (#405) means the real `GuiderService`/`PHD2Guider`
now reaches `Connected` against the minimal FakeGuider and reflects live events. (The connect's first getter, `GetProfiles`,
throws fast when `get_profile`'s bare `result:0` fails to deserialize — caught — so connect returns promptly; later getters
are skipped, which is fine.) `GuiderFakeIntegrationTest.Reaches_Connected_and_reflects_live_guiding_events` now drives the
full connect→Connected→AppState(guiding)→GuideStep-RMS→disconnect path and passes in ~0.5s. The earlier "still open"
characterization was an artifact of testing before #405 landed.

**Guider connect — getters hard-fail against a guider that returns bare results (bench-3 finding).** The connect handshake
(`GetProfiles` etc.) throws `InvalidOperationException` when a getter response can't be deserialized to the expected typed
shape, aborting the rest of the §63.4/.5 push (caught, logged). Fine against real PHD2, but worth making each connect-time
getter independently best-effort so one unsupported method doesn't skip the others. Low priority. Surfaced by bench-3.

**Guider connect — SendMessage async-read timeout (bench-3 finding, ✅ RESOLVED in #405).** `SendMessage`'s `ReadLineAsync`
is now bounded by `receiveTimeout` (TcpClient.ReceiveTimeout doesn't apply to async reads), so a guider that never returns a
matching-id response fails the call instead of hanging the connect forever. In practice the bench connect doesn't hit the
timeout — `get_profile`'s bare response fails deserialization and `GetProfiles` throws fast — so the full lifecycle test
runs in ~0.5s. A future nicety: give the connect-time getters a shorter timeout than the 60s default so a genuinely
non-responsive guider degrades connect in seconds rather than a minute. Low priority. Surfaced 2026-06-13 by bench-3.

**§36 Data Manager — DeleteAsync conflates "not installed" and "permission denied" (§36-1 review note).** Both an
unknown/uninstalled id and an `IOException`/`UnauthorizedAccessException` (locked or permission-denied dir) return
`false` from `DeleteAsync`, so the caller can't tell "give up, it's gone" from "retry later, it's locked". Fine for the
§36-1 inventory layer (the endpoint maps `false` → 404 either way), but the §36-2 download engine should surface a
distinct "could not delete" error so the client can retry rather than treat a locked dir as already-clear. Low priority;
inherit the §36-1 catch shape but split the return. Surfaced 2026-06-13 by the §36-1 round-2 review.

**§36-2 Data Manager — stalled-download backstop (✅ RESOLVED in §36-2b round-6).** The sky-data HttpClient uses
`Timeout.InfiniteTimeSpan` (intentional: with `ResponseHeadersRead` the default 100s would fail a slow-to-start CDN,
and a multi-GB body must not be capped). The backstop is now an *idle*-progress watchdog in the download worker: a
per-job `CancellationTokenSource` armed with `CancelAfter(idleTimeout)` (default 60s) and reset on every byte of
progress, linked into the install token — so a transfer that stalls at 0 B/s is cancelled and reported
`download.failed` with `error: "stalled"` rather than pinning a worker forever. A healthy long download keeps resetting
the deadline and is unaffected.

**§36-2 Data Manager — DownloadRequestDto.ForceReinstall (✅ RESOLVED in §36-2b round-12).** `ForceReinstall: false`
now short-circuits when the package is already installed: `DownloadAsync` checks the §36-2a `.installed` sentinel and,
if present, throws `PackageAlreadyInstalledException` → the endpoint returns **409 Conflict** (no re-download).
`ForceReinstall: true` bypasses the check and re-downloads. (This is distinct from §36-2b-2's inventory rework, which
makes ListPackages/GetState read the sentinel for `InstalledUtc` — still pending.)

**§36-2 Data Manager — no graceful-shutdown drain for in-flight downloads (§36-2b round-9 review note).** The
download workers run as detached `Task.Run` jobs; `DataManagerService` is a plain singleton not tied to the host
shutdown signal. On a daemon restart mid-download the worker is abandoned, leaving the §36-2a `.staging-*`/`.backup-*`
scratch dirs behind (harmless temp dirs, but not reclaimed) and wasting the partial transfer. Two complementary fixes:
(a) **startup sweep** of stale `.staging-*`/`.backup-*` dirs under the sky-data root — **✅ DONE** (§36-2c:
`SkyDataInstaller.SweepStaleScratch`, called at boot in Program.cs; robust even against a hard kill a drain can't
catch); (b) **STILL OPEN** — make `DataManagerService` `IAsyncDisposable` (or an `IHostedService`) that cancels all job
CTSes and awaits outstanding tasks for a clean drain on graceful stop. (b) is low priority for v0.1.0 (restarts are rare
and a partial transfer just re-downloads). Surfaced 2026-06-13 by the §36-2b review; (a) resolved 2026-06-14.

## §43 backup — §43-2 deferrals (2026-06-13, after the §43-1 create/list PR)

§43-1 shipped the non-destructive half of the §43 backup feature — `BackupService.CreateZipAsync` (package
`profile.json` + `sequences/` into `{profileDir}/backups/backup-{utc}-{id:N}.zip` + a `.meta.json` manifest with
sha256), `ListSnapshotsAsync` (read the manifests, newest-first), and `GET /api/v1/backup/snapshot/{id}/download`.
Deferred to **§43-2**:

- **Restore — §43-2a DONE (synchronous, local snapshot); §43-2b OPEN (async worker + progress + remote source).**
  `POST /restore-zip` now restores for real: `BackupRestorer` extracts the chosen local snapshot and swaps the
  selected areas (profile.json / sequences) into place **atomically with all-or-nothing rollback** (mirrors the
  §36-2a installer's backup-aside→swap pattern, extended to multiple areas), gated by a manifest-sha256 integrity
  check. The endpoint returns `202` on success, `404` for an unknown snapshot, `422` for a non-local source / no area
  / corrupt archive. **§43-2b(a) DONE 2026-06-15:** the restore now runs on a background worker (202 returns
  immediately; cheap validation — source/area/snapshot-exists/checksum — stays synchronous so 404/422 surface before
  the 202), `GetCloneStatusAsync` drives a real `idle→running→done/failed` state machine, and a concurrent restore is
  refused **409**. An `IBackupRestorer` seam makes the worker states testable (block/throw). STILL OPEN for **§43-2b**:
  (b) accept a **remote/cloud** `BackupSourceUrl` (re-download then restore) — currently only a local snapshot-download
  URL is accepted; (c) the frame-metadata / logs areas (`RestoreFrameMetadata` / `RestoreLogs` flags) once §43-1 create
  captures them. **§43-2b(d) DONE 2026-06-15:** the client Backup screen now polls `clone-status` after the restore POST
  (`CloneStatus` model + `cloneStatus()` API + `awaitRestoreTerminal()`) and shows the real terminal outcome — including
  a worker-side failure the 202 couldn't reveal — instead of a premature "complete". §43-2a landed in the restore PR
  (2026-06-14). **Future (with (c)):** when large areas land, move `ValidateChecksum`'s SHA-256 off the request thread
  onto the worker — it currently hashes synchronously before the 202 (negligible for config-sized payloads). Flagged by
  the #458 review.
- **Restore sha-256 gate is bypassable by deleting the manifest (low priority).** `RestoreCore` validates the
  archive against its `.meta.json` sha-256 before touching live config, but a missing/unreadable manifest skips the
  gate (logged at Warning via `LogManifestSkipped`, so it's visible; the restore proceeds unvalidated). Anyone able to
  delete the sidecar bypasses integrity validation. Fine for a local single-user daemon; when §44 cloud/remote restore
  lands, require the manifest (or carry the checksum out-of-band). Surfaced 2026-06-14 by the §43-2a review.
- **No disk-space pre-flight on create (low priority).** `CreateZipAsync` packaging on a full disk fails mid-zip with
  an `IOException`; the catch reclaims the temp and the caller gets a generic 500. A pre-flight free-space check or
  mapping the disk-full `IOException` to **507 Insufficient Storage** would give a clearer operator signal. Low
  priority — §43-1 payloads are config-sized (KB), so disk-full during packaging is unlikely. Surfaced 2026-06-13 by
  the §43-1 round-7 review.
- **Async packaging + progress WS.** `CreateZipAsync` completes the zip within the request (the payload is config-sized
  — kilobytes, not the frame library) rather than on a background worker emitting `backup.*` progress events. The
  202/operation-id contract is already in place so the wire shape won't change when it becomes truly async; add the
  worker + WS progress if a future area (e.g. frame-metadata) makes the payload large enough to warrant it.
- **Area selectors beyond profiles+sequences.** §43-1 captures the two config areas only. The frame-metadata and log
  areas from the §43 selector set (and the `RestoreRequestDto.RestoreFrameMetadata`/`RestoreLogs` flags) arrive with the
  restore work in §43-2.
- **No retention/pruning.** Backups accumulate under `{profileDir}/backups/` indefinitely; there's no cap or
  age-based prune. Low priority — add a retention policy (keep-N / keep-days) if disk growth becomes a concern.
- **Orphan-archive boot sweep — RESOLVED 2026-06-15.** `BackupService.SweepOrphans(profileDir)` runs at startup in
  `Program.cs` (mirroring §36-2c `SweepStaleScratch`, before the daemon accepts requests so no create can race it) and
  reclaims **both** crash-only leaks under `{profileDir}/backups/`: every `.tmp-*.zip` from a create hard-killed before
  its `File.Move` reveal, and every `backup-*.zip` whose `{base}.meta.json` sidecar is absent (SIGKILL between the
  reveal and the manifest write). Best-effort per file; manifest-backed snapshots + foreign `.zip`s are untouched.
  Covered by 5 `BackupServiceTest` cases. Original entry surfaced 2026-06-13 by the §43-1 round-4 review.
- **Daemon-wide API auth is unaddressed (cross-cutting, not §43).** The server binds `ListenAnyIP` (default :5555)
  with **no authentication/authorization middleware** — the whole REST surface is open on whatever interface it's
  reachable on, matching the §13 trusted-LAN headless deployment model. The §43-1 backup-download endpoint inherits
  this posture; it adds no *new* exposure (`profile.json` content is already served by the no-auth `/api/v1/profile/*`
  GET endpoints, and `frames/{id}/download` already streams files), but a backup zip does bundle the full profile,
  which may hold device credentials. Bolting auth onto one route would be inconsistent and ineffective (the same data
  leaks via `/api/v1/profile/*`). If the daemon is ever exposed beyond a trusted LAN, API auth must be a **cross-cutting
  middleware** decision (PRODUCT-SCOPE / user-authoritative), not per-endpoint. Surfaced 2026-06-13 by the §43-1
  round-4 review.
- **Stats catalog: light-frame partial index — RESOLVED 2026-06-15.** Added `idx_frames_light_captured` on
  `frames(captured_utc) WHERE frame_type = 'light'` (additive + idempotent in `SqliteAraDatabase.InitializeAsync`), with
  an EXPLAIN-QUERY-PLAN test asserting the planner uses it for the light-frame, captured_utc-ordered queries. This covers
  the `WHERE frame_type = 'light'`-filtered stats queries (targets, calendar, frame-quality, best-frames, focus-temp).
  Residual follow-up (low priority): the `SUM(CASE WHEN frame_type = 'light' …)` aggregations in overview/calendar scan
  all rows regardless of the partial index (the CASE evaluates per row), and §50.4 focus-temp's
  `WHERE focuser_position IS NOT NULL` predicate is still an unindexed residual filter — a separate covering index on
  `(focuser_position, captured_utc)` would tighten that one if the catalog grows large. Original entry surfaced
  2026-06-14 by the §50.19 round-3 review + #448 re-review.
- **Dashboard tiles aren't responsive to narrow viewports (cross-cutting client UI, not §50.19).** Every Stats-dashboard
  tile — the shared `StatTile` (width 200) and the §50.19 milestone badges (width 200) — uses a fixed width inside a
  `Wrap`. Text wraps vertically so there's no horizontal clip, and on the desktop/tablet target the viewport is always
  wider than one tile, so this is benign today. But on a genuinely narrow viewport a 200-wide tile would overflow its
  `Wrap` line. Making the badges flexible alone would make them ragged next to the fixed-200 record tiles, so the right
  fix is a dashboard-wide pass (e.g. a shared responsive `BoxConstraints(minWidth: 160, maxWidth: 220)` applied to
  `StatTile` and the badges together). Low priority until a narrow-viewport target exists. Surfaced 2026-06-14 across the
  §50.19 client-view review rounds.
- **client-settings: orphaned `.tmp-*` on a mid-write crash — RESOLVED 2026-06-15.** `ClientSettingsService.SweepOrphans(profileDir)`
  runs at startup in `Program.cs` (in the post-build block beside the §43-2 backup sweep, before request acceptance)
  and reclaims any `{profileDir}/client-settings.json.tmp-*` left when a `ReplaceAsync` was hard-killed between the temp
  write and its `File.Move` rename. Best-effort per file; the live `client-settings.json` is untouched; logs a Warning
  when it reclaims any. Covered by 5 `ClientSettingsServiceTest` cases. Mirrors §43-2 `BackupService.SweepOrphans` /
  §36-2c `SweepStaleScratch`. Original entry surfaced 2026-06-14 by the §55.1 round-3 review.
- **Stats sections: persist-through-refresh migration — DONE (§50).** A shared `StatsRefreshMixin<T>`
  (`lib/state/stats/stats_refresh_mixin.dart`) provides the Achievements-style refresh: swap-on-success-only (no
  `AsyncValue.loading()` flash), keep last-good data + rethrow on failure (widget shows a stale banner/chip via a local
  flag), `ref.mounted` guard, and a build-generation guard for server-switch (including the error path). **All six live
  Stats sections** — Overview, Targets, Best Frames, and the Frame Quality / Guiding RMS / Calendar charts — are converted
  (notifier + `ConsumerStatefulWidget` widget + tests; charts use the shared `ChartStaleChip` overlay in place of the tile
  sections' banner). The §50.19 Achievements section was already on this pattern (the original template). Landed across the
  refresh-* slices 2026-06-14; subsumes the earlier separate "post-await `ref.mounted` guard" item (the mixin's
  `refreshUsing` includes the `ref.mounted` guard for every section).
- **Best Frames: validate `frame_id` rather than degrading to '' (§50).** `BestFrame.fromJson` degrades a missing/wrong-typed
  `frame_id` to an empty string. Harmless today (the tile never reads `frameId`), but when per-frame detail drill-down lands,
  an empty id would silently misbehave rather than surfacing a parse error. Tighten when the drill-down navigation is built.
  Surfaced 2026-06-14 by the #436 review.
- **Strengthen the sibling concurrent-refresh tests (test-quality, §50). — RESOLVED 2026-06-15.** Overview / Targets /
  Best-Frames were already on the gated-fake generation-guard test (rewritten when those sections moved to
  `StatsRefreshMixin`). The remaining gap was **Achievements**, which still had its own *inline* generation guard (it was
  the pattern the mixin was extracted from) — untested, and missing the #444 fix (a fetch that *throws* after a server
  switch rethrew instead of being swallowed). Converted `AchievementsNotifier` to the shared `StatsRefreshMixin`
  (de-dups the inline guard + inherits the #444 swallow-on-mismatch fix) and added the two gated generation-guard tests,
  bringing all eight stats notifiers to parity. Surfaced 2026-06-14 by the #437 review.
- **Unify the stats models' `_dt` UTC parser (robustness, §50). — RESOLVED 2026-06-15.** Extracted a single
  `parseStatsUtc()` helper (`lib/models/stats/stats_time.dart`) and pointed all six stats timestamp parsers at it —
  `stats_overview`, `stats_target`, `best_frame`, `achievements` (which used the weak `DateTime.tryParse(v)?.toUtc()`
  form that mis-shifted a zone-less timestamp), plus `guiding_rms` and `focus_temp` (which had their own correct copies).
  Now uniformly drift-proof; +4 helper tests (Z / zone-less→UTC / numeric-offset→UTC / null). `calendar_stats._date` is
  intentionally left out (date-only `yyyy-MM-dd` semantics, not an instant). Surfaced 2026-06-14 by the #438 review.
- **Focus-Temp scatter chart: blocked on §38 focuser-position persistence (§50.4). — RESOLVED 2026-06-14.** Done across
  four slices: `focuser_position` column (#446), capture stamps FOCUSPOS (#447), real `GetFocusTempAsync` query +
  Pearson r² (#448), and the client scatter wired to the live endpoint (#449). The chart is now live like the other five
  §50 visualizations. Original note retained for history: the live endpoint was a deliberate server stub until the column
  landed, so wiring the client early would have shown permanent "no data".
- **Drop the dead `_explicitZone` regex branch in the remaining `_dt` copies (§50). — RESOLVED 2026-06-15.** The shared
  `parseStatsUtc()` helper (entry above) has no `_explicitZone` branch — `DateTime.tryParse` already parses any timezone
  designator (`Z` or a numeric offset) with `isUtc == true`, so it was unreachable. Removing `guiding_rms`'s local `_dt`
  in favor of the helper dropped the last copy of the dead branch + the regex. Surfaced 2026-06-14 by the #449 review.
- **Stats `since` filter assumes a UTC `DateTimeOffset` (§50). — RESOLVED 2026-06-15.** Added a shared
  `SqliteUtcBound(DateTimeOffset)` helper (`v.ToUniversalTime().ToString("O", InvariantCulture)`) and routed all three
  `captured_utc` comparison bounds through it (`GetCalendarAsync`'s 30-day spark window, `GetFocusTempAsync`,
  `GetGuidingAsync`) — a non-UTC `since` is now normalized to UTC before the lexicographic comparison against the stored
  UTC strings. +1 test (a `-05:00`-offset cutoff representing the same instant filters identically to the UTC cutoff).
  Surfaced 2026-06-14 by the #448 re-review.
- **`captured_utc` write paths — RESOLVED 2026-06-15 (premise corrected).** The #456 review flagged a presumed `Z` vs
  `+00:00` *mix* in `captured_utc` (claiming `CaptureScanService` emitted `…Z` via a `DateTime`). **Verified empirically
  that the mix does not exist:** `CaptureScanService.capturedUtc` is a `DateTimeOffset` (the `?? File.GetLastWriteTimeUtc`
  promotes the `DateTime` to `DateTimeOffset`), so its `ToString("O")` emits `…+00:00`, identical to the
  `SqliteFrameRepository` path. The column is uniformly `…+00:00`; there was no ORDER-BY/dedup inconsistency to fix, so
  no backfill was warranted. **The probe did surface a real adjacent bug, now fixed:** `ParseDateObs` parsed the FITS
  `DATE-OBS` header with a bare `DateTimeOffset.TryParse`, which assumes *local* for a zoneless value — but FITS defines
  DATE-OBS as UTC and it's usually written without a zone. That both mis-shifted a recovered frame's `captured_utc` by the
  host's UTC offset and stored a non-UTC offset suffix that would break the lexicographic `since`/ORDER BY comparisons.
  Now parsed with `AssumeUniversal | AdjustToUniversal` (zoneless → UTC; an explicit offset → converted to UTC), so
  `captured_utc` is always written `…+00:00`. Covered by `CaptureScanDateObsTest` (5 cases). No backfill: the orphan-scan
  path has no real rows yet (recovered captures land with §38), and an already-corrupted instant can't be re-derived from
  the misinterpreted value anyway. Original entry surfaced 2026-06-15 by the #456 review.
- **§50.4 focuser position is narrowed `(int)GetInt64` (§50.4).** `GetFocusTempAsync` reads `focuser_position` as a
  64-bit SQLite INTEGER and narrows to the `int` DTO field; a value above `Int32.MaxValue` (~2.1B steps — no real
  focuser) would wrap silently. If a wider range is ever needed, widen `FocuserPositionDto`/`FocusTempPoint` to `long`
  end-to-end (wire + client model) rather than casting. Low priority. Surfaced 2026-06-14 by the #448 re-review.
- **Build-time guard for un-provisioned macOS CEF (§36, DX nicety).** If a developer runs `flutter build macos` without
  first running `scripts/provision-cef-macos.sh`, the failure is a cryptic linker error (missing CEF framework symbols).
  A short pre-build phase (or a check in the Podfile `post_install`) that detects an un-provisioned `macos/third/cef` and
  emits a human-readable `error:` pointing at the script would improve DX. Low priority. Surfaced 2026-06-15 by the #463 review.
- **Windows `windows/runner/main.cpp` CEF multi-process/IME tweak (§36).** When the Windows build is first exercised,
  fold in the `windows/runner/main.cpp` CEF multi-process + IME tweak that `webview_cef` requires on Windows (the Linux
  and Windows builds auto-download CEF via the plugin's `third/download.cmake`, so no binary-placement step is needed
  there — unlike macOS). Surfaced 2026-06-14 by the #450 review.
- **DONE (2026-06-15) — macOS webview_cef build wired up (§36).** The first real `flutter build macos` was completed.
  macOS (unlike Linux/Windows) does not auto-download CEF, so: (1) `scripts/provision-cef-macos.sh` fetches + SHA-verifies
  the pinned arch-matched CEF bundle and restructures the flat framework into a versioned bundle (macOS Xcode rejects the
  flat layout); (2) `macos/Podfile`'s `post_install` hook repairs the `OTHER_LDFLAGS` that CocoaPods mis-tokenizes for the
  space-containing "Chromium Embedded Framework" name; (3) `macos/Podfile.lock` + the CocoaPods integration in
  `Runner.xcodeproj`/`Runner.xcworkspace` are now committed for reproducible pod resolution. The CEF binaries themselves
  stay uncommitted (gitignored in the plugin / shared pub-cache) — run the script once after `flutter pub get`.
- **DONE (2026-06-15) — §36 Aladin HiPS tile rendering verified on-device (macOS).** First on-device `flutter run -d macos`
  confirmed the embed renders: CEF logged the Aladin Lite JS loading from the CDN and switching to the DSS2 HiPS survey
  (`Change url of P/DSS2/color to https://irsa.ipac.caltech.edu/data/hips/CDS/DSS2/color`), i.e. null-origin tile fetches
  succeed as predicted — no CORS fallback (localhost bootstrap server) needed. Getting here required a second fork fix (see
  PORT_DECISIONS §36): `createWebView()` threw `Null is not a subtype of InjectUserScripts` on a null `injectUserScripts`,
  which `AladinView` caught and surfaced as the misleading "Sky atlas unavailable / Chromium runtime required" fallback.
- **Implement the merged Planning tab (§36/§25.5, decided 2026-06-15 — see PORT_DECISIONS).** Collapse the separate
  Framing + Sky Atlas tabs into ONE **Planning** tab on the existing Aladin embed — do this BEFORE Phase 12c.2 stands up a
  second `webview_cef` instance (two Chromium textures = ~2× RAM + duplicated offline plumbing). Work items:
  (1) client: replace the two `_TabSpec`s in `app_shell.dart` with one "Planning" tab; fold `framing_tab`/`framing_state`
  into the `sky_atlas` surface; add an Explore / Tonight's-Sky / Frame mode model (Frame is a toggle, not a separate tab).
  (2) Frame mode: draw the **profile-derived FOV** rectangle on the Aladin view —
  `pixelScale = 206.265 × pixelSize_µm ÷ focalLength_mm`, FOV = `sensor_px × pixelScale` — + rotation + mosaic grid (§47),
  with "Add to Sequence" / "Build Mosaic Sequence" output.
  (3) **Profile-cached camera geometry:** on the first successful camera connect for a profile, persist sensor W/H px +
  pixel size + effective focal length (incl. reducer/barlow) into the profile so Frame works with the camera disconnected /
  offline; manual override in Settings → Camera/Telescope. (Inputs exist: `CameraSettings.PixelSize`,
  `TelescopeSettings.FocalLength`, `EquipmentDtos.SensorWidth/Height`.) **Invalidate the cache** so a swapped camera /
  changed reducer doesn't yield stale framing: re-cache when a connect reports different sensor dims (auto + one-time notice),
  fold into the §30.7 equipment-change check (already covers telescope/focal-length → framing), and add a "Refresh from
  connected camera" affordance by the FOV readout.
  (4) Tonight's Sky = location-aware "best objects tonight" from profile site lat/long + date, ranked by altitude/visibility.
  (5) reconcile prose in §36 / §47 / §25.5 / §61 search registry (map "framing"/"FOV"/"mosaic"/"tonight"/"sky atlas" → Planning);
  update `COMMIT-PR-RULES.md` Phase 12 split (12c drops Framing; 12e becomes "Planning"). Server unaffected.
  Three spec details to pin during implementation (flagged by the #465 review): (i) **one-time-notice scope** for the
  connect-diff re-cache — define it as "once per detected sensor-geometry change" (not per session), so a second camera swap
  still notifies; (ii) **reducer/barlow** swaps don't change sensor dims, so they rely on the §30.7 path — *verify* §30.7's
  invalidation matrix explicitly recomputes framing on effective-focal-length change, don't just assume it; (iii) the §25.5
  walkthrough numbers only 4 tabs (Image Library + Stats absent) and the NINA-control-labels list (~PORT_PLAYBOOK line 2021)
  still says "Framing Assistant"/"Sky Atlas" — fold both into the prose sweep so they don't slip.
- **Re-shim the open-astro/webview_cef fork on Flutter upgrades (§36).** The §36 Aladin embed depends on the
  [open-astro/webview_cef](https://github.com/open-astro/webview_cef) fork (SHA-pinned), which carries a one-line shim:
  `bool TextInputClient.onFocusReceived() => false;` in `lib/src/webview_textinput.dart` (upstream predates that Flutter
  API). On each Flutter SDK bump, re-verify the fork compiles — Flutter periodically adds abstract members to
  `TextInputClient` / `State`, each of which needs another no-op override in the fork. The client CI job is analyze+test
  (it never compiles the WebView widget unless a test imports `aladin_view.dart`, which `aladin_view_test.dart` now does),
  so a fork/SDK mismatch surfaces as a client-test compile failure. Update the pinned SHA in `pubspec.yaml` after
  re-shimming. Surfaced 2026-06-14 by the #451 review (fork-maintenance dependency).
- **§70 import — distinguish repeated-import profile names.** D-2 (#489) names every imported profile "Imported profile"
  because the export deliberately strips the donor block (including any donor-chosen `display_name`) per the strip-all
  privacy policy ([[project-profile-share-path-policy]]) — so the manifest carries no profile name to reuse. Result: two
  imports of the same template land as two profiles both named "Imported profile". The recipient can rename via the §37/§30
  management UI (PR C, #488), so this isn't blocking, but the default could be friendlier — e.g. derive a non-identifying
  label from the `rig_description` ("Imported — 2032 mm rig") and/or auto-suffix a counter when the name already exists in
  the repo. Flagged by the #489 review (minor / follow-up).
- **§70 import — `DroppedFields` can drift from the export's strip logic.** `ProfileShareService.DroppedFields` (the list the import preview shows the recipient as "you must re-enter these") is a hand-maintained static mirror of what `ExportAsync` actually strips (§70.1: equipment, paths, location, PHD2, notification tokens). Both sides were authored together so they agree today, but if the export's strip set changes, the import's advisory list won't follow automatically. Low severity (advisory text only, no runtime effect), but worth deriving both from one source — or at least a paired test asserting the export's stripped categories match `DroppedFields` — when §70 next gets touched. Flagged by the #489 review.
- **§37 wizard — null=keep-base mappers can't clear a field back to empty.** The wizard draft→section mappers (applyDraftToPlateSolve and the others) use `copyWith`'s `?? base` null-preserves-base semantics, so a blank field inherits the base profile's value. Intended for the create-new-profile flow (you set values, you don't clear them), but it means re-running the wizard on an existing profile can't blank a path/value back to empty — only overwrite it. If the wizard ever becomes a full editor (vs. create-only), add an explicit "clear" affordance. Flagged by the #492 review.
- **§37 wizard — no per-screen validity gate.** Screen 11 added the first inline field validation (search radius, via `WizardTextField.errorText`), but `WizardScreenScaffold`/the wizard shell have no `canAdvance` hook: a user can see the red error, advance, and Save. It's safe (invalid values are never written to the draft — the field keeps its prior value), but the UX is slightly deceptive. If more screens add validation, give the shell a per-screen "is valid?" callback that disables Next/Save while an error is showing. Flagged by the #492 review.
- **§37.5 wizard — safety screen is the section-mappable subset only.** Wizard screen 15 (#~495 follow-on) maps to the profile safety section's `onUnsafe` / `autoResumeWhenSafe` / `resumeDelayMin`. The §37.5 placeholder also envisioned **weather-type-granular** actions (separate clouds / wind / rain → Pause/Abort+Park/Ignore), a wind threshold, a WILMA-offline auto-abort timer, and **alarm sound/vibrate** — none of which are fields on the `SafetyPolicies` section DTO. To ship those, the server's safety section needs the extra fields first (then a wizard + Settings → Safety follow-up). For now the wizard sets the general unsafe-conditions reaction; the granular/alarm bits stay Settings/§51-only.
- **§37.5 wizard — site/altitude screen maps horizon + twilight only.** Wizard screen 16 maps the draft's hard-minimum altitude → `SiteSettings.defaultHorizonAltitudeDeg` and a twilight choice → `SiteSettings.twilightDefinition`. The §37.5 placeholder also wanted a *separate* soft-warning altitude and a max-sequence-runtime cap — neither is a field on the site (or any) section DTO. Add them server-side (site section or a new sequencing-limits section) before wiring a wizard/Settings control. Flagged by the screen-16 build.
- **§37.6 wizard sky-data — no auto-checked "recommended preset".** Screen 17 lists the not-installed packages and lets the user tick what to download (queued post-save). The §37.6 sketch wanted a default-checked "Famous Targets + Star Catalogs" preset + rig-specific surveys (§36.10). The `DataPackage` DTO has no "recommended" flag, and the per-profile `/sky-data-recommendations` endpoint needs a profile id that doesn't exist until Save — so the wizard can't pre-check a curated preset. Add a `recommended`/`category=preset` flag to the package DTO (or expose recommendations pre-create) to enable the default-checked preset. Flagged by the screen-17 build.
- **§38 editor Save — verify the daemon accepts a promoted plain-array `Items`.** `nina_dom.withChildren` always emits the canonical ObservableCollection wrapper (`{$type, $values}`), even when the source node carried a plain-array `Items` (which `childrenOf` tolerates). The engine round-trips correctly on the Dart side (test added), but before the Phase 3 Save UI wires `updateSequence`, confirm the daemon's `SequenceContainerCreationConverter`/schema accepts the promoted wrapper form for a body that originally stored `Items` as a bare array — otherwise editing such a node would 422 on Save. Almost certainly fine (the daemon's own templates use the wrapper shape), but it's an untested cross-boundary assumption. Flagged by the #514 review (✅-approved, deferred to Phase 3).
- **§38 editor — Run Autofocus + Center instructions are not ported as sequencer classes.** The drag-and-drop instruction catalog (`instruction_catalog.dart`) only lists instructions that exist under `OpenAstroAra.Sequencer/SequenceItem/` (an unknown `$type` deserialises to a skipped `Unknown*` placeholder on the daemon, so faking a `$type` would silently no-op). NINA's `RunAutofocus` (Autofocus) and `Center`/`CenterAndRotate` (Platesolving) instructions have **no** equivalent class in the Sequencer project yet, so they're intentionally absent from the palette even though the §38 directive lists "focusing" and "Center". Focusing is partially covered by `MoveFocuserAbsolute`/`MoveFocuserRelative`. To add Autofocus/Center to the palette: first port the instruction classes (they wrap the existing §59 AF and §28 CenteringService), then add catalog entries. Flagged during the #514-follow-on catalog build.
- **§38 editor catalog — release-mode message on a non-String-keyed default.** `instruction_catalog._deepCloneJson` keeps a debug `assert(value is Map<String,dynamic>)` for a clear message; in release that assert is stripped and the fail-fast becomes the bare `entry.key as String` cast (an opaque `TypeError` with no context, NOT the assert message). The whole catalog is `const Map<String,dynamic>` so this can't trigger today; if Phase 2 ever generates `InstructionDef`s with non-const map defaults, convert the assert to a release `throw` with a descriptive message. The same release-vs-debug note applies to `InstructionField`'s `enumLabels`/`enumValues` XOR assert, which can't become a release `throw` without making the constructor non-`const` (which would break the const catalog). Flagged by the #515 review (low/future-risk, no current bug).
- **§38 editor — make container `build()` populate an empty `Items` wrapper.** `nina_dom.isContainer` classifies a node as a container by an `Items` key or a `.Container.` `$type`. Today every container in a body has `Items`, and the only catalogued types are leaf instructions, so this is robust. But when the instruction catalog grows **container** entries (loops/conditional containers), `InstructionDef.build()` should always emit `Items: {$type: itemsWrapperType, $values: []}` for them, so a freshly-built empty container is detected by the structural `Items`-key branch and the `.Container.` string-scan stays a fallback for raw imported nodes only. Flagged by the #516 review (forward-looking; no current bug — no container catalog entries exist yet).
- **§38 field editor — make text fields controlled when undo/reset lands.** `SequenceFieldEditor._FieldControl` uses an uncontrolled `TextFormField(initialValue:)` reseeded by a `ValueKey(path/field)` on selection change. Correct for the current scope (the only way a field's value changes while open is the user typing into it), but if a future action mutates the selected node's value *without* changing the selection — undo/redo, "reset to defaults", or a sequence reload that keeps the selection — the visible text would go stale. At that point switch to an explicit `TextEditingController` synced from the provider in `didUpdateWidget` (guarding against clobbering in-progress typing / cursor jumps). Flagged by the #519 review (forward-looking; reviewer confirmed the ValueKey approach is correct for now).
- **§29.9 logs — tail does a full-file scan; no pagination.** `LogService.TailAsync` reads each rolling log file line-by-line (one string alloc per line) on every request, walking newest→oldest only until the line cap fills. Fine for an occasionally-hit diagnostic endpoint, but the worst case is a ~50 MB read + thousands of allocations per call, and there's no cursor/offset to page backward beyond the requested cap. If the §54 Logs panel ever live-streams or paginates, add a reverse byte-scan (seek from EOF) or a persisted line index, and a continuation token. Flagged by the #545 review (perf / future work, no current bug).
