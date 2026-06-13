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
- ✅ **Full `TakeExposure` capture path** — **resolved in §14e capture-path PRa+PRb (#343 + follow-up)**: real `CameraService` pipeline (expose → download → §72 FITS → §28 catalog) + re-ported `TakeExposure` executing through `IImagingMediator.CaptureImage` on the same pipeline. The #315 capture-block note is addressed: `IsFreeToCapture` truthfully reflects the shared in-flight capture gate (Register/Release stay inert — no headless consumer registers blocks). Still §2105-gated: the in-memory render path (`CaptureAndPrepareImage`/`PrepareImage`/live view, OpenCvSharp4 + libraw) — `TakeExposure` deliberately discards the returned `IExposureData`.
- ✅ **Connect/Disconnect/SwitchProfile capstone** (`ConnectAllEquipment`, etc.) — **resolved in §38k-22 (#318)**: registered all five via the `HeadlessProfileService` stub; `DisconnectAllEquipment`/`DisconnectEquipment` flipped `internal`→`public` (CA1002 on their `Devices` property fixed to `IReadOnlyList<string>`).

## Execution-engine TODOs

- **Sequencer captures share the "manual capture" session** (flagged on #344, §14e capture-path PRb). `CameraService.CaptureImage` (the `IImagingMediator` sequencer path) routes every frame through `EnsureManualCaptureSessionAsync`, so a named-target sequence run's lights land in the same catalog session as ad-hoc manual REST snapshots. Acceptable for PRb (the capture pipeline is the deliverable; no session-orchestration layer exists yet), but a §40/§50 catalog-organisation concern: a real run should open and own a per-target session and stamp its frames with it. Resolve when the execution engine grows session lifecycle — a sequence run creates a session keyed on target/start-time, and `TakeExposure` passes that session id down through `CaptureImage`.
- **REST manual capture is LIGHT-only by design** (flagged on #344, §14e capture-path PRb). `CaptureInBackgroundAsync` (the fire-and-forget REST path) hardcodes `"LIGHT"`/`FrameType.Light`; `ExposureRequestDto` has no `ImageType` field, so the REST snapshot endpoint cannot produce darks/flats/bias. This is **deliberate for v0.0.1**: the manual-capture endpoint (§60.5) is for ad-hoc light/test snapshots, while calibration frames are sequencer-driven (§38.7 templates + §39 auto-flats/dark-library + calibration sessions) where the frame type is carried on the `CaptureSequence`. If a future REST "capture a calibration frame" affordance is wanted, add an optional `ImageType` (default LIGHT) to `ExposureRequestDto` and thread it into `CaptureCoreAsync` (which is already frame-type-parameterized — only the REST entry point hardcodes it).
- **FITS keyword-convention audit** (flagged on #344). The capture path writes `GAIN` and `OFFSET` for the camera's electronic gain/offset — the NINA/Siril/PixInsight/ASTAP convention, and what ARA's own `FITSHeader` reader round-trips. A reviewer noted these aren't in the core FITS standard dictionary (some pipelines also look for `PEDESTAL`/`BLKLEVEL`). No change for v0.0.1 — interop with the astrophotography ecosystem + ARA's reader wins, and `PEDESTAL` carries a *different* semantic (a data value to subtract). Revisit only as part of a deliberate full FITS-header-convention audit (alongside `IMAGETYP` casing, `DARKFLAT` vs `DARK`, etc.) if/when broader tool compatibility is assessed.
- **§28 `ExposureSeconds` is `int` across the frames schema + DTOs** (flagged on #343). Sub-second calibration exposures (bias/short darks) round up to 1s in the catalog (the FITS `EXPTIME` header preserves the exact value). Widening to `double` touches the SQLite column affinity, `FrameDto`/`FrameListItemDto`, the stats aggregations and the WILMA codegen wire shape — do it as one dedicated pass before the calibration workflows (§40) ship. **Same pass: make `Gain` nullable end-to-end** (the capture path currently carries `Gain ?? -1` as a magic sentinel into the int column, also flagged on #343).

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

**Virtual-observatory bench (§42.2) — Drop fault Linux/arm64 stability (track for bench-5).** The
`AlpacaFaultProxy` connection-drop fault (`DropConnectionAsync`: ContentLength64=64 → write 1 byte → SafeAbort)
relies on the managed `HttpListener` holding the connection open long enough for the client to observe the partial
response before the reset. Verified passing on macOS/arm64 AND inside a `dotnet/sdk:10.0` aarch64 Linux container
(all proxy tests green, 2026-06-12, PR #401). Kept here so the **bench-5 docker-compose chassis** keeps a standing
Linux/arm64 lane for the Drop test — if the kernel ever delivers the byte before `Abort()` or `Abort()` no-ops
post-`Flush()` on a future runtime, this is the first place to check. Per #401 review.

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
