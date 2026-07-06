# OpenAstro Ara — Port TODO log

Append-only list of every `TODO(port)` and `PORT_BLOCKED` left in the codebase during the port, grouped by phase. Also tracks out-of-scope CodeRabbit suggestions deferred for follow-up.

Per PORT_PLAYBOOK.md §0 rule 4 (`Cite when stuck`): when you cannot translate a construct, leave a `// TODO(port): <one sentence>` and a placeholder that compiles, log it here, and move on. Sweep in Phase 15.

Per §0 rule 6 + §15 step 7 + COMMIT-PR-RULES.md CodeRabbit rule "out-of-scope suggestions": when CR suggests a broader refactor or future feature, log it here with the PR reference and reply "Acknowledged — tracked in design/PORT_TODO.md for follow-up".

---

## §36 Planning / webview / connection — follow-ups (2026-06-27)

- **DONE (verified 2026-07-02) — `webview_all` is the shipping renderer and `webview_cef` is fully retired.** Completed by the #611 native-webview pivot, which went further than this entry's plan: WKWebView (macOS/iOS) + WebView2 (Windows) via `webview_all`, a native WebKitGTK overlay on Linux (the platform-view GL conflict made the in-tree webview unusable there), the `packages/webview_cef` submodule removed, and the CEF download/helper machinery deleted. All three desktops verified on-device; the `client-build` CI job compiles the native runners. Original entry: The CEF OSR path freezes on macOS (lock-up on Frame in dense fields, idle freeze); the `--dart-define=WEBVIEW_ALL=true` spike (native platform view) is solid. Plan: (a) confirm the idle + Frame stress on WKWebView; (b) **Linux WebKitGTK WebGL2 spike** on Ubuntu 24.04 (the one gating unknown — needs a `linux/runner/my_application.cc` GtkOverlay edit per `webview_all`); (c) flip the default + remove the flag; (d) drop `webview_cef` + the CEF download/helper machinery. See PORT_DECISIONS 2026-06-27. (`flutter_inappwebview` rejected — no Linux; `atomic_webview` — separate window.)
- **Deep offline star catalogue (mag ~12–14) to replace the reverted online Gaia.** Online Gaia DR2 tiles destabilised CEF OSR and were reverted; the bundled catalogue stops at mag 7. Bundle deeper Norder tiles (offline, no tile churn) sized to taste once the webview pivot settles — WKWebView/WebView2 also handle deeper WebGL2 better than CEF OSR did. (`skydata/stars/properties` is the cap; `display_limit_mag` is the runtime knob.)
- **OBSOLETE (verified 2026-07-02) — do NOT remove `SkyCatalogService` / `GET /api/v1/catalogs[/{id}]`.** This entry's premise went stale: the service was subsequently REPURPOSED, not orphaned. Live consumers today: `TonightSkyService` takes `ISkyCatalogService` in its constructor (§36.8 ranking reads the OpenNGC catalog through it), and the `/api/v1/catalogs` endpoint group is the server half of the §36 Catalogs-overlay panel (toggleable Messier/NGC/IC + type-filter overlays with brightest-first caps) awaiting its client slice. What WAS actually dead — the Aladin-era client overlay (`skyAtlasCatalogProvider` in `catalog_overlay_state.dart`, whose only consumer `AladinView` was deleted in #611) — has been removed; the wire pieces it used (`CatalogObject` model + `DataManagerApi.getCatalog` against `/data-manager/{id}/catalog`) are kept as the contract the Catalogs panel will reuse.
- ✅ **Add a `flutter build macos` CI leg — DONE (client-build job).** Went further than the ask: a `client-build` matrix job compiles the native Runner in release mode on **all three desktops** (`flutter build {macos,linux,windows} --release`), so native link/plugin regressions on any shipping platform are caught pre-merge, not on-device. The Linux leg installs the desktop toolchain + `libwebkit2gtk-4.1-dev` (planetarium overlay) + `libsecret-1-dev`/`libjsoncpp-dev` (secure storage). The same PR dropped the stale `submodules: recursive` checkout from `client-test` (the `packages/webview_cef` submodule was removed with the #611 native-webview pivot) and refreshed the stale committed lockfiles (`Podfile.lock` checksum predated #611's Podfile; `pubspec.lock` sdk upper bound from the pinned 3.44.0 toolchain). (Carried over from the v0.1.0 Aladin/CEF state; also closes the #600-review follow-up at §36 "CEF 149 helpers" item (b).)
- **Faster WS reconnect after the server returns (optional).** `WsEventStream` backoff caps at 30s, so reconnect-after-restart can lag up to 30s. Consider capping the max backoff lower (5–10s) for a snappier comeback. (Resume-window abort + stale-guard are DONE — see CHANGELOG.)

## §68 AlpacaBridge integration — version gate REMOVED (2026-06-21)

**The §68.1 AlpacaBridge version gate was removed wholesale** (user decision, 2026-06-21: "Alpaca doesn't have a protection layer — it's supposed to be open by default"). ASCOM Alpaca is open by design and has no version handshake or `/version` endpoint (AlpacaBridge serves none), so the probe always failed and the gate was pure friction. The removal deleted the version probe, handshake service, gate, gate notifier, the 503/warn-band WS event (`equipment.alpaca_bridge_outdated_warn`), the client `AlpacaBridgeWarningBanner` + `alpaca_bridge_warning_state`, the gate wiring on every REST `/connect` handler + the boot auto-connect path, and all four server gate test files + both client banner/state tests. Equipment connects now go straight to the Alpaca client.

> **⚠️ Maintainer action — reconcile `PORT_PLAYBOOK.md` §68.** The playbook still specs the version gate (gate core, 503 connect-block, warn-band event, advisory banner). The playbook is user-authoritative — left for the maintainer to update §68 to "Alpaca is open; no version gate" rather than autonomously editing it.

The following former-§68 entries are now **obsolete** (the gate they depended on is gone): the §68.2 warn banner (#414), the §68.2 503 connect modal, the §68 gate-altitude confirmation, the §68 boot-path gate-duplication refactor, and the §68.5 server gate tests. These two non-gate items survive:

- **DONE (2026-07-02) — §68.2 wizard Screen 2 missing-bridge UX.** Implemented per playbook §68.2 (which the old wording here had drifted from — the gate is on a successful HANDSHAKE, i.e. a clean discovery response even with zero devices, not "a device is discovered"; with the §68.1 version gate removed, reachability IS the handshake): the probe auto-runs on entry, Next is gated via `wizardStepValidProvider` until it succeeds, failure shows the prominent "AlpacaBridge not detected." panel with the `sudo apt install alpaca-bridge` command + [Retry detection], and the playbook's [Skip to manual entry] escape requires the address override to be filled in. Discovery API construction moved behind `equipmentDiscoveryApiFactoryProvider` for the test seam; 4 widget tests cover happy/blocked/retry/skip/no-server.
- **DONE (2026-07-02) — §68.4 search entries, resolved via the Help registry.** The design call answered itself once §69 shipped: help entries ARE the "informational entry" kind. `Help` gained an optional `keywords` list, `buildSearchIndex()` now indexes the whole `helpRegistry` as informational hits (groupLabel "Help", no panel/setting id), and activating one in the command palette opens the §69 help sheet (refactored to a public `showHelpSheet` carrying its own Consumer, so it survives the palette popping) — making all ~60 help entries searchable, not just the new one. `equipment.alpacabridge.troubleshoot` added with the playbook §68.4 keywords + the §68.2 guidance (install command, systemctl diagnostic, subnet note, override pointer). `equipment.alpacabridge.version` is MOOT — the §68.1 version gate was removed (Alpaca has no version endpoint by design; user decision 2026-06-21), so there is no version to explain; recorded in the registry comment.

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

- **DONE (2026-07-02) — PR #71 filter-slot control.** The Imaging tab's exposure panel now has the Filter dropdown wired to `params.filterSlot`/`setFilterSlot` (until now only the hardcoded 'L' default ever went up as `filter_name`). Choices come from the profile's wheel slot labels — daemon-authoritative since the 12h.2b round-trip — with a stored value not among the labels kept selectable (the §38 picker's stance).

## CodeRabbit security findings addressed

- **PR #12** (port/ara → master promotion) — zizmor + CodeRabbit flagged `.github/workflows/ci.yml` for (1) mutable `actions/checkout@v4` reference, (2) missing `persist-credentials: false`. Both fixed on `ci-harden` branch. Future workflow steps added at Phase 0.5p / Phase 4 / Phase 11 / Phase 14 should follow the same pattern: actions pinned to a commit SHA + `persist-credentials: false` unless a step explicitly needs git push.

---

## Merge-gate audit follow-ups (2026-06-17)

A retrospective audit of merged PRs (the bot's verdict-at-merge vs. the §19.1 gate) found **no unaddressed high-severity defect** — the recall-biased reviewer's near-permanent "⚠️ Issues found" sign-off is not itself a defect signal, and every high-severity keyword in a final verdict was negated / avoided-by-design / an explicit non-blocker. It did surface these **low-severity, bot-acknowledged-non-blocker** hardening items, logged here and being remediated as focused follow-up PRs (each driven to the bot's ✅ Approved before merge):

- **DONE (verified 2026-07-02) — #384 dark-library build disposal/cancellation.** `GuiderService.DarkLibrary.cs` now handles the whole lifecycle: the shutdown CTS token is captured BEFORE the deferred lambda (a concurrent Dispose can no longer throw ObjectDisposedException in the background task and leak the gate), the token threads into the 2-hour `BuildDarkLibraryAsync` RPC so shutdown cancels it, the single-build gate releases in `finally` on every path (and in the Task.Run queue-failure catch), and background exceptions are contained + surfaced as the `*.failed` WS event, never an unobserved task exception. Landed with the guider a/c hardening.
- **DONE (verified 2026-07-02) — #390 WS drain error surfacing.** `ws_event_stream.dart`'s teardown now logs both failure paths ("Surface (don't silently drop) a teardown failure"): a faulted `sub.cancel()` and a faulted `socket.close()` each land in a `catchError` → `debugPrint`, and the onError path finalises the socket so a half-open TCP connection can't leak across reconnects.
- **DONE (verified 2026-07-02) — #425 backup `listSnapshots` drop logging.** `backup_api.dart` now throws on a non-array 2xx body (wire-contract break surfaces as an error state, not a silent empty list) and logs the dropped count when malformed/unusable entries (missing id or downloadUrl) are skipped.
- **DONE (verified 2026-07-02) — #349 image preview guards.** All three `SKBitmap.GetPixels` call sites in `JpegEncoder` (EncodeGray, EncodeThumbnail, RgbToBitmap) check for `IntPtr.Zero` and throw with a clear message instead of writing through a null pointer, and the full-preview path resizes via `PreviewMaxDim` (2048, `SqliteFrameRepository`) with `EncodeGray`/`EncodeColor` delegating oversize frames to the tested thumbnail-resize path.
- **CLOSED (2026-07-02) — #399 latent dialog error-drop: mitigated by a documented contract, not dead code.** The dialog's fire-and-forget sites carry the contract comment at the exact refactor point ("GuiderCalibrationNotifier._run wraps every action in try/catch and routes failures to state = AsyncValue.error… Keep that contract if _run is ever refactored"), which is the appropriate guard for a purely hypothetical future refactor — a `catchError` today would be dead code silencing nothing. Re-open only if `_run`'s error routing actually changes.

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
- **DONE (2026-07-02) — §28 frames-schema widening pass (the §40 prerequisite).** `exposure_seconds` is REAL end-to-end (`FrameDto`/`FrameListItemDto`/`DarkLibraryEntryDto` double; readers `GetDouble`; the §28.8 FITS rescan now parses `EXPTIME` as the double it is — a `0.5` header used to fail `int.TryParse` and record 0), and `Gain` is nullable end-to-end (the `Gain ?? -1` sentinel is gone; a camera/FITS that reports no gain records NULL). The old-schema upgrade runs as an **in-place rebuild on initialize** (SQLite can't ALTER COLUMN): DDL-sniff trigger (idempotent, fresh DBs skip), one-transaction recreate copying every row with legacy `-1` gains normalized to NULL, all three frames indexes recreated — proven by a test that hand-builds the v0.0.1 schema, seeds a sentinel row, and initializes the current build over it. Stats aggregations already CAST to REAL (unchanged); the Astrobin CSV formats sub-second exposures and blanks unknown gain; the openapi `Frame` schema already said `number`. Settings ints (imaging defaults / autofocus exposure) deliberately out of scope — they are user-entered whole seconds, not catalog measurements. **§40 calibration workflows are now unblocked.**

**§38 client editor — container depth (follow-ups to the container palette slice).** The editor can now add `Sequential`/`Parallel` containers, nest instructions inside them (daemon template shape, verbatim round-trip), ✅ **rename a container**, ✅ **edit its loop conditions** (`LoopCondition` iterations + `TimeSpanCondition` duration), and ✅ **edit its triggers** (`MeridianFlipTrigger` — catalog + nina_dom ops + state + field-panel Triggers UI). The container model is now structurally complete (child items + name + conditions + triggers), matching NINA's Advanced Sequencer.

**DONE (2026-07-02) — filter picker's list is daemon-authoritative (the 12h.2b round-trip).** `GET/PUT /api/v1/profile/filter-wheel/labels` now exists (`FilterWheelLabelsDto` profile section: slot-indexed, trimmed server-side, 1–32 slots, ≤64 chars; persisted in profile.json with older-file back-fill to the reference-8 default, which mirrors the client's in-memory default so first hydration is visually a no-op). Client-side, `FilterWheelLabelsNotifier` SELF-HYDRATES whenever the active server changes (generation-guarded microtask via `profileApiProvider`; failures keep the defaults so offline authoring still works) — so the §38 picker, the filter-set seed button, and the Settings panel all see profile labels with zero per-surface plumbing, exactly the "picker picks it up unchanged" outcome this entry asked for; the Settings panel persists per committed row with SnackBar failure surfacing. Original entry: The `SwitchFilter` filter picker now sources its filter names from the in-memory `filterWheelLabelsProvider` (the §37.4/§46.2 slot labels). These are app-local defaults until a daemon round-trip exists — there is currently no server endpoint returning the active profile's `FilterWheelSettings.FilterWheelFilters` (nor is the `/api/v1/profile/filter-wheel/labels` round-trip wired). The picker writes a minimal `FilterInfo` (`{$type, _name, _position}`) and the daemon's `SwitchFilter.MatchFilter` re-resolves it to the full profile filter by name, so the stored body is correct regardless; but the *list the user chooses from* should come from the daemon (profile filters, or the connected wheel's slot list via `FilterWheelService.GetAsync`). When that endpoint lands, hydrate `filterWheelLabelsProvider` (or a dedicated filter-list provider) from it and the picker picks it up unchanged.

**Field editor — conditional field relevance.** ✅ **Done** (#542): `InstructionField` gained an optional `enabledWhen(node)` predicate over the field's sibling node, and the editor greys-out + `Semantics(enabled:false)`/`IgnorePointer`-disables a field whose siblings make it inert. Applied to a `TimeCondition`'s Hours/Minutes/Seconds (grey under a sky-event provider) and `MinutesOffset` (grey in Custom-time mode). General mechanism (not a `_ConditionCard` special-case), reusable for any future relevance rule.

Remaining for fuller coverage: (1) **object-valued conditions** — ✅ **done**: `TimeCondition` (#542 — a `SelectedProvider` sky-event/clock-time picker via the `timeProvider` field type, clock H/M/S greyed under a provider) and the altitude conditions `AltitudeCondition`/`AboveHorizonCondition` (#543 — nested `WaitLoopData`: coordinates + comparator + offset, via the `waitLoopData` composite field type reusing the extracted `_CoordinatesEditor`). (2) ✅ **`ReconnectTrigger`** — done: catalogued as `Reconnect Device` with a `SelectedDevice` stringEnum over the grounded device-name set (`reconnectDeviceNames`), rendered via the trigger card. (3) ✅ **`TimeSpanCondition` field clamping** (was #531) — done: `InstructionField` gained `min`/`max`, bounded number/integer fields route through the clamped `_NumField`, and the loop conditions set bounds (TimeSpan minutes/seconds 0–59, hours ≥ 0; Loop iterations ≥ 1).
The §38 execution engine (`SequencerService`, #319) now **runs** sequences for real through NINA's inherited `Sequencer`. These items refine it as real equipment + data models land:

- ✅ **Pause / Resume — SERVER DONE (2026-07-02).** The headless engine gained a real instruction-boundary pause: `PauseGate` (`OpenAstroAra.Sequencer/Utility/PauseGate.cs`) hangs off the root container (`SequenceRootContainer : IPauseGateHost`) and both execution strategies await it — `SequentialStrategy` before every item's pre-triggers (so a trigger that ripened during a long pause, e.g. meridian flip, is evaluated fresh on resume) and `ParallelStrategy` at block entry. `SequencerService.PauseAsync` arms the gate (current instruction always finishes — NINA semantics); the run flips to `Paused` + emits `sequence.paused` only when the engine ACTUALLY suspends (the gate's `PauseEntered` callback), never on the mere request. `ResumeAsync` releases the gate + emits `sequence.resumed`. Abort/Stop/shutdown win over an active pause via token cancellation; the Running↔Paused transitions CAS (int-backed run state) so they can never clobber a concurrent Abort. Covered by `PauseGateTest` + 5 new `SequencerServiceTest` cases (suspend-at-boundary, abort-while-paused, pause-racing-completion, unknown/finished no-ops, shutdown-while-paused). **Client follow-up DONE (same day):** the WILMA toolbar re-offers Pause while running and relabels Run→Resume while paused (reverting #551's removal against the current toolbar, Skip button included); the WS fold already consumed the whole `sequence.*` prefix, so `paused`/`resumed` flow with no state-layer change. §38 Pause/Resume is complete end-to-end.
- **Shutdown completed-vs-stopped race** (flagged on #319, accepted). If a run finishes its execution at the exact instant `IHostedService.StopAsync` runs, the run can be marked `Stopped` (and emit `sequence.stopped`) rather than `Completed`. Accepted: it's a one-instant shutdown coincidence, the run did stop, and the §28.2 reconciler keys off the checkpoint (cleared by the worker's `finally`), so post-restart reconciliation is unaffected. A fully race-free fix would need a CAS/lock on the Running→Completed vs →Stopped transition; not worth the machinery for a shutdown-only edge. (The companion null-`Worker` window was *fixed* in #319 — the worker Task is now assigned synchronously in `StartAsync`.)
- **DONE (2026-07-02) — §60.9 terminal-without-`started` documented.** The ordering contract now lives in `openapi.yaml`'s Stream section: terminal MAY arrive without `started` (the accepted acceptance-window skip), `sequence.progress` is GUARANTEED never after the terminal event (the #667 seal+drain), and progress payloads are snapshots (coalescing). WILMA's WS handling merges payload fields per event without assuming started→terminal pairing (verified: `SequenceSummary.applyingWsPayload` is order-tolerant).
- **DONE (2026-07-02) — `frames_*` → `instructions_*` renamed while the wire had no external consumers.** The decision the entry asked for, executed inside the breaking-change window: `SequenceRunStateDto.InstructionsCompleted/InstructionsTotal`, WS payload keys `instructions_completed`/`instructions_total`, openapi schema, the startup-notification wording ("was at instruction X/Y"), and the WILMA parse/UI — all in one PR, both sides shipped together (pre-release, WILMA is the only consumer). A true exposure counter can arrive later as a separate `frames_*` pair without a second rename (recorded in the openapi ordering note). Upgrade edge: a §28 checkpoint written by a pre-rename daemon that crashes across the upgrade reads its counters as 0 (missing keys) — transient, self-corrects on the next run.
- **§28 checkpoint writes are synchronous per progress tick (from the #667 review).** `WriteCheckpointIfOwner` does `File.WriteAllText` + `File.Move` on every progress tick, on the Progress<T> callback thread. Deliberately untouched by the #667 back-pressure work (checkpointing has its own durability semantics — every tick's checkpoint matters for §28.2 crash recovery in a way WS snapshots don't), but now that ticks arrive at camera-capture rates the per-tick fsync-ish cost is worth measuring on the Pi; if it shows up, debounce the WRITE (keep the newest state) rather than reusing the publish pump — a checkpoint must never be older than the last emitted terminal event. (low)
- **DONE (2026-07-02) — progress-emit back-pressure.** The condition this entry waited on ("when the capture path lands") arrived with the §14e capture PRs, so the guard shipped: `CoalescingAsyncPublisher`, a single-flight trailing-coalesce pump — at most one `sequence.progress` publish in flight, a tick burst collapses into one trailing publish carrying the freshest run state (read at publish time), the first poke publishes immediately (no debounce latency at low rates), a throwing delegate can't kill the pump, and lifecycle events stay unthrottled. Poke-never-lost proven under an 8-thread hammer test; burst-coalescing and single-flight proven with gated fakes.
- **⚠️ Globalization-invariant gotcha (reference).** The AOT server container runs in **globalization-invariant mode** (no ICU). `new CultureInfo("xx")` for any *named* culture throws `CultureNotFoundException` there. Fixed in `OpenAstroAra.Profile/ApplicationSettings.cs` via a `SafeCulture(name)` helper (named culture when ICU is present, invariant fallback otherwise) — this was crashing `new Profile()` and thus the whole sequencer-factory construction in the e2e container (caught by the §38 execution-engine PR #319's `IHostedService` eager construction). **Any future inherited NINA code that constructs a named culture must use the same fallback pattern**, or it will crash the daemon. The server-e2e CI job exercises invariant mode, so such regressions fail CI.
- ✅ **Precise `frames_completed` + `current_instruction_index`** — **resolved in #320.** The worker flattens the deserialized tree to its leaf instructions and counts terminal-status (`FINISHED`/`FAILED`/`SKIPPED`) leaves for `frames_completed` + the first `RUNNING` leaf for `current_instruction_index`, on each `IProgress` tick + a final settle. `frames_total` = leaf count. Exact for linear sequences; looped containers reflect the current iteration (ties into the `frames_total`-vs-instruction-count API item above).
- ✅ **Profile source-of-truth (§14e PR20)** — `StoreBackedProfileService` replaces the `HeadlessProfileService` stub at the DI registration point: one live `Profile` hydrates from `IProfileStore` (profile.json) at startup and on every settings PUT (new `IProfileStore.Changed` event), via the unit-tested `ProfileStoreMapper` (site→astrometry, PHD2→guider, autofocus→focuser, storage→image-file incl. FileType, plate-solve numerics incl. arcsec→arcmin threshold, safety-policy meridian fields). Sections with no NINA-profile counterpart stay store-only (documented per mapping). Multi-profile management remains inert (ARA v0.0.1 is single-profile per §37).
- **CLOSED (verified 2026-07-02) — camera capture-block surface.** Half the entry went stale: `IsFreeToCapture` is real now (reads `_captureInFlight`, the same gate Live View and manual captures contend on). `RegisterCaptureBlock`/`ReleaseCaptureBlock` remain no-ops deliberately: repo-wide grep finds ZERO callers (they are inherited NINA-mediator surface whose consumers — WPF dockables — did not port), so there is no lifecycle to be consistent WITH. Re-open only if a ported instruction starts registering capture blocks; the no-ops would then need real block semantics tied to `_captureInFlight`.
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
  - **Guider follow-ups (§63, from the guider-a #345 self-review):** (1) ✅ **RMS** — *done in guider-a*: `GuiderService` accumulates raw RA/Dec errors from the `GuideEvent` stream into a bounded 200-step window and `GetAsync` reports `RmsTotal/Ra/Dec` (root-mean-square, pixels). ✅ *arcsec refinement done (2026-07-05)*: `GuiderStateDto` gained additive `RmsTotalArcsec/RmsRaArcsec/RmsDecArcsec` (pixel RMS × the guider's reported `PixelScale`; null until PHD2 reports a positive scale — never a fake 0″). (2) **Terminal-status surface for guide ops**: `StartGuiding`/`StopGuiding`/`Dither` are fire-and-forget `Task.Run` returning 202, so a `Dither` rejected because the guider isn't yet GUIDING (returns false, no exception) is indistinguishable from success to a polling client — the proper fix is a §60.9 WS event (`guider.dither.{complete,rejected}` etc.) once the WS guider channel lands, not a synchronous result. (3) **Arg-passing via shared profile**: `ConnectAsync`/`DitherAsync` write Host/Port/DitherPixels onto the singleton `ActiveProfile.GuiderSettings` that `PHD2Guider` reads asynchronously — racy under *concurrent* guider requests, but mitigated by §27 single-client for v0.0.1; a cleaner design passes these as method args when the guider mediator unification (guider-c) reworks the surface. (4) **`IGuiderMediator` unification** (guider-c) replacing `HeadlessGuiderMediator` so the sequencer's StartGuiding/StopGuiding/Dither drive the live guider.
  - **Multi-instance equipment (`/equipment/{type}/{n}`) — Switch is the pilot.** ✅ **PR1 (server) done:** `SwitchService` went single-instance → keyed-by-`AlpacaDeviceNumber` so multiple Switch devices connect at once (`GET /switch` list + `/switch/{n}` get/disconnect/value; `SwitchDto.alpaca_device_number`; mediator targets the lowest-numbered "primary"; cross-host number collision replaces + logs). ✅ **PR2a (client data layer) done** (#557): `SwitchDevice`/`SwitchPort` models, `SwitchApi`, `switchListProvider`, `EquipmentDeviceType.switchDevice`, `DiscoveredDevice.toConnectRequestJson`. ✅ **PR2b (client UI) done:** `equipment_switch_panel.dart` (Settings → Equipment → Switch) — list of connected switches + per-port controls (boolean toggle / value slider / read-only) + Disconnect + an "Add switch" button (chooser `onPick` connects an additional device). **Deferred from PR2b:** the **wizard discovery Switch slot** stays disabled — the wizard models one device id per type (`switchDeviceId`), which doesn't fit multiple switches; multi-switch is managed in the Settings panel instead (wiring the wizard for a switch *list* is a separate follow-up if wanted). ✅ **PR3 (sequencer) done (2026-07-05):** `SetSwitchValue` gained a `[JsonProperty] AlpacaDeviceNumber` (default `-1` = primary — the value every pre-PR3/NINA sequence deserializes to, so no migration needed) routed through the new ARA-owned `ISwitchDeviceTargeting` capability interface (NOT added to the NINA-inherited `ISwitchMediator` — that file is ISO-8859-1 and the inherited surface stays untouched; the instruction type-checks and falls back to the single-target path). `SwitchService` implements it (`TargetConnectionLocked`: `-1` → lowest-numbered primary; `>= 0` → exact connected match only — a named-but-absent device degrades to the logged no-op, never a fallback to a DIFFERENT hub); Validate reads the SAME device Execute writes. Client: `SetSwitchValue` catalogued (new Switch palette category; device / writable-port / value fields). Dead `PlaceholderSwitchService` removed. **(Later)** multi-switch remember+auto-connect (`EquipmentSelectionStore` is one-device-per-type — multi-per-type for Switch + an `EquipmentAutoConnectService` Switch case); generalize `{n}` multi-instance to the other device types per §10.6 as real multi-device rigs need them; equipment `switch.*` WS events (no device emits equipment WS events yet — cross-cutting); a friendlier device/port PICKER in the sequence editor (live-queried names instead of raw numbers) once an editor-side equipment lookup exists.
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
- **Distributed (daemon) deps**: all permissive (MIT / Apache-2.0 / BSD / MS-PL / ISC / public-domain) EXCEPT one inherited-from-NINA non-MIT one, compatible: **Accord.NET (LGPL-2.1)** — separate dynamically-linked .NET assemblies, satisfies LGPL's relink allowance. (The **FreeImage**/VVVV.FreeImage dependency was removed as dead weight — the RAW-converter that used it was deleted in the port, the §2105 render path is SkiaSharp, and only a vestigial enum label remained; dropping it also removed a shipped-but-unused .NETFramework native lib + the NU1701 warning.) **No GPL/AGPL/SSPL/commercial-licensed code in the distributed daemon.** Called out in `NOTICE.md`.
- **Test-only deps with license caveats (NOT distributed)**: `FluentAssertions` is pinned `[7.0.0]` — deliberately the last Apache-2.0 version before v8 went commercial (Xceed paid); `Moq 4.20.72` is post-SponsorLink. Both fine + test-only.
- ✅ **Action remaining (release-time only) — DONE (2026-07-01)**: `scripts/generate-3rd-party-licenses.py` generates the repo-root `3rd-party-licenses.txt` from the daemon's full transitive NuGet graph (98 packages; SPDX expressions from the nuspecs + a curated OVERRIDES table for the 18 pre-SPDX legacy packages, each verified upstream). CI's `server-build` job runs `--check` right after restore so a dependency bump that forgets to regenerate fails fast, and `build-deb.sh` ships LICENSE.txt + NOTICE.md + the notices at `/usr/share/doc/openastroara-server/`. ✅ Sibling task DONE (2026-07-05): the **WILMA client's** third-party notices ship in-app — Settings → System → About (the `app.changelog` panel is now real: identity + version via package_info_plus + license posture + repo link) opens Flutter's built-in `showLicensePage` over `LicenseRegistry`, which the build tooling populates with every bundled package's LICENSE automatically, so the notices stay correct as dependencies change with no generated file to forget.

## §65 JpegEncoder.EncodeThumbnail null-resize guard ✅ DONE (verified already present, 2026-07-05)

Stale entry: the grayscale `EncodeThumbnail` carries the identical `Resize(...) ?? throw
InvalidOperationException` guard as `EncodeColorThumbnail` (JpegEncoder.cs — both resize call
sites) — it landed with the #349-era guard sweep despite this entry surviving. Nothing to do.

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
- **guider-e §63.5 param push — ✅ DONE (verified stale 2026-07-05).** This entry predated #372: the profile HAS the §63.5 fields (`Phd2SettingsDto` guide focal length / pixel size / RA-Dec aggressiveness / min-move / dec-guide-mode, normalized in `StoreBackedProfileService.ApplyPhd2`), the on-connect push shipped (`PHD2Guider.GuiderEngineConfig.cs`, #372), profile-name mapping (e-3a/b/c) and the dark-library push (e-4) followed, and the client exposes every field (Settings → Equipment → Guider panel + settings registry). Only the LIVE integration run remains — hardware-gated (see the physical section above).

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
- ✅ **Live focuser V-curve sweep — ORCHESTRATION BUILT (2026-07-02); live validation still focuser-gated.**
  `AutofocusSweepService` (implements the sequencer's new `IAutofocusExecutor` seam): steps ±Steps·StepSize
  around the start in ONE direction (outermost first → every sample carries identical backlash bias, final
  approach also from above), probes each position through the §59 `IAnalysisFrameSource` capture seam
  (same device path + in-flight gate as real captures, nothing persisted), `StarDetector` HFR per probe
  (min 3 stars or the sweep fails — no junk points into the fit), `FocusCurveFit.FitBest`, and moves to the
  best only when the fit is usable AND within the sampled range. Failure/cancel restores the start position
  per the profile's `RestorePositionOnFailure`. Wired into: `RunAutofocus` (imported NINA plans now focus for
  real), the factory prototype, and the meridian flip's `AutoFocusAfterFlip` (a failed re-focus logs + continues
  per §58.7, never aborts the night). Fully unit-tested with mocked equipment + an injected metric (8 tests:
  single-direction probing, vertex recovery, disconnected/invalid-config/starless/flat-curve/capture-failure/
  cancellation × restore policy). **Still owed:** live end-to-end validation on a real focuser (the dev rig is
  camera-only — the original physical blocker stands for VALIDATION, not the build), §59.7 backlash
  auto-discovery, and the `AutofocusAfterExposures` trigger's own wiring check.
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

Also follow-ups: ✅ **§58.7 failure notifications — DONE (2026-07-05)**: `NotifyCritical` generalized to
`Notify(severity, …)`; a failed flip now notifies **Critical in BOTH modes** (previously only under the §58.9
safety layers — a safety-off failure halted the sequence silently), with the guider-restore outcome folded in
("could NOT be resumed" when the best-effort resume also failed); the best-effort re-center failure notifies
Error ("imaging continues on an unverified pointing") and a failed post-flip autofocus notifies Warning
("focus may have drifted"). All best-effort — a notification-store fault never masks the flip fault.

✅ **§58.8 first-flip 60 s confirmation — DONE (2026-07-05)**: `SafetyPoliciesDto.FirstFlipConfirmed`
(default false — every existing profile announces its next flip once); the executor announces the
profile's FIRST flip with a Critical notification and a proceed-on-timeout window (`FirstFlipConfirmWait`,
spec 60 s, test-shrinkable), runs AFTER the Layer-1 flight check (announcing a flip Layer 1 would refuse
is noise) and BEFORE any state is touched; the user's "no" is stopping the sequence (cancel propagates,
flag stays false, next attempt announces again); the elapsed window persists `first_flip_confirmed: true`
via a fresh read-modify-write (a policy edit during the wait isn't clobbered). **Reset on rig change**:
both profile stores clear the flag when the optics train genuinely changes (identical re-save keeps it) —
optics is ARA's profile rig identity (mounts are Alpaca-discovered). Independent of `flip_safety_enabled`.

Remaining: **§58.10 unattended-hours
severity escalation** (+ §58.12 shutdown countdown), and the **re-focus-after-flip** step (gated on the live focuser AF V-curve sweep, itself a
focuser-hardware blocker). Of the §58 profile block only `refocus_after_flip`/`guider_recal` mode enums
remain un-modelled (they land with the refocus/recal orchestration they configure); `first_flip_confirmed` shipped with §58.8.

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
sentinel). Flats match by `filter` only (correct).

### §39.5 matching flats — plan → runnable sequence ✅ DONE (2026-07-02)
`GenerateMatchingFlatsAsync` now materializes the plan into a persisted §38 sequence via `ISequenceService`
(`CalibrationSequenceBuilder` emits the NINA-verbatim body dialect): per light filter, a looped
SequentialContainer of [SwitchFilter (name-resolved, `_position:-1` so a stale slot index never wins) →
MoveFocuserAbsolute (the session's modal per-filter focus, §39.5 dust-shadow alignment) → TakeExposure(FLAT)
carrying the lights' modal gain/offset]. `GeneratedSequenceId` is the real stored id (endpoint: 201+Location;
`GenerateOnly` → null id, 200). Proven by deserializing the stored body through the real sequencer factory.
Deferred: a flat-device instruction (panel on/brightness/auto-ADU exposure — generated TakeExposure uses a 1 s
starting point, TargetAdu stays advisory) and §39.6 cooler-temp replay; both need new sequencer instructions.
### §39.8 dark library — catalog-backed + generated build sequence ✅ DONE (2026-07-02)
`SqliteDarkLibraryService` replaced the fixture placeholder: entries ARE the catalog's DARK frames grouped by
the §39 matching key (exposure, gain-nullable, whole-degree temp bucket; stable SHA-derived entry ids; group
byte totals; newest frame as representative path). The build generates a runnable §38 dark-matrix sequence via
`CalibrationSequenceBuilder.BuildDarkLibraryBody` (CoolCamera per set-point — empty temp list = ambient, no
CoolCamera; looped TakeExposure(DARK) per combination; empty gain list = camera default) and persists it
(`calibration:dark-library`); `DarkLibraryStateDto.GeneratedSequenceId` surfaces it. Status is coverage-based
(a combination completes when the catalog holds >= FramesPerCombination matching darks; completion stamped at
first observation). `ReuseExistingFrames` skips already-covered combinations. Wire changes (no client consumer
yet): build request exposures int->double (§28), entry Gain int->int? (the #670 review note), state +
GeneratedSequenceId. Deferred: `calibration.dark_library.*` WS events (the generated sequence already emits
standard §60.9 run events) and server-side master-dark stacking (§39.2 capture-only philosophy — external
stackers consume the raw frames).
### §39.10 calibration client surface ✅ DONE (2026-07-02)
Full-screen Calibration route (bottom status bar, beside Image Library/Stats) live over
`/api/v1/calibration/*`: Sessions tab (per-filter summaries, flats/darks coverage badges, the §39.5
[Capture Matching Flats] dialog -> generate -> select the sequence -> jump to the Run tab) and Dark
Library tab (catalogued groups incl. null-gain/sub-second rendering, coverage status header with
[Open build sequence], the §39.8 matrix build form). New `CalibrationApi`/models/state follow the
sequence-list factory->api->notifier idiom. Remaining §39 client work: the Image Library session
card's stubbed [Capture Matching Flats] button waits on 12f.2 (library live-wiring gives cards real
session ids); WS auto-refresh SHIPPED (frame.complete now actually EMITTED on capture insert — it was
catalogued but never published — and the library/calibration/dark-library notifiers refresh on it,
debounced 2 s); cursor paging on the
sessions tab (first page at the server's 200 cap today — from the #673 review).
### 12f.2 Image Library live-wiring — sessions/frames/thumbnails + flats button ✅ DONE (2026-07-02)
`ImageLibraryScreen` now renders `/api/v1/sessions` + `/sessions/{id}/frames` via the new
`LibraryApi`/`live_library` models/state trio (autoDispose, last-issued-wins refresh): live session
cards (light/calibration counts, filters), lazily-loaded frame strips with capture-time thumbnails
(`/frames/{id}/thumbnail`, placeholder on 404), the shared §39.5 [Capture Matching Flats] dialog
wired with REAL session ids, and a wire-truth `LiveFrameViewerScreen` (pinch-zoom thumbnail +
list-endpoint metadata; the demo `FrameViewerScreen` deleted). Demo `librarySessionsProvider`
remains ONLY for the Stats dashboard (§50 live-wiring is its own slice). 12f.3b bulk ops SHIPPED (Rate/Tag/Delete live against `/frames/bulk/*` with confirm/star/tag dialogs;
Move-to-session + Export stay disabled — no server endpoints). §40.6 Resume Target SHIPPED
(server ResumeTargetAsync real: recorded sequence_json re-persisted when present/valid, else per-filter
modal LIGHT synthesis via `CalibrationSequenceBuilder.BuildResumeTargetBody` with original frame counts,
OverrideSequenceId echoed; endpoint 201+Location; library button -> Run-tab jump. Slew/center steps are
the user's to add — per-frame plate-solve coordinates aren't in the catalog). 12f.3 pills SHIPPED
(filter/rating pills narrow the frame strips, target search hides sessions, active pills highlight +
Clear escape hatch). §50 demo-data retirement SHIPPED: the Stats CSVs stream from `/api/v1/stats/export/csv`
(scope=sessions now a real per-session rollup server-side; scope=frames the full table), and the
12f.1/12g.2 in-memory demo sessions are deleted — every Stats/Library surface is catalog-backed.
§65 stretched previews SHIPPED in the viewer (palette picker -> POST /frames/{id}/preview full-res
render, thumbnail as instant first paint, fetch-generation guard). In-viewer star rating SHIPPED (optimistic single-frame
edit via the §40.8 bulk endpoint; strips invalidated after). In-viewer tag editing + detail
metadata SHIPPED (GET /frames/{id} detail fetch: gain/offset/sensor/focus/size rows + tag chips
with add/remove via the single-id bulk reuse). Cursor paging SHIPPED (Load-more on the library +
calibration session lists via the shared CursorPage envelope; frame strips stay first-page-only by
design). Move-to-session SHIPPED (POST /frames/bulk/move,
target-session existence 422-guarded; bulk bar picker over the loaded sessions). §65.9 manual sliders SHIPPED
(the baked v0.0.1 design: 200 ms debounced server round-trip; black/midtone/white 0-1 with the
profile-seed defaults; client-side real-time stretching stays v0.1.0). Export SHIPPED (POST
/frames/bulk/export streams a tar of the selected FITS via System.Formats.Tar — missing files
skipped, name-collision dedupe, 404 when nothing exportable; client saves via the OS dialog with
an injectable saver for tests). THE §40.8 BULK BAR IS COMPLETE. In-memory tar assembly is fine at
selection scale; the streaming writer SHIPPED (open-handles-first
FrameExportPrep: TOCTOU skip at open time before any tar bytes, count header from successful opens,
entries streamed straight into the response via the Results.Stream callback — no MemoryStream, no
rollback; path resolution batched into one IN(...) query, closing the #686 N+1 note for export).

### ✅ DONE (2026-07-03) — `temperature_c` sentinel pass (from the #681 review)
Nullable end-to-end: DDL dropped NOT NULL (in-place rebuild keyed on the stored DDL; rows copied
VERBATIM — a legacy 0.0 cannot be told apart from a real freezing-point reading), FrameDto double?,
RegisterFrameAsync + the FITS rescan record NULL when the camera/header reports nothing. The §39
matching/bucketing queries use ROUND(COALESCE(temperature_c, 0), 0) so NULL and legacy 0.0 share a
bucket — the documented uncooled-match semantics hold across both generations (tested). Astrobin
cooling + frame viewer blank unknown; focus-temp chart filters NULL. This pass also introduced the
**schema_version table** (=3, the #670 review commitment) for future passes to key on. Superseded
entry follows for history:

### (superseded) §28-style follow-up — `temperature_c` NOT NULL + 0.0 sentinel (from the #681 review)
`frames.temperature_c` is NOT NULL and `CameraService.RegisterFrameAsync` coalesces a missing CCD
temperature to 0.0 — the same fabricated-sentinel anti-pattern §28 removed for gain (#670), now
user-visible as "Sensor: 0.0°C" in the frame viewer. Widening to nullable end-to-end is a dedicated
pass like #670 (column rebuild, FrameDto, readers) **plus a design decision**: the §39 dark-matching
rules deliberately exploit the sentinel ("uncooled lights/darks still bucket-match at 0.0" —
documented + tested in SqliteCalibrationServiceTest). NULL-matching semantics must replace that
(NULL matches NULL, as filters do) before the sentinel goes. Client is already nullable-ready
(`LibraryFrameDetail.temperatureC` is `double?`, renders unknown as "—").

### §39 calibration — ListSessions is O(N) queries per page (from #370 review)
`SqliteCalibrationService.ListSessionsAsync` runs `BuildSessionDtoAsync` per session = 4 queries each (header,
per-filter, flats EXCEPT, darks EXCEPT). A 50-session page ≈ 201 queries. Acceptable at v0.0.1 scale over the
embedded SQLite file (in-process, sub-ms each → tens of ms for a page), but a catalog with hundreds of nights
would benefit from batching the per-filter + coverage queries via `IN ($ids)` / a single GROUP BY pass and
assembling the DTOs in code. Same N-query shape in `SqliteDarkLibraryService`'s coverage loops (one COUNT per requested combination — fine at real dark-matrix sizes, from the #672 review). Defer until catalog sizes warrant it. The cursor is also a plain integer OFFSET (same pattern as the §28 frame repo), so a session inserted between page fetches can repeat/skip a row; the eventual keyset-pagination migration over captured_utc covers both.

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

**OBSOLETE (2026-07-02) — §36 CEF 149 sandbox hardening: no longer applicable.** The #611 planetarium pivot removed CEF/`webview_cef` entirely; the planetarium now runs in the OS-native WebView (WKWebView / WebView2 / WebKitGTK), which carries the platform's own renderer sandboxing, so neither hardening track below can or needs to happen. Kept for history. Original entry: The CEF 149
Sky Atlas runs with **no macOS App Sandbox** (host + helper) — CEF's browser process registers a PID-suffixed *global*
Mach bootstrap name for the helper rendezvous that `bootstrap_check_in` denies under the sandbox, aborting
`CefInitialize` — **and** no Chromium renderer sandbox (`webview_cef` sets `CefSettings.no_sandbox = true`, a plugin
default). So the webview has zero OS/Chromium sandboxing. Accepted for now: Developer-ID desktop control app, fixed
bundled offline Aladin page, only outbound CDS HiPS tile fetches. Two future-hardening tracks: (a) if a future CEF/plugin
release makes the rendezvous name static (or offers a sandbox-compatible transport), re-enable `app-sandbox` on host +
helper (and flip the entitlements-test guard back); (b) enable Chromium's own renderer sandbox by linking `cef_sandbox`
+ a sandboxed helper variant — independent of (a). See `client/.../macos/CEF_HELPER.md`. Surfaced 2026-06-24 by the
CEF-149 OSR review; nothing tracks the migration except this entry + the entitlement comments.

**DONE (2026-07-02) — §36 Data Manager DeleteAsync no longer conflates "not installed" and "permission denied".** `DeleteAsync` now returns `PackageDeleteResult` (Deleted / NotInstalled / Blocked — the delete-raced `DirectoryNotFoundException` maps to NotInstalled, the locked/permission-denied `IOException`/`UnauthorizedAccessException` family to Blocked), the endpoint maps Blocked to a 409 problem ("files are in use or protected — retry"), and the client `delete()` turns the 409 into a typed `PackageDeleteBlockedException` whose message surfaces in the modal SnackBar — so a locked dir reads "retry later", never "already clear". POSIX-gated read-only-parent test covers the Blocked path. Original entry: Both an
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
- **OBSOLETE (2026-07-02) — build-time guard for un-provisioned macOS CEF: CEF was removed by the #611 native-webview pivot** (and `scripts/provision-cef-macos.sh` was deleted in the Aladin-scrub PR), so there is nothing to provision. Original entry: If a developer runs `flutter build macos` without
  first running `scripts/provision-cef-macos.sh`, the failure is a cryptic linker error (missing CEF framework symbols).
  A short pre-build phase (or a check in the Podfile `post_install`) that detects an un-provisioned `macos/third/cef` and
  emits a human-readable `error:` pointing at the script would improve DX. Low priority. Surfaced 2026-06-15 by the #463 review.
- **OBSOLETE (2026-07-02) — CEF multi-process helper DX/CI follow-ups: CEF and the `packages/webview_cef` submodule were removed by the #611 native-webview pivot**, so neither follow-up has a target. Original entry: (1) **CI guard for the sandbox repoint** — `add_helper_target.rb` re-points the Helper target's `CODE_SIGN_ENTITLEMENTS` to the plugin's non-sandboxed default each run; a `git`-grep/`diff --exit-code` CI check asserting the three Helper configs still point at `Runner/Helper.entitlements` would catch a re-run that forgot the manual repoint (currently mitigated by the self-contained two-step command in `macos/CEF_HELPER.md`). It's a `.github/workflows` change → its own infra PR. (2) **Missing-submodule pre-build error** — the Helper target's `INFOPLIST_FILE` points into the `packages/webview_cef` submodule; a first build without `git submodule update --init` fails with a confusing Xcode error. A pre-build shell phase that checks the path and aborts with a clear `error: run git submodule update --init packages/webview_cef` would improve DX. Both low priority.
- **OBSOLETE (2026-07-02) — Windows CEF main.cpp tweak: CEF was removed by the #611 native-webview pivot** (Windows renders via WebView2). Original entry: When the Windows build is first exercised,
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
- **§70 import — distinguish repeated-import profile names — RESOLVED.** `ProfileShareService.ImportedName` now derives a
  non-identifying label from the `rig_description`'s effective focal length ("Imported — 1626 mm rig") when the share file
  carries no donor display name (the current export omits it per the strip-all privacy policy
  [[project-profile-share-path-policy]]), falling back to "Imported profile" only when the rig has no usable focal length;
  the chosen name is then de-duplicated case-insensitively against the existing profiles (suffixing " (2)", " (3)", …) at
  both preview and commit, so repeated imports of the same template no longer collide. Original gap (flagged by the #489
  review, minor / follow-up): every imported profile was named "Imported profile" because the export strips the donor block.
- **§70 import — `DroppedFields` can drift from the export's strip logic.** `ProfileShareService.DroppedFields` (the list the import preview shows the recipient as "you must re-enter these") is a hand-maintained static mirror of what `ExportAsync` actually strips (§70.1: equipment, paths, location, PHD2, notification tokens). Both sides were authored together so they agree today, but if the export's strip set changes, the import's advisory list won't follow automatically. Low severity (advisory text only, no runtime effect), but worth deriving both from one source — or at least a paired test asserting the export's stripped categories match `DroppedFields` — when §70 next gets touched. Flagged by the #489 review.
- **§37 wizard — null=keep-base mappers can't clear a field back to empty.** The wizard draft→section mappers (applyDraftToPlateSolve and the others) use `copyWith`'s `?? base` null-preserves-base semantics, so a blank field inherits the base profile's value. Intended for the create-new-profile flow (you set values, you don't clear them), but it means re-running the wizard on an existing profile can't blank a path/value back to empty — only overwrite it. If the wizard ever becomes a full editor (vs. create-only), add an explicit "clear" affordance. Flagged by the #492 review.
- **§37 wizard — per-screen validity gate — RESOLVED.** The shell now disables Next / Save Profile while the current screen has a blocking inline validation error (tooltip explains why; Back/Skip stay available). Implemented via `wizardStepValidProvider` that the validated capture-setup screens publish to and `WizardShell` reads; the controller resets it on each step change. Original gap (flagged by the #492 review): screen 11 had the first inline validation but no `canAdvance` hook, so the user could advance past a visible red error (safe — invalid values are never written to the draft — but deceptive).
- **§37.5 wizard — safety screen is the section-mappable subset only.** Wizard screen 15 (#~495 follow-on) maps to the profile safety section's `onUnsafe` / `autoResumeWhenSafe` / `resumeDelayMin`. The §37.5 placeholder also envisioned **weather-type-granular** actions (separate clouds / wind / rain → Pause/Abort+Park/Ignore), a wind threshold, a WILMA-offline auto-abort timer, and **alarm sound/vibrate** — none of which are fields on the `SafetyPolicies` section DTO. To ship those, the server's safety section needs the extra fields first (then a wizard + Settings → Safety follow-up). For now the wizard sets the general unsafe-conditions reaction; the granular/alarm bits stay Settings/§51-only.
- **§37.5 wizard — site/altitude screen maps horizon + twilight only.** Wizard screen 16 maps the draft's hard-minimum altitude → `SiteSettings.defaultHorizonAltitudeDeg` and a twilight choice → `SiteSettings.twilightDefinition`. The §37.5 placeholder also wanted a *separate* soft-warning altitude and a max-sequence-runtime cap — neither is a field on the site (or any) section DTO. Add them server-side (site section or a new sequencing-limits section) before wiring a wizard/Settings control. Flagged by the screen-16 build.
- **§37.6 wizard sky-data — no auto-checked "recommended preset".** Screen 17 lists the not-installed packages and lets the user tick what to download (queued post-save). The §37.6 sketch wanted a default-checked "Famous Targets + Star Catalogs" preset + rig-specific surveys (§36.10). The `DataPackage` DTO has no "recommended" flag, and the per-profile `/sky-data-recommendations` endpoint needs a profile id that doesn't exist until Save — so the wizard can't pre-check a curated preset. Add a `recommended`/`category=preset` flag to the package DTO (or expose recommendations pre-create) to enable the default-checked preset. Flagged by the screen-17 build.
- **§38 editor Save — verify the daemon accepts a promoted plain-array `Items`.** `nina_dom.withChildren` always emits the canonical ObservableCollection wrapper (`{$type, $values}`), even when the source node carried a plain-array `Items` (which `childrenOf` tolerates). The engine round-trips correctly on the Dart side (test added), but before the Phase 3 Save UI wires `updateSequence`, confirm the daemon's `SequenceContainerCreationConverter`/schema accepts the promoted wrapper form for a body that originally stored `Items` as a bare array — otherwise editing such a node would 422 on Save. Almost certainly fine (the daemon's own templates use the wrapper shape), but it's an untested cross-boundary assumption. Flagged by the #514 review (✅-approved, deferred to Phase 3).
- **§38/§28 — CenterAndRotate EXECUTES its centre half (ICenteringExecutor, 2026-07-02); rotation + RunAutofocus still owed.** `ICenteringExecutor` (Sequencer, mirrors the `IMeridianFlipExecutor` seam) is implemented by `CenteringService` (partial, §8.1 one-singleton pattern) and wired through `HeadlessSequencerFactory.WithDefaults` → an imported `CenterAndRotate` now drives the real §28 solve→sync→re-slew loop. Semantics: non-convergence FAILS the step; a CONNECTED rotator still fails loudly (rotation not wired — silently mis-framing a rotator rig is worse); with no rotator the position angle is preserved-but-skipped, matching NINA's own no-rotator behaviour. Remaining: (1) rotation fidelity — solve-driven rotator moves (ties into the framing position-angle entry below); (2) **RunAutofocus execution is blocked on the §59 LIVE AUTOFOCUS SWEEP, which does not exist yet** — §59 shipped the curve-fit math (#359–#361) but no orchestrator (move focuser → expose → HFR → fit → move-to-best); even `MeridianFlipExecutor` skips its AutoFocusAfterFlip with a warning. Building that sweep service unlocks RunAutofocus, AutofocusAfterExposures, and flip re-focus in one stroke.
- **§38 editor — Run Autofocus + Center instructions are not ported as sequencer classes.** The drag-and-drop instruction catalog (`instruction_catalog.dart`) only lists instructions that exist under `OpenAstroAra.Sequencer/SequenceItem/` (an unknown `$type` deserialises to a skipped `Unknown*` placeholder on the daemon, so faking a `$type` would silently no-op). NINA's `RunAutofocus` (Autofocus) and `Center`/`CenterAndRotate` (Platesolving) instructions have **no** equivalent class in the Sequencer project yet, so they're intentionally absent from the palette even though the §38 directive lists "focusing" and "Center". Focusing is partially covered by `MoveFocuserAbsolute`/`MoveFocuserRelative`. To add Autofocus/Center to the palette: first port the instruction classes (they wrap the existing §59 AF and §28 CenteringService), then add catalog entries. Flagged during the #514-follow-on catalog build.
- **§38 editor catalog — release-mode message on a non-String-keyed default.** `instruction_catalog._deepCloneJson` keeps a debug `assert(value is Map<String,dynamic>)` for a clear message; in release that assert is stripped and the fail-fast becomes the bare `entry.key as String` cast (an opaque `TypeError` with no context, NOT the assert message). The whole catalog is `const Map<String,dynamic>` so this can't trigger today; if Phase 2 ever generates `InstructionDef`s with non-const map defaults, convert the assert to a release `throw` with a descriptive message. The same release-vs-debug note applies to `InstructionField`'s `enumLabels`/`enumValues` XOR assert, which can't become a release `throw` without making the constructor non-`const` (which would break the const catalog). Flagged by the #515 review (low/future-risk, no current bug).
- **§38 editor — make container `build()` populate an empty `Items` wrapper.** `nina_dom.isContainer` classifies a node as a container by an `Items` key or a `.Container.` `$type`. Today every container in a body has `Items`, and the only catalogued types are leaf instructions, so this is robust. But when the instruction catalog grows **container** entries (loops/conditional containers), `InstructionDef.build()` should always emit `Items: {$type: itemsWrapperType, $values: []}` for them, so a freshly-built empty container is detected by the structural `Items`-key branch and the `.Container.` string-scan stays a fallback for raw imported nodes only. Flagged by the #516 review (forward-looking; no current bug — no container catalog entries exist yet).
- **§38 field editor — make text fields controlled when undo/reset lands.** `SequenceFieldEditor._FieldControl` uses an uncontrolled `TextFormField(initialValue:)` reseeded by a `ValueKey(path/field)` on selection change. Correct for the current scope (the only way a field's value changes while open is the user typing into it), but if a future action mutates the selected node's value *without* changing the selection — undo/redo, "reset to defaults", or a sequence reload that keeps the selection — the visible text would go stale. At that point switch to an explicit `TextEditingController` synced from the provider in `didUpdateWidget` (guarding against clobbering in-progress typing / cursor jumps). Flagged by the #519 review (forward-looking; reviewer confirmed the ValueKey approach is correct for now).
- **§29.9 logs — tail does a full-file scan; no pagination.** `LogService.TailAsync` reads each rolling log file line-by-line (one string alloc per line) on every request, walking newest→oldest only until the line cap fills. Fine for an occasionally-hit diagnostic endpoint, but the worst case is a ~50 MB read + thousands of allocations per call, and there's no cursor/offset to page backward beyond the requested cap. If the §54 Logs panel ever live-streams or paginates, add a reverse byte-scan (seek from EOF) or a persisted line index, and a continuation token. Flagged by the #545 review (perf / future work, no current bug).
- **§54 bug-report — client PII disclosure — RESOLVED (client bug-report card).** The §54 Support tab's "Send me a bug report" card now shows a PII disclosure dialog (logs + full profile incl. possible credentials/coordinates/tokens + filesystem path, with approximate size) and only passes `acknowledge=pii` on explicit confirmation — satisfying the server-enforced gate at the UI layer. The "scrubbed export" option below remains a possible future nicety. Original entry kept for context: The server `BugReportService` ZIP bundles three things, each potentially sensitive: (1) `system-info.json` with `profile_dir` (the absolute path can carry the OS username, e.g. `/home/alice/...`); (2) the daemon's recent **log files**; and (3) the **full `profile.json`** — which may hold equipment credentials, precise observatory coordinates, network endpoints, and notification tokens. All deliberate (the user downloads it themselves for diagnostics). **Server-side backstop now in place (#546):** the download endpoint requires `?acknowledge=pii` and returns `403` without it, so a client physically cannot download a bundle without going through the acknowledgement — which forces the disclosure to exist. The remaining **client-UX work**: the §54 "Send me a bug report" UI must spell out *what* is inside (logs + full profile + system info incl. filesystem path) and only pass `acknowledge=pii` after the user confirms. The server fields stay; this is the client follow-up (no longer a pure honour-system gap, but the user-facing disclosure text is still required before a public/non-dev build). (A future option: a "scrubbed" export mode reusing the §70 profile-share strip logic for users who want to share without secrets.) Flagged by the #546 review.
- **§54 client — daemon-log download buffers the whole file in memory.** `LogsApi.downloadLog` reads the file into a single `Uint8List` then hands it to `FilePicker.saveFile(bytes:)`. Bounded by the §29.9 sink's 50 MB/file roll cap, so fine on desktop, but on a low-RAM/mobile host a near-maxed log spikes memory (file bytes + the Dio response buffer). If a mobile target ships, stream the download straight to the user-chosen path (Dio `download()` to a temp file → move) instead of buffering. The 30 s `receiveTimeout` may also be tight for 50 MB over a slow LAN. Flagged by the #547 review.
- **§54 client — `LogsApi.tail` silently yields empty on a non-array body.** `_dio.post<List<dynamic>>` returns `data == null` when Dio can't coerce the response to a list (e.g. the daemon returns an error object instead of the expected JSON array), and the `?? const []` fallback surfaces zero entries rather than an error. Low probability against the internal daemon (the §29.9 endpoint always returns an array), but a malformed/proxy-rewritten body would look like "no logs" instead of an error banner. If it ever bites, branch on the response shape and surface a transport error. Flagged by the #547 review.
- **§54 client — bug-report download buffers the whole ZIP in memory.** `BugReportApi.download` reads the bundle into a single `Uint8List` then hands it to `FilePicker.saveFile(bytes:)`, same pattern as the §54 log download. The bundle is small today (logs + profile ≈ hundreds of KB), but if the logs slice grows it'll spike RAM; and if the user cancels the Save picker the bytes are discarded and the whole prepare+download cycle reruns. The stream-to-chosen-path fix (Dio `download()` to a temp file → move) should cover both this and the log-download caller. Flagged by the #548 review.
- **§25.5.5 Dome — `FindHome` / `AbortSlew` / `SetPark` / `SyncToAzimuth` not wired (no daemon endpoint).** The live Dome panel (#566) surfaces the full capability set (`can_set_shutter`/`can_set_azimuth`/`can_sync_azimuth`/`can_park`/`can_find_home`) and wires Open/Close shutter, Slew, and Park (the endpoints that exist). `DomeService` reads `CanFindHome`/`CanSyncAzimuth` but has no `FindHomeAsync`/`AbortSlewAsync`/`SetParkAsync`/`SyncToAzimuthAsync` and no `/findhome`,`/abort`,`/setpark`,`/sync` endpoints, so the client can't offer a Find-Home / Abort / Set-Park / Sync-azimuth control yet. The caps are already in the DTO, so adding the daemon methods + endpoints + the client buttons is purely additive when desired. Flagged by the #566 review.
- **§25.5.5 Camera — daemon DTO gaps for full Alpaca coverage.** The live Camera panel (#~567) shows temp/cooler + sensor/gain/offset/bin caps from the existing `CameraCapabilitiesDto`/`CameraStateDto`, and a new `/camera/cooler` endpoint drives `SetCoolerAsync`. Still not exposed (audit, lower-value): `sensortype` as an explicit field (currently inferred from `bayer_pattern` null-ness), `readoutmode`/`readoutmodes[]` (no caps/runtime/selection), the cooler **set-point read-back** (runtime reports actual `ccd_temperature` but not the target the device is cooling *to*, so the panel can't show "cooling to −10°C"), `exposureresolution`, `hasshutter`, `gains[]`/`offsets[]` value-list mode, `heatsinktemperature`, `pixel_size_y`. Additive when desired. Flagged during the camera conformance slice.
- **DONE (2026-07-02) — §25.5.5 dumb-cooler (on/off, no TEC set-point) support.** Implemented exactly as this entry prescribed: `CameraCapabilitiesDto` gained `HasCooler` (connect-time probe — ASCOM contract: reading `CoolerOn` throws PropertyNotImplementedException when there is no cooler, so a successful read means a cooler exists), the panel gates the on/off Switch on `has_cooler` and the set-point field on `can_set_temperature`, and the client parse falls back to `can_set_temperature` when the field is absent so a pre-slice daemon reproduces the old gating exactly. Original entry: The panel currently gates the *entire* cooler control block (on/off Switch + set-point) on `can_set_temperature`. A camera with a simple on/off cooler but no temperature regulation reports ASCOM `CanSetCCDTemperature=false`, so its cooler Switch is hidden entirely. Such cameras are rare in this hobby (TEC set-point cameras dominate), so v1 ships gated on `can_set_temperature`. Proper fix when wanted: add a separate `has_cooler` capability to `CameraCapabilitiesDto` (daemon probes whether `CoolerOn` is implemented at connect), gate the on/off Switch on `has_cooler` and the set-point field on `can_set_temperature`. Flagged by CR on #567.
- **§64 Live View — reported frame dims vs the encoder's maxDim downscale (from the #652 review, pre-existing).** `LiveViewFrameData.Width/Height` report the PRE-encode dimensions (native for mono, super-pixel-halved for OSC), but `JpegEncoder.EncodeGray/EncodeColor` internally downscale anything over `LiveViewMaxDim` (1024) — so on any real sensor the reported dims exceed the actual JPEG's pixel size. Harmless today: the client renders the bytes directly (`Image.memory`) and never sizes by the DTO. If a consumer ever needs the JPEG's true dims (overlay math, pixel-space cursor readout), either report post-encode dims or have the encoder return them. Noted so the #652 review observation isn't lost. (low)
- **DONE (2026-07-02) — §64 Live View debayered colour for OSC (RGGB) cameras.** The live loop now resolves the session's Bayer pattern at start (from the connect-time capabilities, only at bin 1x1 — binning mixes CFA cells, mirroring the capture path's BAYERPAT gating) and renders debayered COLOUR via the shared mosaic->RGB preview recipe, which moved to `Debayer.SuperPixelStretched` so the §65 capture preview and the §64 live render cannot drift. Super-pixel halves the published frame dims (the client sizes by them; it renders the JPEG bytes directly, so no client change). Mono and binned-OSC sessions render greyscale luminance exactly as before. The render decision is the testable `CameraService.RenderLiveFrame` seam (dims + JPEG assertions; channel-order proven via Manual-stretch determinism). Original entry: LV-1 (#569) renders every live frame as greyscale via `Stretcher.Apply(AutoStf) → JpegEncoder.EncodeGray`. For a mono camera that's true luminance; for an **RGGB OSC** camera it renders the raw CFA mosaic as greyscale (the cross-hatch is visible), since the live path skips the `Debayer.SuperPixel` step the §65 capture-preview path (`SqliteFrameRepository.DebayerAndStretch`) does. Acceptable for framing/focus, but a debayered (or green-channel) live render is the nicer finish — additive in a later LV slice. `SensorType.Color` (already-debayered 3-plane) is refused at `StartLiveViewAsync`. Flagged by CR on #569.
- **OBSOLETE (2026-07-02) — §36.1 GPL v3 §6 Aladin Lite sign-off: no longer applicable.** The premise died with the #611 planetarium pivot: Aladin Lite is no longer bundled (nor fetched) — the `AladinView` widget was deleted, `assets/aladin/` was never in `pubspec.yaml` after the pivot, and the leftover unshipped `assets/aladin/{aladin.js,ALADIN_LICENSE.md}` files were removed from the repo in the Aladin-scrub PR (2026-07-02), which also replaced the stale NOTICE.md Aladin bullet with the Stellarium Web Engine (AGPL-3.0) disclosure. No GPL §6 obligation exists for software we do not convey. The bundled planetarium engine's own source-disclosure duty (AGPL §13) is already satisfied — see `client/.../assets/stellarium/README.md` + the `open-astro/stellarium-web-engine` fork (tag `ara-v1`). Original entry (historical): As of #571 the Aladin Lite v3 engine (GPL v3, minified) ships *inside* the WILMA client binary rather than being fetched from the CDS CDN at runtime, which changes the obligation from "linking" (the GPL FAQ separate-process exception NOTICE.md cites) to "distribution": GPL v3 §6 requires the complete corresponding source accompany or be offered alongside any binary conveyed to a third party. A formal §6 **written offer** + the unambiguous corresponding-source pointer (the upstream `v3.6.1` tag) is now in `client/.../assets/aladin/ALADIN_LICENSE.md`, which satisfies the substance for a network-available upstream. **Before the first public/installer release that conveys the binary**, have whoever signs off the existing LGPL/FreeImage compliance confirm the §6 form is adequate for the shipping channel (e.g. that the installer carries or links the offer, and the corresponding-source location is durable). No obligation crystallises at merge — this PR does not ship an installer to third parties; the duty attaches when the packaged binary is conveyed. Flagged by the #571 review.
- **DONE (2026-07-02) — §25.3 top-bar Switch chip busy (amber) state.** Closed via the entry's own "derive it from an in-flight set-value op" option: `switchActingProvider` mirrors `SwitchListNotifier._act`'s re-entrancy guard (set/cleared in the same try/finally, generation-guarded on server switch), and `switchChipLevel` gained an `acting` input that maps to amber for any in-flight switch action (port write or connect/disconnect) — loading/error still outrank it, matching the other chips' precedence. `SwitchDevice` itself stays unchanged (the daemon reports no per-port actuation state to model). Original entry: The single-instance equipment status chips (camera/focuser/mount/…) turn amber while a move/op is in flight (their `EquipmentDeviceStatus.isBusy`), but `SwitchStatusChip` can't: `SwitchDevice` only models `connectionState`/`isConnected`, not a per-port actuation/busy state, so a switch with a port actuating stays green rather than amber. Intentional gap (the model doesn't carry it), not forgotten — to close it, add a busy/actuating signal to `SwitchDevice` (or derive it from an in-flight set-value op) and map it in `switchChipLevel`. Flagged by the #577 review.
- **DONE (verified 2026-07-02) — §36/§25.5 Planning "Add to sequence" from the Framing panel.** Superseded by the #611 self-driven page: the framing overlay (camera FOV from the optics profile + rotation dial + N×M mosaic) lives *inside* `assets/stellarium/index.html`, which owns the framing geometry and posts an `addToSequence` event (`raDeg`/`decDeg`/`rotationDeg`/`name`) over the `/araevent` loopback; `StellariumView._onPageEvent` builds the Run with the shared `buildSlewTargetBody`. No atlas-centre→Dart bridge is needed. The old Aladin-era Dart framing chain (`framing_state.dart` + `fov_box.dart`) was deleted as superseded in the Aladin-scrub PR. Remaining gap (already tracked in the NINA sequencer-fidelity epic): the framing `rotationDeg` is not carried into the Run (the slew instruction has no PA field — needs CenterAndRotate/rotator). Original entry: The Tonight's-Sky-row "Add to a new sequence" action (this slice) builds a one-slew sequence from the object's `ra_deg`/`dec_deg`, which Dart already holds. The Framing panel's **"Add to Sequence"** button (still `onPressed: null`) wants to capture the *currently-framed* centre + rotation/mosaic, but the atlas centre lives only in the planetarium page (post-#611: the self-driven Stellarium page; the page->Dart channel is the `/araevent` loopback POST, not a JS bridge) and is never reported back to Dart — there's no `{centerRaDeg, centerDecDeg}` provider. To enable it: add a JS→Dart bridge that reports the atlas centre (and ideally the live FOV), store it in a provider, then wire the Framing button to `buildSlewTargetBody` + the framing rotation/mosaic (a richer body than the single slew — likely a container with the FOV-derived panel set). Until then the Framing button stays disabled. Flagged while building the Tonight's-Sky add-to-sequence slice (PR2).
- **§36 Data Manager catalogs — durability + remaining slices.** **Durability RESOLVED (2026-07-01):** `hyg-stars`/`openngc-dso` now download from the org's own snapshot repo — commit-pinned raw URLs at `open-astro/sky-data` @ `712bf93` (byte-identical to the upstream pins, digests unchanged; that repo's README carries the CC BY-SA attribution + provenance, and a `v1` GitHub release mirrors the files for humans). Deliberately raw URLs, NOT release assets: asset downloads 302-redirect and the sky-data HttpClient refuses redirects (the HTTPS-downgrade guard in Program.cs), while raw serves 200 directly — verified live (0 redirects, digests match). Refresh procedure: commit new files to sky-data `main`, cut `v2`, update the `Catalog` URLs (new commit SHA) + `CatalogSha256` in the same ARA PR. Historical context: previously commit-pinned to the upstream repos (HYG archived repo; OpenNGC), which a repo deletion would have broken. `horizon-default` — **REMOVED** (was the dead-`data.openastro.net` entry): a site horizon (flat default or survey) is generated LOCALLY for the §36 Tonight's-Sky overlay (Meeus altitude formula + below-horizon shading per playbook §36), NOT fetched via the Data Manager, so it was a miscategorised broken download. The real horizon feature re-adds horizon data on the Tonight's-Sky side when built. Remaining catalog-overlay slices: (1) a server endpoint to serve an installed catalog as JSON rows {ra,dec,mag,name} (parse `{packageDir}/catalog.csv` — **delimiters + columns differ per package**: HYG `hygdata` is comma-separated with `ra`/`dec`/`mag`/`proper` columns, OpenNGC is **semicolon**-separated with different column names, so the parser must be per-catalog, not one-size-fits-all); (2) client `araAddCatalog` Aladin overlay + fetch, gated on installed catalog packages. Flagged while wiring the real-catalog download (PR4a-2); per-package delimiter raised by the #584 review. **Content-integrity — RESOLVED in #584:** downloads now verify against a known SHA-256 (internal `CatalogSha256` map; `SkyDataInstaller` hashes the stream and checks the digest *before* the atomic swap, so a corrupt/wrong download is rejected without replacing a good install). When a self-hosted snapshot lands, update the digests alongside the URLs.
- **§36 catalog serve endpoint — no caching of parsed catalogs.** `GET /data-manager/{id}/catalog` reads + parses `catalog.csv` (up to the 50k cap) on every request via `Task.Run`. Fine for the single-user local daemon, but N concurrent requests each spin a thread-pool thread re-parsing the same ~13 MB file. If multi-user / CI-load scenarios land, memoize the parsed `IReadOnlyList<CatalogObjectDto>` per package (invalidate on download/delete) so repeat overlay loads don't re-parse. Flagged by the #586 review (low priority).
- **§36 CEF 149 helpers — entitlement split, fork-script defaults + a native macOS CI leg (from the #600 review).** The five macOS helper bundles share one `Runner/Helper.entitlements` carrying `cs.allow-unsigned-executable-memory`, but only the **GPU** helper needs it (SwiftShader's Reactor JIT). Follow-up: split into a GPU-only file (JIT + exec-memory + disable-library-validation) and a narrower file (JIT + disable-library-validation) for the base/Renderer/Plugin/Alerts helpers, to keep the Hardened-Runtime surface minimal on four of five subprocesses (documented/accepted, not a regression). Two related upstream/CI chores surfaced by the same break: (a) the fork's `add_helper_target.rb` defaults helpers to `MACOSX_DEPLOYMENT_TARGET = 10.15` and the plugin `helper.entitlements` — so every client re-run needs the CEF_HELPER.md fix-up block (re-point entitlements + 12.0); a fork PR could bake the client's choices in (or parameterize them) so the follow-up isn't needed. (b) **Client CI only runs `analyze + test` and never compiles the native macOS Runner** — which is why #599 shipped a non-building macOS client (C++20 + CEF-130 wrapper mismatch) through a green gate; add a `flutter build macos` leg (its own CI/infra PR) so native link breaks can't slip through again. **Status (2026-07-01): (b) DONE — the `client-build` CI job compiles all three desktop Runners in release mode (see the §36 follow-ups entry above). The entitlement split + (a) are MOOT: #611 removed CEF (and its five helper bundles) from the client entirely.**
- **§36 Planning horizon — geographic-pole degeneracy (from the #608 review).** `HorizonService.Compute` projects the flat horizon onto RA/Dec; at exactly |lat| = 90° every azimuth hits the `cosLat`-degenerate guard, so all 181 curve points collapse to a single `(RA = LST, dec = ±90°)`. Physically correct (azimuth is undefined at the geographic pole) and finite, but a non-real observing site gets a degenerate overlay with no signal in the DTO. If we ever support polar sites, either compute the true constant-altitude horizon at the pole (the dec = horizonAlt small circle, all RA) or add a `polarDegenerate` flag to `HorizonDto` so the client can warn. Out of scope for the flat-horizon slice — no real observatory sits at ±90.0000°. Guard widened to 1e-6 (≈0.2″) in the same PR keeps the near-polar numerics safe.
- **§36 Planning framing — position angle not carried into the Run (from the #610 review).** The framing overlay's "Create Run" posts `rotationDeg` (the dialed position angle) to `/araevent`, but `_onPageEvent` in `stellarium_view.dart` builds a `SlewScopeToRaDec` sequence target, which has **no PA field** — so the framing PA is currently not applied (the overlay is preview-only for rotation). To carry it, the add-to-sequence body needs a `CenterAndRotate`/rotator instruction (rotate-to-PA after the slew), which lands with the NINA sequencer rotation-fidelity work. Until then `rotationDeg` is received but deliberately unused (documented at the receive site). Flagged by the #610 review.
- **NEXTGEN §4 wizard — capture read noise / QE peak + the planning filter set in the wizard flow.** Slice 2 (feature/exposure-profile-setup) delivers the capture paths that don't need new wizard UI: aperture was already asked on the telescope screen and now persists to `optics.aperture_mm`; the ASCOM-sourced electronics (sensor name, full well, e⁻/ADU, gain) auto-capture when the wizard's camera step connects the camera; and the Settings → Imaging panels (Camera electronics, Filter set — incl. "seed from filter wheel labels") cover manual entry. **Camera-step half DONE (2026-07-02):** the wizard's camera screen asks for read noise + peak QE (optional, spec-sheet values; QE entered as a percent, stored as a fraction with an implausible-value guard), merged into `camera_electronics` GET-then-PUT so the connect-time ASCOM auto-capture survives. **Filter-set half DONE (2026-07-02, same day):** `applyDraftToFilterSet` maps screen 6's named filters into the profile's `filter_set` on Save (kind guessed per name via the same `FilterSetNotifier.guessKind` inference as the Settings seed button; unnamed rows skipped; empty draft preserves the base) — closing a real gap: the wizard's filter entries were previously saved NOWHERE. The whole NEXTGEN §4 wizard deferral is now closed.
- **§36 multi-target plans — duplicate same-name target blocks are removed one at a time, in list order.** `indexOfTargetBlock` matches a target block by container Name (trimmed, case-insensitive), so adding the same object twice produces two identically-named blocks and the Planning bar's "Remove" prunes the FIRST match per click with no user-visible ordering cue. Low severity (a duplicate add is itself unusual, and the editor tree can always delete a specific block precisely); if it ever matters, key blocks by a generated id stamped at build time instead of the display name. Flagged by the #689 review (non-blocking).
- **§38 daemon — DELETE/PATCH of a sequence with an active run should 409, not apply blindly.** `FileSequenceService.DeleteAsync` does an unconditional `File.Delete` and `UpdateAsync` an unconditional rewrite; neither consults `ISequencerService.GetRunStateAsync`. The client guards both (the #689 Stop & Delete re-probe; the append/remove active-run probes), but the invariant "never delete/edit a sequence with a live run" belongs in the daemon — a second client (or bare curl) can still pull the file out from under a live executor, and even the single-client probe→request window stays open. Fix: the endpoints (or the service) check run state and return 409 Conflict when active; the client flows then collapse to act-on-409 instead of probe-first. Flagged by the #689 reviews (client TOCTOU fixed there; the server half is this entry).
- **Profiles — `FileProfileRepository.ReadFile` doesn't normalize null sections.** The #689 fix normalizes every section at `FileProfileStore`'s load + write choke-points, but saved multi-profile snapshots are ALSO read by `FileProfileRepository` (GET `/api/v1/profiles/{id}` serves the raw snapshot; profile-share export carries it) — an older saved profile still returns `"camera_electronics": null` etc. on the wire. Fix: hoist `NormalizeSections` to a shared helper (e.g. on `ProfileStoreSnapshot`) and apply it in `FileProfileRepository.ReadFile` too — normalize where the nulls are born. Flagged by the #689 self-review (altitude).
- **§36 sequencer model — duplicated `$type` constants + FilterInfo wire shape.** `imaging_run_body.dart` declares nine assembly-qualified `$type` consts (and `filterInfoType` + the `{'_name','_position'}` FilterInfo map shape) that duplicate literals already living in the instruction/condition/trigger catalogs, `optimal_sub_advisor.dart` (public `takeExposureType`), and `sequence_field_editor.dart`'s `_FilterEditor`. Consolidate: promote the needed types to named consts in the catalogs (the existing `slewScopeToRaDecType` pattern) + one shared `buildFilterInfo(name, position)` helper, then import everywhere. Also fold `kRunTabIndex` (create_imaging_run.dart + a bare `select(1)` in calibration_screen.dart) next to `kOptionsTabIndex` with the matching app_shell assert. Flagged by the #689 self-review (reuse).
- **§36 Planning — createImagingRun outcome SnackBars triplicated + inconsistent no-server feedback.** The three call sites (target action bar, Tonight's Sky row, framing overlay) each rebuild the same appended/created/error strings and already disagree on the null-result case (framing shows "Connect to a server", the others are silent). Consolidate into a `describe`/feedback helper next to `ImagingRunResult`. Flagged by the #689 self-review (reuse/simplification).
- **§36 multi-target append — NINA "End" containers aren't recognized as session-end steps.** `appendTargetToRunBody` walks back over trailing WarmCamera/ParkScope LEAVES (#689), but an imported NINA sequence that wraps its park/warm steps in an End-area CONTAINER gets the new target appended after it (containers are indistinguishable from target blocks structurally). If imported-sequence appending matters, detect NINA's Start/End area containers by their `Strategy`/name markers. Flagged by the #689 self-review.
