# OpenAstro Ara — Port Progress

Single-page status. Updated on every phase boundary. Per PORT_PLAYBOOK.md §20.1, "Currently working on" must point at a specific file, endpoint, or sub-PR — never "various refactoring."

> **Section-level completion checklist:** see **`PORT_PLAYBOOK.md` → "Port completion status — v0.0.1 section checklist"** (near the top) for a ✅/🟡/⬜/🚫 status on all 77 playbook §sections. This file (PORT_PROGRESS) carries the per-PR narrative; the playbook checklist is the section-level rollup. Keep both current (each feature PR updates its Completed entry here + flips its §checklist marker there).

## Current

- **Phase:** v0.0.1 feature completion. The headless daemon drives real Alpaca equipment end-to-end (discover→connect→capture→§72 FITS→§28 catalog→§65 preview), runs saved sequences through the ported NINA engine, and guides via the external `openastro-guider` daemon (guider a/c/d done). Active threads: **§2105 in-memory render** (un-stubbing the inherited Image render path on SkiaSharp — PR1-3 merged), **guider-e** (e-1 RPC classes + e-2 §63.5 on-connect push & in-app editor merged #352/#371/#372/#373; e-3 §63.4 profile-name mapping — RPC classes #375, connect wiring in flight; e-4 dark-library remains), **§45 polar-align**. Remaining placeholders (polar-align, backup, data-manager, profile-share, Live View) are v0.1.0 per RELEASE_NOTES; the `v0.0.1-ara.1` tag + RPi smoke are the user/Pi-gated terminus.
- **Last merged:** PR **#366** (§58.4 meridian-flip orchestration — `MeridianFlipExecutor`) on `master` @ `0a3d7192f`, **5 review rounds** (r1 caught a real §58.5 pier-side-comparison gap + a double-settle clarification; r2-3 hardened the error-path resume/restore via guidingStopped/trackingDisabled flags; r4 approved + a log-level nit). The real `IMeridianFlipExecutor` (Server) replacing the throwing placeholder + the deleted WPF `MeridianFlipVM`: faithful headless port of `DoMeridianFlip` (stop guiding → pass meridian → flip slew + settle + dome sync → recenter via `ICenteringService` → resume guiding → settle → §58.5 pier-side verify); also wires the previously-unregistered `MeridianFlipTrigger` prototype into `HeadlessSequencerFactory`. +12 tests. **§58 meridian flip is now functionally complete (trigger #362 + executor #366); §58.9 unattended-safety layers are the deferred follow-up.** Prior: **#365** (§28 `CenteringService`) + **#364/#363** (§18.I plate-solve chain → ASTAP). **ASTAP `astap_cli` BUILT** + fork build-fix pushed (`open-astro/ASTAP` `fix/fpc-3.2.2-build`). Earlier this arc: §59 curve-fits (#359-361), §58 trigger (#362).
- **Last merged (earlier this arc):** PR **#346** (§63 guider-c — `IGuiderMediator` unification) on `master` @ `d7d04a942`. `GuiderService.Mediator.cs` makes the service also serve the Sequencer's `IGuiderMediator` (one singleton for both interfaces per §8.1, replacing `HeadlessGuiderMediator`), so sequence `StartGuiding`/`StopGuiding`/`Dither` instructions drive the **live** guider over TCP :4400. Null-safe delegation (`MediatorGuider()?.X ?? false`); GetInfo from live state; mediator events no-op + RMS-recording/GetLockPosition inert sentinels (no registered consumer). Merged on the **resolved TCP :4400 transport** — the daemon's HTTP :8080 bridge is its unbuilt Phase 5, so :4400 is the only working path; the mediator is transport-independent so a future HTTP migration only swaps the client wire layer. §3a self-review (reviewer silent), +10 tests, merged via REST API. **The guider CLIENT is now functionally complete (a connect/status/guide/RMS + c sequencer-drive).**
- **Last merged (prior):** PR **#345** (§63 guider-a — re-port `PHD2Guider` + real `GuiderService`) on `master` @ `b0dc8f13d`. The inherited 1261-line PHD2 JSON-RPC client (deleted in #242) was recovered from `840893eb8^` and re-ported headless (WPF strip; `Notification`→`Notifier`; settings `PHD2ServerUrl`→`PHD2ServerHost`; nullable + full CA analyzer-gate compliance incl. a justified file-local `#nullable disable warnings` + `#pragma CA1031` for the recovered protocol path; `IDisposable`/sealed; a CA2022 fix that also corrected a latent full-buffer garbage-decode). `GuiderService` (real `IGuiderService`, replaces `PlaceholderGuiderService`) drives it PHD2-backed (host/port from the profile per §63.5; no Alpaca discovery): §60.5 202-Accepted connect with generation-supersede, live state from the client's listener, connect/disconnect/status + start/stop/dither, **observes `PHD2ConnectionLost`→Error**, and reports **guiding RMS** (total/RA/Dec, bounded 200-step window from `GuideEvent` via the unit-tested `ComputeRms` — folds the guider-b RMS work in). Merged after a §3a `/code-review` self-review (the claude[bot] reviewer didn't post; the self-review caught the connection-loss HIGH bug). +8 sim-free tests. CodeQL failed once on a transient GitHub-API auth blip (re-run cleared it); merged via REST API (GraphQL was intermittently 401-ing). Deferred to PORT_TODO: terminal-status WS surface for fire-and-forget guide ops, shared-profile arg-passing race (§27-mitigated), `IGuiderMediator` unification (guider-c).
- **Last merged (prior):** PR **#344** (§14e capture-path PRb — `TakeExposure` re-port + camera/imaging mediator unification) on `master` @ `5ace6c5b4` — **15 review rounds.** `TakeExposure` re-ported with the NINA-verbatim JSON surface (resolves via §38k-6 remap); `CameraService` now also serves `ICameraMediator` + `IImagingMediator`, routing sequencer captures through the SAME `CaptureCoreAsync` pipeline as REST (in-flight gate: sequencer waits / REST rejects; cancellation checkpoints at entry/pre-exposure/pre-download + `TryAbortQuietly`; client re-snapshot under gate after a long queue wait; `ExposureCount` Interlocked+Volatile; `Validate` rejects non-positive exposure + unrecognized `ImageType`; `IMAGETYP` uppercased; `CameraOffset` cap-validated via new `MinOffset/MaxOffset` caps). **A saved sequence with capture nodes now produces real FITS frames in the catalog — the camera capture path (PRa #343 + PRb #344) is complete.** Deferred to PORT_TODO: shared "manual capture" session for sequencer frames, REST LIGHT-only, FITS keyword-convention audit, `ExposureSeconds` int / `Gain` sentinel widening. Follows #321–#343.
- **guider-d (§63.3 crash-detection + auto-restart) — ✅ MERGED PR #351 @ `ece8822e1`.** Active crash-recovery for the sibling `openastro-phd2` systemd unit: `IGuiderProcessSupervisor` (systemctl `is-active`/`restart` seam, no-op off-systemd) + `GuiderRecoveryCoordinator` (backoff 1→5→15→30→60→120 s, per-poll 5 s timeout, single-nudge) fired from `GuiderService.OnConnectionLost`; Critical notification + Red diagnostic (AutoActionTaken honest) on non-recovery; per-pass CTS cancels stale recovery on reconnect/disconnect/dispose. +16 tests. 5 review rounds (first found zero correctness bugs).
- **Currently working on:** **NOTHING IN FLIGHT — §68 AlpacaBridge version-gate complete; the cleanly-buildable autonomous queue is drained again.** §68 (a "verify"-audit-surfaced §68.1 gap) shipped end-to-end across **#410** (classifier + `/version` probe), **#411** (30s cached handshake), **#412** (the `ConnectGatedAsync` REST gate — all 10 Alpaca connects refuse **503 `alpaca_bridge_outdated`** below 1.2.0), **#413** (the `equipment.alpaca_bridge_outdated_warn` event for the 1.2–1.5 warn band), and **#414** (the client dismissible warn banner). A 2026-06-13 investigation **confirmed the gate is correctly placed at the REST endpoint** — there's no daemon auto-connect-on-boot and no client manual Alpaca-connect, so the REST `POST /equipment/{type}/connect` is the only connect path today. **Remaining §68 is blocked or needs a user/design decision** (tracked in PORT_TODO §68): the 503 connect-modal needs a client equipment-connect UI that doesn't exist (scope call — manual-connect vs §52.1 auto-connect-on-boot); §68.4 search entries need a registry-design call (the `Setting` registry is settable-fields-only); the wizard missing-bridge UX is bigger client work. **Next autonomous candidates all need a scope decision:** the client equipment-connect UI (substantial), §31 time-sync (NTP-vs-GPS + privileged clock-set ambiguity), or call v0.0.1 done and tag. **Phase 15 close-out is DONE:** the `3rd-party-licenses.txt` client-dep inventory is complete + uniform (**#400** @ `e4b00d314`, 3 review rounds, ✅ approved — all 12 direct Flutter deps now carry resolved versions + LICENSE-verified copyright holders; dropped the non-dep `riverpod_annotation`). The full guider control + calibration UI chain shipped earlier this session: **#396** (status model/API/provider), **#397** (live GUIDE chip + connect/disconnect dialog), **#398** (calibration API/status/provider), **#399** (dark-library + defect-map build/enable UI — **this delivers the originally-blocked guider-e-4b-3**). **v0.0.1 is feature-complete.** The two remaining items are NOT autonomously completable: **(1)** the `v0.0.1-ara.1` tag on master (user-driven), **(2)** the §13/§34 RPi smoke test (physical Pi hardware). Deferred to v0.1.0 per RELEASE_NOTES: **§45 polar-align** (guider daemon's polar-align RPCs absent from its `jsonrpc_api.md` — externally blocked), **§42.2 per-equipment fault recovery** (hardware), **§71 Native AOT** (scoped), the guider live-progress WS surface, and the placeholder screens (backup / data-manager / profile-share / Live View).
- **(prior context preserved below)** §58 meridian flip — `MeridianFlipTrigger` (decision logic) MERGED #362. The trigger was deleted in the §0.5 WPF demolition; re-ported headless from `840893eb8^` — `ShouldTrigger` (connected/parked/home/tracking guards, side-of-pier dedup, the §58.2 timing-window decision off `TelescopeInfo.TimeToMeridianFlip`) is intact and depends only on `ITelescopeMediator` + the profile. The WPF `MeridianFlipVM` orchestration it called is replaced by the headless **`IMeridianFlipExecutor`** seam (a throwing placeholder export keeps MEF composition valid + fails loudly rather than silently skipping a flip). +16 tests (timing matrix + early-return guards + Validate). **Sub-PR 2 (the §58.4 flip orchestration — pause → flip slew → plate-solve recenter → refocus → restart guiding) is the follow-up**, plus the side-of-pier projection test matrix. **Remaining §59:** the focuser-gated live V-curve sweep + Smart Focus (v0.1.0). Other open threads: **§45 polar-align** (guider-daemon RPCs not yet in its `jsonrpc_api.md` — externally blocked), **guider-e-2+** (blocked on the profile-model+wizard extension).
- **guider-e (§63.4/.5 profile + dark-library push) — guider-e-1 + e-2 MERGED; e-3 (profile-name mapping) + e-4 (dark-library) remain:**
  - **guider-e-1** ✅ #352: typed **named-object** RPC request classes for the setter set — `set_connected {"connected":bool}`, `set_profile_setup {subset}`, `set_selected_camera {"camera"}` / `set_selected_camera_id`, `set_alpaca_server {host,port,*_device subset}`, `set_algo_param {axis,name,value}`, `set_dec_guide_mode {"mode"}` — with Newtonsoft serialization unit tests. **WIRE FORMAT RESOLVED (2026-06-11):** `openastro-guider/design/API_REFERENCE.md:24` — *"Params may be an object (named) or array (positional)."* The daemon **dual-supports** both, which is why the existing positional `Phd2SetConnected` works *and* the documented named-object form will. **Decision: build the new setters to the named-object form** (clearer + matches `doc/jsonrpc_api.md`). HTTP transport is `POST :8080/api/rpc` (port = `8080 + instance−1`); existing client uses TCP `:4400`. Contract: `~/Documents/GitHub/openastro-guider/doc/jsonrpc_api.md` + `design/API_REFERENCE.md`.
  - **guider-e-2** ✅ #371/#372/#373: §63.5 profile-param push on connect — **fully landed** (the earlier "blocked on a profile-model+wizard extension" was the work this chain did). **#371** extended ARA's profile model with the missing §63.5 source data (`IGuiderSettings`: `GuideFocalLength`/`GuidePixelSize`/`RAAggressiveness`/`DecAggressiveness`/`MinimumMove`/`DecGuideMode`, + `Phd2SettingsDto`/`StoreBackedProfileService.ApplyPhd2` normalization/clamping, + openapi); **#372** added the on-connect push (`PHD2Guider.GuiderEngineConfig.cs` — disconnect (only when `set_profile_setup` is in play) → push `set_profile_setup`/`set_algo_param`/`set_dec_guide_mode` → reconnect; 0/"auto" treated as unset so a fresh ARA profile never clobbers PHD2's own North/South or disables corrections); **#373** added the in-app editor (Flutter "Guider engine" settings section + help + search-registry entries). **This PR (guider-push-observability)** folds in the #372-review-suggested push-summary/skip logging + the deferred-follow-up notes in PORT_TODO.
  - **guider-e-3:** §63.4 ARA-profile ↔ PHD2-profile name mapping (`create_profile`/`set_profile_by_name`, `ara-<slug>`).
  - **guider-e-4:** §63.5 dark-library push (`build_dark_library`) — likely the v0.0.1/v0.1.0 boundary.
  - **Next after guider-e:** §45 polar-align (drives the daemon's `POLAR_ALIGNMENT_DESIGN.md`) → Live View / §2105 image render (SkiaSharp per the §26 reconsideration) → Phase 15 release (user/Pi-gated terminus).
- **⚠️ ARCHITECTURE CLARIFICATION (2026-06-10, from the user): the guider engine is NOT built in ARA.** The guiding engine is the separate **`openastro-guider`** daemon (`github.com/open-astro/openastro-guider`, local at `~/Documents/GitHub/openastro-guider`) — a **headless, Alpaca-only, Linux/Pi C++ fork of PHD2** (lineage: PHD2 → `openastro-phd2` (GUI/INDI) → `openastro-guider` (headless)). It keeps PHD2's JSON-RPC method table intact and exposes it over **two transports sharing one dispatch**: classic TCP `:4400` (the NINA event-server path) **and** a newer HTTP `/api/rpc` on `:8080` that its docs earmark "primarily for ARA". ARA builds only the **thin client** that drives it (per §63 "ARA does not modify PHD2"). guider-a (#345) is that client over **TCP :4400** and works against the daemon today. **OPEN DECISION (user):** keep ARA's client on TCP `:4400` (merged, works) or migrate to the HTTP `:8080 /api/rpc` bridge (ARA-intended, cleaner for a .NET HTTP daemon; rewrites only the wire layer — JSON-RPC method semantics identical). The guider's authoritative contract lives in `openastro-guider/design/{API_CONTRACT,API_REFERENCE,API_GAP_AUDIT}.md` + `doc/jsonrpc_api.md` — ARA's client should align to it. **Consequence for §45 polar-align + §63.5 dark-library:** the guider OWNS these now (`openastro-guider/design/POLAR_ALIGNMENT_DESIGN.md`; `build_dark_library`/`build_defect_map_darks` RPCs) — ARA should **drive the guider's** polar-align/dark-library over the API, NOT reimplement them.
- **Next substantive work (v0.0.1 queue, non-user-blocked):** **guider** (a: PHD2Guider re-port + `GuiderService` connect/status; b: StartGuiding/StopGuiding/Dither; c: `IGuiderMediator` unification replacing `HeadlessGuiderMediator`; d: §63.3 crash-detect + systemd-restart; e: §63.4/.5 profile + dark-library push — possibly the v0.0.1/v0.1.0 boundary) → **polar-align** (§45, gates on camera+plate-solve) → **Live View / §2105 image render** (OpenCvSharp4 + libraw un-stub in `Image/ImageData/` — Live-View-gated, NOT a capture-path blocker; previews already come from the §65 SkiaSharp stretch pipeline) → ~~`IXxxMediator → IXxxService` rename (§8.1)~~ **— SUBSTANTIVELY DONE / obsolete (2026-06-10):** §8.1's actual goal was "equipment mediators → thread-safe ASP.NET service singletons, UI-thread-affinity removed." The mediator-unification work (each `XxxService` serves both `IXxxService` REST + the inherited `IXxxMediator` sequencer interface as one singleton — camera #344, guider #346, telescope/switch/filterwheel/focuser/rotator/dome/safetymonitor earlier) already achieved that. The *literal* rename of the inherited `IXxxMediator` interfaces is unnecessary cosmetic churn on working, merged code (and would collide with the existing `IXxxService` REST names) — not done, not needed. → **Phase 15** (TODO sweep, `3rd-party-licenses.txt`). **User-blocked terminus:** the `v0.0.1-ara.1` tag on master is user-driven, and the RPi smoke test needs physical Pi hardware — so the final release step is NOT autonomously completable. **Phase 14e Alpaca simulator pinning** (§14.5.1 v0.4.0) already landed (#321).

## Completed

### Session 2026-06-13 — §42.2 virtual-observatory bench (bench-1→6, #401–#408)
Hardware-free §42.2 test bench in `OpenAstroAra.TestHarness` + `bench/`, driving the **real** daemon services against simulated gear — no cameras, mount, or PHD2 daemon. The arc also surfaced + fixed three real guider-path production bugs.
- ✅ **bench-1 (#401)** — `AlpacaFaultProxy`: loopback reverse proxy injecting transport faults (Alpaca error / HTTP failure / dropped connection / hang / response-value rewrite), per device/method, one-shot or sticky.
- ✅ **bench-2 (#402)** — `FakeGuider` + `PhdEvents`: scriptable TCP fake of the PHD2 event server (greeting, canned/overridable RPC results, event broadcast).
- ✅ **bench guider-path fixes (#403/#404/#405)** — surfaced by driving the real `GuiderService`/`PHD2Guider` against the fake: **#403** connect-as-service (retired the inherited NINA-desktop `StartPHD2Process`/`WaitForInputIdle`, which couldn't work headless and blocked connecting to a localhost guider; ARA now asks the supervisor to `systemctl start` then connects); **#404** read-driven `RunListener` (replaced a macOS-fragile OS-TCP-table busy-poll with `ReadLineAsync` + keep-alive); **#405** `SendMessage` async read bounded by `receiveTimeout` (a silent/wrong-version guider could otherwise hang the connect forever).
- ✅ **bench-3 (#406)** — `GuiderFakeIntegrationTest`: the real client through the full connect→Connected→AppState(guiding)→GuideStep-RMS→disconnect lifecycle in ~0.5s.
- ✅ **bench-4 (#407)** — two §42.2 device-fault scenarios: lost guide star (`StarLost`→`star_lost`, link stays Connected) and dropped guider link (new `FakeGuider.DropConnections` → `PHD2ConnectionLost` → `Error` + §63.3 recovery). Fault-detection asserted; recovery outcome stays in `GuiderRecoveryCoordinatorTest`.
- ✅ **bench-5 (#408)** — `bench/`: a hermetic `docker compose` lane that builds + runs the 29-test bench suite on `linux/arm64`, keeping a standing Linux check on the kernel-sensitive Drop-fault mechanic. Copy-in (not bind-mount) + a **root `.dockerignore`** so the host's osx-arm64 `bin/obj` never contaminate the linux image on either the BuildKit or classic-builder path (the review caught the per-Dockerfile-ignore-is-BuildKit-only gap).
- ✅ **bench-6** — retired the bench's substring test filter for a `[Category("bench")]` tag (`TestCategory=bench`, one source of truth) + a fixed compose `image:` tag. Off the #408 review notes.

### Session 2026-06-11 (cont.) — §59 AF curve fits, §58 flip, §63.5 guider profile push (#359–#373)
- ✅ **#359–#361 (§59 Classic AF curve fits)** — parabolic (#359), hyperbolic + `FitBest` §59.8 selection (#360), trendline two-arm regression + intersection (#361); `FocusCurveFit` weighted LS on #358's HFR. Remaining §59: the focuser-gated live V-curve sweep + Smart Focus (v0.1.0).
- ✅ **#362 (§58.2 `MeridianFlipTrigger`)** — re-ported the flip decision logic headless behind the `IMeridianFlipExecutor` seam (throwing placeholder keeps MEF valid).
- ✅ **#363/#364 (§18.I plate-solve chain → ASTAP)** + **#365 (§28 `CenteringService`)** — solve a captured frame; slew→solve→sync→re-slew centering.
- ✅ **#366 (§58.4 meridian-flip orchestration executor)** `0a3d7192f` — the real `IMeridianFlipExecutor` (stop guiding → pass meridian → flip slew + settle + dome sync → recenter → resume guiding → settle → §58.5 pier-side verify), wiring `MeridianFlipTrigger` into `HeadlessSequencerFactory`. 5 review rounds. **§58 functionally complete for the attended/auto flip.**
- ✅ **#367 (§58 side-of-pier projection test matrix)** — +20 deterministic JNOW tests locking down `ExpectedPierSide`/`TimeToMeridian`/`TimeToMeridianFlip` (previously zero coverage).
- ✅ **#371/#372/#373 (§63.5 guider-e-2 — profile push chain)** — **#371** extended ARA's profile with the §63.5 source data (`IGuiderSettings` focal/pixel/RA+Dec aggressiveness/min-move/dec-mode + `Phd2SettingsDto` w/ optional ctor defaults for old-profile back-compat + `ApplyPhd2` clamping + openapi); **#372** added the on-connect push (`PHD2Guider.GuiderEngineConfig.cs`: disconnect only when `set_profile_setup` is in play → `set_profile_setup`/`set_algo_param`/`set_dec_guide_mode` → reconnect; 0/"auto" treated as unset so a fresh profile never disables corrections or clobbers a user's North/South; 5 review rounds); **#373** added the in-app "Guider engine" settings editor (Flutter panel + help + search registry). **guider-e-2 done; e-3 profile-name mapping + e-4 dark-library remain.**

### Session 2026-06-11 — OSC colour, manual capture, guider-d/e-1, §2105 render (#349–#356)
- ✅ **#349 (§65 OSC debayered colour previews)** `5f3913878` — super-pixel debayer in the preview/thumbnail path so OSC frames render in colour; capture detects `SensorType.RGGB` + `BayerOffsetX/Y` and stamps the resolved `BAYERPAT` header; **stored FITS stays the raw, undebayered mosaic**. New `OpenAstroAra.Stretch/Debayer.cs` (4 CFA patterns, super-pixel) + `JpegEncoder.EncodeColor`/`EncodeColorThumbnail`. +tests. Review fixed a real binning bug (BAYERPAT only stamped at 1×1).
- ✅ **#350 (§25.5 manual capture from the Imaging tab)** `32b85b449` — the Flutter "Take One" button + `FrameViewer` were stubs; wired to the real exposure→catalog→§65 preview path with zoom/pan. New client `CameraExposureApi`/`FramesApi` + `lastCapturedFrameId`/`captureInProgress` providers; macOS dev-run guide (playbook **§23.1**) + a build-time `libcfitsio` copy (macOS SIP strips DYLD_*). 6 review rounds.
- ✅ **#351 (§63.3 guider-d crash detection + auto-restart)** `ece8822e1` — `IGuiderProcessSupervisor` (systemctl `is-active`/`restart` seam over the `openastro-phd2` unit, no-op off-systemd) + `GuiderRecoveryCoordinator` (backoff 1→5→15→30→60→120 s, per-poll 5 s timeout, single-nudge) fired from `GuiderService.OnConnectionLost`; Critical notification + Red diagnostic (honest `AutoActionTaken`) on non-recovery; per-pass CTS cancels stale recovery on reconnect/disconnect/dispose. +16 tests.
- ✅ **#352 (§63.5 guider-e-1)** `e435c8f71` — named-object RPC request classes for the profile-push setters (`set_profile_setup`/`set_selected_camera`/`set_selected_camera_id`/`set_alpaca_server`/`set_algo_param`/`set_dec_guide_mode`); wire shape locked by serialization tests. **Wire format resolved:** the guider dual-supports named + positional params (`API_REFERENCE.md`), so the documented named-object form was chosen. Also restored the inherited file to ISO-8859-1 (my edit had re-encoded it, mangling ©).
- ✅ **#353 (§65 encoder hardening)** `03ad8d13e` — `GetPixels()` zero-pointer guards + grayscale `Resize` null-guard across the SkiaSharp encode paths (deferred from #349 review).
- ✅ **#354 (§2105 render PR1)** `85d06655e` — un-stub `BaseImageData.RenderBitmapSource`/`RenderImage` on the §65 `Stretcher` (SkiaSharp; **§26 OpenCvSharp4→SkiaSharp decision made** — its native runtimes don't align across linux-arm64/x64/osx-arm64). Image.csproj now refs `OpenAstroAra.Stretch`. **Also fixed a real `Stretcher.AutoStf` over-brightness bug** (median landed ~0.65 not the PixInsight-STF 0.25 → geometric-mean midpoint) that affected **every preview incl. the catalog**.
- ✅ **#355 (§2105 render PR2)** `291037f76` — `RenderedImage.GetThumbnail` (offloaded `JpegEncoder.EncodeThumbnail`) + `ReRender` (re-render from raw).
- ✅ **#356 (§2105 render PR3)** `f1158bd22` — `RenderedImage.Stretch(factor, blackClipping, unlinked)` via a new public parameterized `Stretcher.Stf(targetBackground, shadowSigma)`; `AutoStf` = `Stf(0.25, 2.8)`. Review caught + fixed an unguarded `shadowSigma` (negative → NaN/all-black) on the public API.
- ✅ **#357 (§2105 render PR4 — Debayer, full-resolution)** `861db061c` — `Stretcher.Debayer.Bilinear(mosaic, w, h, BayerPattern)` (pattern-aware bilinear, edge-clamped, full-res R/G/B planes — distinct from the §65 half-res `SuperPixel`) + new `DebayeredImage : RenderedImage, IDebayeredImage` (LRGB planes, Rec.601 luma) wiring `RenderedImage.Debayer` (SensorType→BayerPattern; exotic CFAs throw). +tests. `DetectStars`/`UpdateAnalysis` remain (need a star-detection algorithm).
- ✅ **#358 (§2105 render PR5 — DetectStars + HFR, final stub)** — new dependency-free `StarDetector` (background median+MAD → median+k·σ threshold → optional 3×3 median pre-filter → 8-connected flood-fill blobs → flux-weighted centroid + Half-Flux-Radius; rejects noise specks, edge-truncated, saturated, and frame-spanning blobs; honours a `MaxNumberOfStars` cap brightest-first). Wires `RenderedImage.DetectStars` (StarSensitivity→k: Normal 8 / High 5 / Highest 3; NoiseReduction→3×3 median; offloaded; on-image annotation a documented no-op pending the §2105 annotator) + `UpdateAnalysis` (publishes HFR/HFRStDev/StarCount/StarList onto `RawImageData.StarDetectionAnalysis` → flows into the FITS HFR/StarCount pattern keys). Honours the §26 decision (no OpenCvSharp4) — pure managed code. +9 tests (synthetic Gaussian fields: known count, reasonable HFR, centroid accuracy, edge/saturation rejection, cap, dimension-mismatch, DetectStars→analysis publish). **Closes the §2105 in-memory render thread — all inherited render stubs un-stubbed.**

**Remaining after this session** (tracked in PORT_TODO): ~~§2105 render stubs~~ **DONE (#354–#358)**; ~~guider-e-2 (§63.5 profile push)~~ **DONE (#371/#372/#373)**; guider-e-3 (profile-name mapping) + e-4 (dark-library); §45 polar-align; the user/Pi-gated `v0.0.1-ara.1` release.

### Pre-Phase-0.5 prep
- `prep-ci` (PR #2) — CI baseline + branch naming + merge authority
- `rules-tighten` (PR #10) — tightened §19.1 (no merge-on-rate-limit) + added §22 periodic master promotion
- `tracking-files` (PR #11) — added the four §1 tracking files (retroactive)

### Phase 0.5 — Fork hygiene + project demolition (tag `phase-0.5p-complete` on master)
- ✅ **0.5a** (tag `phase-0.5a-complete`) — Fork hygiene / WPF demolition, 6 sub-PRs (#4-#9)
- ✅ **0.5b** (tag `phase-0.5b-complete`) — Delete MGEN + nikoncswrapper + WiX + Plugin (#13)
- ✅ **0.5c** (tag `phase-0.5c-complete`) — Delete vendor SDKs + vendor concrete impls
- ✅ **0.5d** (tag `phase-0.5d-complete`) — Delete ASCOM COM glue
- ✅ **0.5e + 0.5f** (tag `phase-0.5f-complete`) — Strip Stefan branding + non-English locales
- ✅ **0.5g** — `NINA.Core` → `OpenAstroAra.Core` (tag `phase-0.5g-complete`)
- ✅ **0.5h** — `NINA.Astrometry` → `OpenAstroAra.Astrometry`
- ✅ **0.5i** — `NINA.Profile` → `OpenAstroAra.Profile`
- ✅ **0.5j** — `NINA.Image` → `OpenAstroAra.Image`
- ✅ **0.5k** — `NINA.Equipment` → `OpenAstroAra.Equipment` (rename + cascade scrub)
- ✅ **0.5l** — `NINA.Sequencer` → `OpenAstroAra.Sequencer`
- ✅ **0.5m** — `NINA.Platesolving` → `OpenAstroAra.PlateSolving`
- ✅ **0.5n** — `NINA.Test` → `OpenAstroAra.Test` (tag `phase-0.5n-complete`)
- ✅ **0.5o** — `NINA.sln` → `OpenAstroAra.sln` + `.gitignore` rewrite
- ✅ **0.5p** — .NET 10 bump + first build cleanup pass (tag `phase-0.5p-complete`)

### Phase 1 — .NET 10 SDK pin
Folded into Phase 0.5p (global.json + csproj target framework bumps).

### Phase 2 — Equipment layer to Alpaca-only
- ✅ Commit `013da7697` — collapsed `MyCamera/*` (vendor concrete impls) into Alpaca-only providers per §52. Added `IEquipmentProvider` per §6.2 with `DiscoverAsync` + `ConnectAsync<T>`.

### Phase 3 — Repoint PHD2 client at openastro-phd2
- ✅ Commit `82481559e` — `PHD2Guider.cs` now logs `openastro-phd2` vs upstream PHD2 detection via PHDSubver/PHDVersion substring match. Mojibake fixed (`(c)` for the copyright glyph).

### Phase 4 — Server scaffold (tag `phase-4-complete`)
- ✅ Commit `8c103c324` — `OpenAstroAra.Server` project (Microsoft.NET.Sdk.Web, .NET 10, AOT in Release). Kestrel port resolution env → appsettings → 5555. `/healthz` + `/api/v1/server/info` operational. Scalar UI mounted at `/scalar/v1`.

### Phase 5 — Define API contract + OpenAPI spec (PR #37)
- ✅ `OpenAstroAra.Server/openapi.yaml` — full v0.0.1 contract:
  - 6 endpoint groups (Server, Equipment, Sequence, Image, Log, Stream)
  - Cursor-based pagination per §60.2 (`limit`/`cursor` + `items`/`next_cursor`/`has_more`)
  - `Idempotency-Key` HTTP header per §60.5
  - RFC 7807 `Problem` schema with JSON-pointer keyed `errors` map
  - `ServerStateSnapshot` includes `ws_resume_token` for §60.9 resume
  - WS protocol fully documented (`/api/v1/ws`, X-Ara-WS-Version: 1, 30s ping/60s pong heartbeat, resume, close codes 1000/1001/1009/1011/1012/4001-4004)
  - OpenAPI 3.1 null unions throughout (no `nullable: true`)

### Phase 6 — Equipment endpoints + Alpaca discovery (PR #38)
- ✅ `Contracts/EquipmentDtos.cs` — 12 DTO records covering all device types + `DeviceType` enum (now includes `FlatDevice` per the §10.6 row, mapped to Alpaca `CoverCalibrator` under the hood)
- ✅ `Services/IEquipmentServices.cs` — 12 service interfaces (discovery + per-device)
- ✅ `Services/EquipmentDiscoveryService.cs` — **functional** Alpaca UDP discovery on port 32227 via `ASCOM.Alpaca.Discovery.GetAscomDevicesAsync`
- ✅ `Endpoints/EquipmentEndpoints.cs` — `GET /api/v1/equipment/discover/{type}` (functional) + per-device 501 stubs
- ✅ Route collision fix: discovery moved to `/discover/{type}` so it doesn't shadow per-device literal routes (`/camera`, `/telescope`, …)
- ✅ `DeviceType` token alignment: all-lowercase concatenated form (`filterwheel`, `safetymonitor`) matches both URL path segments and the C# enum (`Enum.TryParse` with `ignoreCase: true`)
- ✅ Global `JsonStringEnumConverter` with custom `LowerCaseNamingPolicy` for consistent enum-to-string serialization

### Phase 7 — Sequence + Calibration + Mosaic endpoints (PR #39)
- ✅ `Contracts/SequenceDtos.cs` (~16 records), `Contracts/CalibrationDtos.cs`, `Contracts/MosaicDtos.cs`, `Contracts/SharedDtos.cs` (cursor page + operation accepted)
- ✅ `Services/ISequenceServices.cs` — 8 service interfaces
- ✅ `Endpoints/SequenceEndpoints.cs` — 16 endpoints incl. `PATCH /api/v1/sequences/{id}` (was PUT; switched to PATCH because partial-update semantics)
- ✅ `Endpoints/CalibrationEndpoints.cs` — 6 endpoints (sessions + matching-flats + dark-library build/status/list)
- ✅ `Endpoints/MosaicEndpoints.cs` — 6 endpoints (CRUD + panels + progress; panel DTO includes §47.3 `crosses_ra_wrap` flag)
- ✅ Full OpenAPI metadata on every route: `.Accepts<TReq>() / .Produces<TResp>() / .ProducesProblem() / .WithName()` so generated OpenAPI surface has typed schemas for WILMA codegen

### Phase 8 — Image + Session + Backup stream + Diagnostics (PR #40)
- ✅ `Contracts/ImageDtos.cs` — ~15 DTOs incl. `FrameType` enum mirroring FITS IMAGETYP, `QualityScoreBreakdownDto` (composite score per §50.10), HFR analysis time series, `BackupClaimRequestDto`
- ✅ `Contracts/DiagnosticsDtos.cs` — health state + issue + event DTOs + 2 enums (`DiagnosticHealth`, `DiagnosticsMode`)
- ✅ `Services/IImageServices.cs` — 4 service interfaces (`IFrameRepository`, `ISessionService`, `IBackupStreamService`, `IDiagnosticsService`)
- ✅ `Endpoints/ImageEndpoints.cs` — 16 endpoints (`/api/v1/{frames,sessions,backup/stream}`) with full pagination params on list routes
- ✅ `Endpoints/DiagnosticsEndpoints.cs` — 3 endpoints (`/state`, `/mode`, `/history` with cursor pagination)

### Phase 9 — Log/state + WS + notifications + Stats + System (PR #41)
- ✅ `Contracts/SharedDtos.cs` (re-confirmed), `Contracts/ServerStateDtos.cs`, `Contracts/NotificationDtos.cs`, `Contracts/StatsDtos.cs`, `Contracts/DataManagerDtos.cs`
- ✅ `Contracts/WsEvents/WsEventCatalog.cs` — 75+ event tokens across Phases 6-9, `WsEventEnvelopeDto` matching §60.9.3 wire shape
- ✅ `Services/IServerStateServices.cs` — 9 service interfaces (state, logs, notifications, stats, bug-report, data-manager, backup, profile-share, `IWsBroadcaster` + `IWsEventChannel`)
- ✅ `Endpoints/ServerStateEndpoints.cs` (9 endpoints + `/readyz`)
- ✅ `Endpoints/NotificationEndpoints.cs` (5 endpoints)
- ✅ `Endpoints/StatsEndpoints.cs` (8 endpoints with typed query params)
- ✅ `Endpoints/SystemEndpoints.cs` (15 endpoints across bug-report + data-manager + backup + profile-share)
- ✅ `Endpoints/WebSocketEndpoints.cs` — `GET /api/v1/ws/catalog` is **functional** (dumps WsEventCatalog.All); WS upgrade returns 426 with proper `Upgrade: websocket` + `Connection: Upgrade` headers per RFC 7231 §6.5.15

### Phase 10 — Server smoke test (tag `phase-10-complete`)
- ✅ Server build + publish ARM64/x64 CI. `/healthz` + `/api/v1/server/info` verified.

### Phase 11 — Flutter client scaffold + first-run (tag `phase-11-complete`)
- ✅ Handshake flow + server discovery (mDNS). `client/openastroara_client` project structure + runtime deps.

### Phase 12 — Flutter views (App shell, tabs, settings)
- ✅ **12a** (tag `phase-12a-complete`) — App shell + navigation + top equipment bar + §25.3 Chips.
- ✅ **12b** — Wizard (§37) 18-screen scaffold + `ProfileDraft` model.
- ✅ **12c** — Imaging + Framing tab cores. `ExposureControlsPanel` + `FramingParamsPanel`.
- ✅ **12h.2-refactor** (tag `phase-12h2-complete`) — Settings polish:
  - 12h.2-safety: Editable Safety Policies (PR #94)
  - 12h.2-site: Editable Site preferences (PR #97)
  - 12h.2-filenames: Editable File Naming (PR #98)
  - 12h.2-autofocus: Editable Autofocus (PR #99)
  - 12h.2-platesolve: Editable Plate Solve (PR #100)
  - 12h.2-diagnostics: Editable Diagnostics Mode (PR #101)
  - 12h.2-trim: Whitespace-tolerant string setters (PR #103)
  - 12h.2-switch: Shared `SettingsSwitchRow` (PR #104)
  - 12h.2-dropdown: Shared `SettingsDropdownRow` (PR #105, merged 2026-05-29)
- ✅ **12h.3** — Smart Settings Search (⌘K) + Help Registry. Cross-cutting all settings panels. Foundation + per-section rollout across PRs #110–#123 (2026-05-29 → 2026-05-30):
  - 12h.3a (PR #111): Foundation — `settings/registry.dart`, `help/registry.dart`, command palette widget, two CI registry-enforcement scripts.
  - 12h.3b-k (PRs #112–#121): Bulk-register each panel's entries + wire help icons (imaging defaults, storage, notifications, site, filenames, filter wheel, equipment auto-connect, safety policies, autofocus, plate solve + diagnostics mode).
  - 12h.3l (PR #123): Visible magnifying-glass affordance in AppShell top bar.
- ✅ **12h.4** (PR #124) — §63 PHD2 settings state (`phd2_settings_state.dart`, 7 tests, 10 fields) + full guider panel migration.
- ✅ **12h.5** — §52.2 Alpaca device chooser. Three sub-PRs (#125, #126, #127):
  - 12h.5a: `DiscoveredDevice` model + `EquipmentDiscoveryApi` dio wrapper + `AlpacaSelectionNotifier` + modal chooser dialog + camera-panel wiring.
  - 12h.5b: Lifted `AlpacaDeviceRow` to a shared widget + wired mount panel.
  - 12h.5c: Wired the row across the remaining 7 equipment panels.
- ✅ **12h.6** — §37 daemon round-trip for every settings panel (PRs #129–#140, tag `phase-12h7-complete`). 11 sub-PRs cloning the same `IProfileStore` foundation across all sections:
  - 12h.6a (PR #129): Server-side imaging-defaults endpoint — `IProfileStore` + `InMemoryProfileStore` foundation.
  - 12h.6b (PR #130): Client `ProfileApi` + imaging-defaults panel hydrate-on-mount + Save → PUT.
  - 12h.6c (PR #131): Storage settings (server + client bundled).
  - 12h.6d-L (PRs #132–#140): Notifications, site, filenames, safety policies, autofocus, plate solve, diagnostics mode, PHD2, equipment-connection (10 auto-connect bools auto-saved via notifier).
  - PR #140 also caught a systemic camelCase-vs-snake_case drift in 11 profile-section OpenAPI schemas and swept all to snake_case.
- ✅ **12h.7** (PR #141) — `FileProfileStore` + `ProfileSnapshotDto`. Settings now survive daemon restart via atomic JSON writes to `{profileDir}/profile.json`. Path resolves env > `/var/lib/openastroara` > `~/.local/share/openastroara`.

### Phase 14 — Tests + AOT hardening + CI matrix
- ✅ **14a** (PR #143) — `AraJsonSerializerContext` source-gen for all 133 DTO records + 7 `CursorPage<T>` instantiations + 8 `IReadOnlyList<T>` collection wrappers + `ProblemDetails`. Closes the long-running AOT-readiness gap that was blocking `dotnet run` smoke testing. Daemon now starts cleanly in Development mode; profile GET/PUT round-trip works end-to-end.
- ✅ **14b** (PR #144, tag `phase-14b-complete`) — server runtime smoke step in CI. After `dotnet build`, the workflow backgrounds the daemon, polls `/healthz`, probes a real DTO endpoint + a 501-stub Problem endpoint, and asserts `profile.json` is written with snake_case keys. Would have caught the 12h.6a JsonTypeInfo bug at PR time.

### Phase 13 — Server placeholder library (PRs #147–#169)

The §60.x endpoint surface was already laid down in Phases 5–9 (141 routes returning 501). Phase 13 walks that surface and replaces each 501-stub with a placeholder service that returns realistic wire shapes, so WILMA client codegen + UI can be exercised end-to-end before real infra (cameras, FITS files, sequencer engine) lands. Each sub-PR also advances the CI smoke gate's 501-probe to a still-unwired endpoint to catch placeholder regressions.

- ✅ **13.1** (PR #147) — `IFrameRepository` placeholder + real `/frames/{id}/preview` + `/frames/{id}/thumbnail` endpoints serving 1×1 PNG samples.
- ✅ **13.2** (PR #148) — `GET /frames/{id}` + `ListAsync` with sample frames (mixed light/dark/flat/bias types, real `FrameType` enum tokens).
- ✅ **13.3** (PR #149) — `PlaceholderSessionService` returning a session list whose ids match frame `session_id` fields from 13.2.
- ✅ **13.4** (PR #151) — `PlaceholderNotificationService` (§42 in-memory CRUD + read/dismiss + bulk operations).
- ✅ **13.5** (PR #152) — `PlaceholderDiagnosticsService` returning §51 yellow-health state + history. Catches the §51-vs-§51.5 `DiagnosticsMode` enum-collision footgun (monitor mode ≠ settings mode).
- ✅ **13.6** (PR #154) — `PlaceholderStatsService` covering all 8 §50 chart views (HFR series, RA/Dec error, focuser, dither, temperature, weather, eccentricity, FWHM).
- ✅ **13.7** (PR #155) — `PlaceholderServerStateService` (§39 snapshot + resume token).
- ✅ **13.8** (PR #157) — `PlaceholderLogService` (§32 ring-buffered log list + filtered query).
- ✅ **13.9** (PR #158) — `PlaceholderBugReportService` (§54 bundle creation + status).
- ✅ **13.10** (PR #159) — `PlaceholderDataManagerService` + `PlaceholderProfileShareService` + `PlaceholderBackupStreamService` (sky-data packages + profile share import/export + §43 streaming hooks).
- ✅ **13.11** (PR #160) — `PlaceholderBackupZipService` (§43 ZIP snapshots — claim/upload/finalize/abort lifecycle).
- ✅ **13.12** (PR #162) — `PlaceholderEquipmentServices` covering all 12 device types (camera, telescope, focuser, filterwheel, guider, rotator, dome, switch, weather, safetymonitor, flatdevice, covercalibrator). Shared `Accepted` helper for 202 OperationAccepted responses.
- ✅ **13.13** (PR #163) — `PlaceholderSequenceService` (CRUD) + `PlaceholderSequencerService` (lifecycle: start/pause/resume/abort).
- ✅ **13.14** (PR #164) — `PlaceholderCalibrationService` + `PlaceholderDarkLibraryService` + `PlaceholderMosaicService` (matching flats, auto-flats, dark library build status, mosaic panels with §47.3 `crosses_ra_wrap` flag).
- ✅ **13.15** (PR #166) — `PlaceholderSequenceTemplateService` + NINA import + `PlaceholderAutoFlatsService`.
- ✅ **13.16** (PR #167) — `/readyz` returns 200 "ready"; `/profiles/{id}/sky-data-recommendations` returns not-installed packages from `IDataManagerService`. Smoke gate 501-probe migrates to `/sessions/{zero-guid}/hfr-analysis` (anchor for the §40.7 time-series aggregation).
- ✅ **13.17** (PR #169) — `InMemoryWsServices` implementing both `IWsBroadcaster` (publish) and `IWsEventChannel` (consume) via a shared singleton. 1000-event replay buffer for §60.9.6 resume; bounded channel (`DropOldest`) for backpressure. `/api/v1/ws` upgrade itself stays 501 — real lifecycle is post-Phase-13 work. Also fixed a latent AOT registration gap on `WsCatalogResponse` that had been silently 500-ing `/ws/catalog` since Phase 14a (smoke gate now probes it).

After Phase 13 the daemon serves realistic shapes for ~all WILMA-facing routes. Functional ground (not placeholders): `/healthz`, `/api/v1/server/info`, `/api/v1/equipment/discover/{type}`, `/api/v1/ws/catalog`, all `/api/v1/profiles/*` settings round-trip + persistence (Phase 12h.6 + 12h.7), `/frames/{id}/preview` + `/thumbnail` (Phase 13.1).

### §60.9 real WS upgrade handler (PRs #172–#176)

Builds the real WebSocket lifecycle on top of the Phase 13.17 `InMemoryWsServices` broadcaster/channel placeholders. Promoted to master via PR #175 (sub-PRs A/B/C); sub-PR D awaiting next promotion on `port/ara`.

- ✅ **Sub-PR A** (PR #172) — accept the upgrade + drain `IWsEventChannel` to JSON text frames. `app.UseWebSockets()` registered with `KeepAliveInterval = 30s`. Passive receive loop with linked CTS detects client Close frames. Best-effort `1000 Normal Closure` on shutdown. Bonus fix: pub-sub fan-out replacement for the single-shared-channel design (multi-client correctness) + a CI smoke-gate WS-upgrade-handshake probe.
- ✅ **Sub-PR B** (PR #173) — `X-Ara-WS-Version: 1` validation. Missing/wrong header → 426 Upgrade Required pre-upgrade with a version-mismatch Problem body (per openapi.yaml line 674). `ProtocolVersion` constant as single source of truth. CI smoke gate added positive (101) + negative (426) probes.
- ✅ **Sub-PR C** (PR #174) — §60.9 resume protocol. First-frame JSON `{ "resume_token": "..." }` parsing with 5s window. Three response shapes (resumed:true / token_expired / token_invalid). Inline replay of every envelope with `seq > last_seen_seq`. Eager per-subscriber registration + high-water-mark dedup (via `Max(Seq)`) closes the snapshot-gap race between replay end and live-stream start. v0.0.1 token format = base-10 stringified last-seen seq.
- ✅ **Sub-PR D** (PR #176) — `KeepAliveTimeout = 60s`. .NET 10 closes the socket with code 1011 if no pong/data arrives within the window — matches openapi.yaml line 680's "2 consecutive missed pongs → server closes" and line 711's mapping of 1011 to "unresponsive client". No manual pong-tracking code required.

Close-code coverage from this work: 1000 (sub-PR A clean close), 1001 (handled by `RequestAborted` propagation), 1011 (sub-PR D timeout), 4003 (sub-PR B version mismatch). Out of scope until real infrastructure: 1009 (frame too large — depends on actual large-frame use cases), 1012 (service restart pairs with §34.7 imminent-restart event), 4001 (auth, v0.1.0 only), 4002 (real opaque resume tokens with 1-hour validity tied to REST `/server/state` issuance), 4004 (single-client policy via §27 takeover state machine).

### Post-§60.9 placeholder cleanup (PRs #179–#183)

After the §60.9 WS lifecycle landed, four sub-PRs flipped the last batch of endpoints from 501-stubs (with `NotImplementedException`-throwing service methods) to standard placeholder-Accepted responses, so WILMA can develop against real wire shapes without the daemon throwing 500s.

- ✅ **#179** — §40.7 `/sessions/{id}/hfr-analysis`: real aggregation (mean / stddev / least-squares slope / `improving`/`degrading`/`stable`/`insufficient-data` trend label) from the per-frame Hfr column on `PlaceholderFrameRepository.SampleFrames`. CI smoke gate's 501-probe anchor migrated to `/frames/{id}/download`.
- ✅ **#180** — §40.8 `/frames/bulk/{rate,tag,delete}`: standard `Accepted` helper with `Idempotency-Key` threaded through. operation_type names follow the route-segment-prefix convention (`frames.bulk-rate` etc.).
- ✅ **#181** — `/sessions/{id}/{resume-target,restretch}`: same shape + 404 existence check before 202 so unknown session ids don't get silent no-op operations.
- ✅ **#182** — `/server/{restart,restart-on-idle}`: optional `?reason=` query string (defaults to `operator_requested`). Real systemd-driven restart still in Phase 14 hardening.

After this sweep, **the only remaining 501 stub is `/api/v1/frames/{id}/download` (§72)** — kept as the CI smoke gate's 501 anchor since it depends on real FITS file storage.

### Phase 15 release-prep docs (PRs #228–#231)

Ship-list documentation per playbook §15.

- ✅ **#228** — `NOTICE.md` (attribution + license inventory + trademark disclaimer per §17.2)
- ✅ **#229** — `RELEASE_NOTES.md` for v0.0.1-ara.1 (referenced by `release.yml`'s `body_path` per §33.7)
- ✅ **#230** — `DEPLOY.md` Pi installation guide (.deb quick-start, ext4 storage setup, fstab, UPS advisory, logs, update/uninstall, troubleshooting)
- ✅ **#231** — Promotion to master.

Still missing from the §15 ship-list: `3rd-party-licenses.txt` — auto-generated from the package graph at release time, deferred until the .deb release pipeline is wired.

### §60.9 server-state polish (PRs #224–#227)

`GET /api/v1/server/state` now returns a fully populated §60.9 snapshot.

- ✅ **#224** — §60.9.6 real `ws_resume_token` from broadcaster `CurrentSequence` (was a literal placeholder string). `ws_event_cursor` aligned with same value.
- ✅ **#225** — §60.9.4 real `diagnostics_health` + `notifications_summary` blobs aggregated from the SQLite-backed services.
- ✅ **#226** — §34.7 `server.restart_imminent` WS event fired before the systemctl spawn so WILMA's reconnect modal can show the right copy.
- ✅ **#227** — Promotion to master.

### §65 stretch pipeline (PRs #207–#216 + variant cache + DELETE)

End-to-end §65 implementation on top of §72 `FitsImage` + the §28 catalog:

- ✅ **#207** — §65.1 algorithms: 7 pure-math stretches (linear, log, asinh, sqrt, equalized, manual, auto_stf) in new `OpenAstroAra.Stretch` project. 14 xUnit tests for monotonicity + dynamic range + distribution spreading. Quickselect for percentile/median/MAD. AOT-safe, no native deps.
- ✅ **#208** — Preview pipeline: SkiaSharp `JpegEncoder` (gray + thumbnail variants) + wire into `SqliteFrameRepository.GetPreviewAsync` / `GetThumbnailAsync`. Read FITS via `FitsImage.ReadImageData16` → stretch → encode JPEG.
- ✅ **#209** — Promotion of #207 + #208 to master.
- ✅ **#210** — §65.2 `stretch_defaults` profile section: 12th section on the §37 profile (light_default + manual_params + asinh_beta + linear_clip_percentiles). `IProfileStore` + endpoints + AOT registration. Persistence verified across daemon restart.
- ✅ **#211** — Thread profile `stretch_defaults` through `GetPreviewAsync` / `GetThumbnailAsync` algorithm + param resolution. Frame-type auto-override (Darks/Bias/Flats → linear) still wins.
- ✅ **#215** — §65.4 variant cache: disk-backed LRU at `<frame>.preview.<stretch-id>.jpg` (manual stretches hash-coalesce by rounded params). Cap 6 variants/frame, atomic write per §28.7.
- ✅ **#216** — §65.6 `DELETE /api/v1/frames/{id}/preview/variants` cache-reset endpoint.

Future §65 sub-PRs:
- §65.5 batch re-stretch (`POST /sessions/{id}/restretch` actually enqueues a job + WS events `session.restretch.{progress,complete,failed}`)
- §65.4 storage-pressure eviction (currently only LRU + per-frame cap)
- WS events on cache lifecycle (`frame.preview.ready` / `variant.ready` / `variant.evicted`)

### §28.8 orphan scan + §13 systemd restart (PRs #212–#214)

- ✅ **#212** — `CaptureScanService` runs on startup: writability check on save path, stale `.tmp` sweep (>5min old), orphan FITS recovery via `FitsImage.ReadHeaders` + INSERT into the catalog. Synthetic recovery session for orphans without a parent session id. Sub-ms no-op on fresh installs.
- ✅ **#213** — §13 systemd-driven `/api/v1/server/restart`: spawns `systemctl restart openastroara-server` with a 2-second delay so the 202 response reaches the client before the daemon dies. Silent no-op on non-Linux dev envs.
- ✅ **#214** — Promotion of #210–#213 to master.

### §72 FITS storage (PRs #197–#200)

CFITSIO via P/Invoke per playbook §72.3, packaged into the new portable `OpenAstroAra.Fits` project (net10.0, AOT-compatible). Managed `FitsImage` wrapper with §28.7 atomic-write pipeline. **Closes the last 501 stub on the surface** — every endpoint now serves a real response.

- ✅ **#197** — Scaffold: project + `[LibraryImport]` P/Invoke wrappers for CFITSIO.
- ✅ **#198** — `FitsImage` managed wrapper + atomic-rename + parent-dir fsync; xUnit tests verify round-trip + atomic semantics + stale-temp purge against `libcfitsio-dev` installed on the Linux CI runner.
- ✅ **#199** — Wire `/api/v1/frames/{id}/download` to the catalog's `file_path` via `FileStream`; last 501 stub gone. `NotImplementedStub` helper deleted.
- ✅ **#200** — Promotion to master.

### §46.5 SQLite notifications log (PRs #201 + #203)

Persistent notifications + JSON-blob preferences in `app_config`. Replaces in-memory placeholder.

- ✅ **#201** — `notifications` + `app_config` tables; `SqliteNotificationService` with list/dismiss/mark-read + UPSERT preferences; 3 fixture seed.
- ✅ **#203** — Promotion (with #202).

### §50 SQLite stats (PRs #202 + #203)

Aggregations over the §28 catalog. Views needing data not yet captured (focuser position, separated RA/Dec RMS) return empty payloads until §38 sequence orchestrator persists those columns.

- ✅ **#202** — `SqliteStatsService` covering all 8 chart views (overview, targets, focus-temp, guiding, frame-quality, best-frames, calendar, CSV export).
- ✅ **#203** — Promotion (with #201).

### §51 SQLite diagnostics (PR #204)

Open issues + history in one `diagnostic_events` table (`cleared_utc IS NULL` = open). Operating mode persists in `app_config` — survives daemon restart (placeholder reset to Observe every launch).

- ✅ **#204** — `SqliteDiagnosticsService` + state/mode/history/seed. Monitor worker that *writes* events arrives with §38.

### §28 frame catalog DB (PRs #190–#195)

Replaces the in-memory `PlaceholderFrameRepository` + `PlaceholderSessionService` with a SQLite-backed catalog. Sessions + frames persist across daemon restarts; bulk rate/tag/delete actually mutate rows; sessions list+get return live aggregates from frames.

- ✅ **#190** — SQLite scaffold: `IAraDatabase` + §28.6 PRAGMAs (WAL, synchronous=NORMAL, etc.) + §28.1 schema (sessions + frames tables) via `CREATE TABLE IF NOT EXISTS`. DI-registered but not yet consumed.
- ✅ **#191** — `SqliteFrameRepository` read path (`ListAsync`, `GetAsync`) + sample seed. Idempotent — survives daemon restart with persistence intact. Same Guids as the prior placeholder so existing CI probes (hfr-analysis on sample session) keep finding the data.
- ✅ **#192** — Bulk ops actually mutate the catalog: `UPDATE` for rate, read-merge-write JSON-blob for tags, `DELETE` for delete. Single transaction per batch.
- ✅ **#193** — `SqliteSessionService`: reads sessions row, aggregates derived fields (target name, light/cal counts, filters used) from frames at read time. Composes on `IFrameRepository` for `GetFramesAsync` + `GetHfrAnalysisAsync`.
- ✅ **#194** — Delete `PlaceholderFrameRepository` + `PlaceholderSessionService`. After this, `IFrameRepository` + `ISessionService` are exclusively SQLite-backed.
- ✅ **#195** — Promotion to master.

Future §28 sub-PRs (deferred until they have a real-infra prerequisite):
- §28.7 atomic-write pipeline — lands with §72 FITS storage (the rename + dir fsync is per-file, not per-row)
- §28.8 startup scan + orphan recovery — needs §72 FITS files to scan
- §28.2 recovery routine — landed with §38j-6 + §38j-7 + §38j-8 (see §38 below). Sequence checkpoint writes + daemon-startup reconciliation + §46 notification emission are in place; equipment reconnect path remains a §38 real-engine concern.

### §38 sequence library + orchestrator scaffold (PRs #236, #248–#278)

Filesystem-backed sequence library + NINA-verbatim JSON schema + placeholder sequencer with realistic run-state + WS event emission. The real engine (NINA's `SequencerFactory` + `SequenceJsonConverter`) is deferred until equipment mocks land — every §38 sub-PR so far hardens the storage + lifecycle scaffold so the real engine can drop in cleanly.

- ✅ **§38-mock (#236)** — `PlaceholderSequencerService` with the full run-state machine (idle → running → paused → running → complete) + §60.9 WS events on every transition. 1-second-per-instruction simulation so WILMA's sequencer UI sees realistic progress.
- ✅ **§38a (#248)** — `FileSequenceService`: filesystem-backed sequence library per §38.2 storage layout (`{profileDir}/sequences/library/{id}.json`). Atomic write via temp + rename. Replaces the in-memory placeholder.
- ✅ **§38b (#250)** — `FilenameTemplateSanitizer` — §38.6.1 sanitization helper (strip control chars, replace path separators) + 10 NUnit fixtures.
- ✅ **§38c (#252)** — `SequenceTemplateVariables` — §38.6 `{{token}}` substitutor + 10 fixtures covering known/unknown tokens + escape rules.
- ✅ **§38d (#254)** — wire `SequenceTemplateVariables.Substitute` into the template instantiate flow so `POST /api/v1/sequences/templates/{name}/instantiate` actually substitutes the body before save.
- ✅ **§38e (#256)** — `SequenceSchemaValidator` — §38.5 structural validation (`schemaVersion` field present + recognized, body parseable) wired to a 422 RFC 7807 response on `POST /api/v1/sequences`.
- ✅ **§38f (#258)** — scaffold all four §38.2 subdirs (`library/`, `imported/`, `templates/`, `active/`) on `FileSequenceService` startup so disk template + import landing zones exist before first use.
- ✅ **§38g (#260)** — load disk-shipped sequence templates from `{profileDir}/sequences/templates/` via `DiskSequenceTemplateService`. Disk entries override built-ins by name.
- ✅ **§38h (#262)** — NINA import: `/api/v1/sequences/import` backfills `schemaVersion: openastroara-sequence-v1` on raw NINA `.json` uploads and persists the raw upload under `imported/{id}.json` for traceability.
- ✅ **§38i (#264)** — `FilenameTemplateValidator` — §38.6.1 sequence-start template check that rejects empty-token bodies + control chars before a run starts, surfaced via a 422 on the sequencer start endpoint.
- ✅ **§38j-1 (#266)** — pause `<PublishAot>true</PublishAot>` on Server so Newtonsoft.Json (NINA's `TypeNameHandling.All` `$type` discriminator path) deserializes the verbatim NINA schema. AOT will be revisited via `[JsonPolymorphic]` post-v0.0.1.
- ✅ **§38j-2 (#267)** — `SequenceBodyInspector` — heuristically counts instructions + targets in a NINA-shaped `$type` body so list responses surface `instructionCount` + `targetCount` per item without deserializing the whole graph.
- ✅ **§38j-3 (#269)** — `SequenceSchemaValidator` gains a capturable-instruction reachability check; strict-mode validation rejects bodies whose root container has zero capturable instructions.
- ✅ **§38j-4 (#271)** — `FileSequenceService.ListAsync` surfaces live `CurrentRunState` per item by composing on `ISequencerService.GetRunStateAsync(id)`. Resolved a DI cycle via `Func<T>` lazy injection.
- ✅ **§38j-5 (#272)** — `PlaceholderSequencerService.StartAsync` reads the real instruction count from the stored body via `SequenceBodyInspector.Inspect()` instead of the hardcoded `DefaultMockInstructionCount = 5`. Falls back to the mock default when no body exists (unit-test path).
- ✅ **§38j-6 (#274)** — `ActiveSequenceCheckpoint` — atomic writer for `{profileDir}/sequences/active/current.json` per §28.1 + §38.2. Writes on every progress step + `StartAsync`; clears in the worker's `finally` block. Provides the canonical "is a sequence running" signal for §28.2 startup reconciliation.
- ✅ **§38j-7 (#276)** — `SequenceStartupReconciler` — §28.2 daemon-startup pass that classifies the previous shutdown as `Clean` / `Interrupted` / `Corrupt`. Interrupted clears the checkpoint per the "no auto-resume" policy. Corrupt applies the §28.1 `.corrupt.<unix-ts>` quarantine.
- ✅ **§38j-8 (#278)** — emit §46 notification on reconciler `Interrupted` (Warning) or `Corrupt` (Critical). Adds `INotificationService.CreateAsync` as the server-emitter surface. `StartupNotificationFactory` translates `Result` → `NotificationDto` so the copy + severity decisions are unit-testable.
- ✅ **§38j-9 (#280)** — emit §51 Red pre-cleared diagnostic event on reconciler `Corrupt`. Mirrors §38j-8 pattern: `IDiagnosticsService.CreateEventAsync` for server-side emitters + `StartupNotificationFactory.DiagnosticForCorruptResult` factory. Event is pre-cleared so it shows in §51 history (not as an open issue) — quarantine already handled the file.
- ✅ **§38k-1 (#282)** — `SequenceBodyDeserializer` bridges the stored `JsonElement` body (with §38.1 `schemaVersion` prefix) through NINA's existing `SequenceJsonConverter` into the `ISequenceContainer` tree. Unknown `$type` values gracefully degrade via `UnknownSequenceContainer`.
- ✅ **§38k-2 (#284)** — `HeadlessSequencerFactory` — minimal `ISequencerFactory` that doesn't need `IProfileService` or the WPF sidebar ceremony. DI-registered alongside `SequenceBodyDeserializer`. Backing lists start empty.
- ✅ **§38k-3 (#286)** — `HeadlessSequencerFactory.WithDefaults()` ships with the three structural container prototypes (`SequenceRootContainer`, `SequentialContainer`, `ParallelContainer`). JSON converter now resolves those types to real instances at the root of a sequence body.
- ✅ **§38k-4 (#288)** — first two no-equipment instruction prototypes registered: `Annotation` (metadata) + `WaitForTimeSpan` (timer via `CoreUtil.Wait`). `SequenceItemCreationConverter` resolves both via the registered prototype lookup.
- ✅ **§38k-5 (#290)** — end-to-end Serialize → Deserialize round-trip validation through the real factory. Five fixtures: empty-container baseline, single-item resolution, JsonProperty value preservation through clone-then-populate, multi-item ordering, System.Text.Json → Newtonsoft bridge via `SequenceBodyDeserializer`.
- ✅ **§38k-6 (#292)** — proper NINA → OpenAstroAra type-name remap in `JsonCreationConverter.GetType()` via new public `NinaTypeRemapper` helper. The inherited code only swapped the assembly suffix; this PR also swaps the class-side namespace so NINA-imported `$type` strings actually resolve. Closes a real port-blocking bug for the §38h import flow.
- ✅ **§38k-7 (#294)** — first two no-equipment condition prototypes registered: `LoopCondition` (iteration count) + `TimeSpanCondition` (elapsed wall-clock).
- ✅ **§38k-8 (#296)** — three round-trip fixtures for the Conditions path (mirror of §38k-5 Items round-trip): `Iterations` JsonProperty preservation, Conditions ordering, combined Items + Conditions populate without interference.
- ✅ **§38k-9 (#299)** — first equipment-mediator stub + first mediator-bound instruction. `HeadlessSafetyMonitorMediator` (smallest interface: `IDeviceMediator` + `IsSafeChanged`) implements every member as a no-op; `GetInfo()` returns a static "not connected, not safe" sentinel. `WaitUntilSafe` registered as a prototype in `HeadlessSequencerFactory.WithDefaults()` (parameterless after mediator injection). Program.cs registers the stub as `ISafetyMonitorMediator` and feeds it to the factory ctor. Establishes the mediator-stub pattern to copy across the equipment tree until real Alpaca drivers land (blocked on §14e). +7 tests. Also relaxed `WithDefaults_registers_utility_instructions` from exact-count to contains-`Annotation`+`WaitForTimeSpan` so future instruction additions don't keep breaking it.
- ✅ **§38k-10 (#301)** — `HeadlessTelescopeMediator` (largest equipment surface: `IDeviceMediator` + ~20 telescope methods + 4 events), all no-op/false. `GetInfo()` → "not connected" `TelescopeInfo`; `GetCurrentPosition()` → `(0,0,J2000)` sentinel. `SetTracking` registered as the next prototype (depends only on `ITelescopeMediator`). +7 tests.
- ✅ **§38k-11 (#303)** — `HeadlessGuiderMediator` (`IDeviceMediator` + ~10 guider methods + 4 events), no-op sentinels (real PHD2 wiring arrives with §63). Bulk-registered 4 telescope-bound prototypes: `UnparkScope` (telescope only), `ParkScope` / `FindHome` / `SlewScopeToRaDec` (telescope + guider — exercises the multi-mediator resolution path). +10 tests.
- ✅ **§38k-12 (#305/#306)** — `HeadlessFocuserMediator` (`IDeviceMediator` + focuser methods + events), no-op sentinels. Registered 3 focuser instructions: `MoveFocuserAbsolute`, `MoveFocuserRelative`, `MoveFocuserByTemperature`. +9 tests.
- ✅ **§38k-13…18 (#315)** — completed the equipment-mediator **stub layer** in one PR (PR-size policy relaxed): five new headless stubs + every instruction prototype that depends only on device mediators (+ the telescope stub).
  - **§38k-13** `HeadlessCameraMediator` (`ICameraMediator`) + 5 camera-control instructions: `CoolCamera`, `WarmCamera`, `SetUSBLimit`, `SetReadoutMode`, `DewHeater`. The exposure-producing members (`Capture`/`Download`/`LiveView`) throw `NotSupportedException` — no honest "empty exposure" sentinel exists (same reasoning as `GetDevice()`); no prototype calls them. +16 tests.
  - **§38k-14** guider-only instructions on the existing guider stub: `StartGuiding`, `StopGuiding`. +2 tests.
  - **§38k-15** `HeadlessFilterWheelMediator` (`IFilterWheelMediator`) — DI-registered to complete the mediator surface; no instruction registered yet (`SwitchFilter` deferred, needs `IProfileService`). +3 tests.
  - **§38k-16** `HeadlessRotatorMediator` (`IRotatorMediator`) + `MoveRotatorMechanical`. +6 tests.
  - **§38k-17** `HeadlessSwitchMediator` (`ISwitchMediator`) + `SetSwitchValue`. +5 tests.
  - **§38k-18** `HeadlessDomeMediator` (`IDomeMediator`) + 7 dome instructions (`Open`/`CloseDomeShutter`, `ParkDome`, `FindHomeDome`, `SlewDomeAzimuth`, `Enable`/`DisableDomeSynchronization` — the last two also take the telescope stub). +13 tests.
  - **Deferred (documented in `PORT_TODO.md`):** `Dither` + `SwitchFilter` (`IProfileService`), `SynchronizeDome` (`IDomeFollower`), and the full `TakeExposure` capture path (`IImagingMediator` + image pipeline). OpenAstroAra.Test 434→**479** (+45); 498 total; clean under `TreatWarningsAsErrors`.
- ✅ **§38k-19/20 (#316)** — completed the **device-mediator stub set** (11/11): `HeadlessFlatDeviceMediator` (`IFlatDeviceMediator`; cover/brightness no-ops) + `HeadlessWeatherDataMediator` (`IWeatherDataMediator`; nothing beyond the base). Both DI-registered. No instruction registered (no flat-device/weather sequence items; the Connect dir defers as the capstone — `Connect*`/`SwitchProfile` need `IProfileService`, `Disconnect*` are `internal` in the Sequencer). These two stubs are the prerequisite the disconnect instructions need (they take all 11 device mediators). OpenAstroAra.Test 480→**485** (+5); 504 total.
- ✅ **§38k-21 (#317)** — `HeadlessDomeFollower` (`IDomeFollower`, the one non-mediator equipment dependency in the §38k instruction set) + registered `SynchronizeDome`, the dome instruction deferred from §38k-18. `GetSynchronizedDomeCoordinates` throws `NotSupportedException` (no honest sentinel for a computed azimuth); all other ops report not-following / `false`. No `IProfileService` needed, so cleanly unblocked. OpenAstroAra.Test 485→**491** (+6); 510 total.
- ✅ **§38k-22 (#318)** — **completed the §38k instruction-registration surface.** `HeadlessProfileService` (`IProfileService`; default in-memory `Profile` + no-op mutators/events) lets the profile-bound instructions construct as prototypes. Registered the last 7: `Dither`, `SwitchFilter`, `ConnectAllEquipment`, `ConnectEquipment`, `DisconnectAllEquipment`, `DisconnectEquipment`, `SwitchProfile`. Flipped the `internal` `DisconnectAllEquipment`/`DisconnectEquipment` → `public` (sibling `ConnectAllEquipment` was already public); that surfaced their `List<string> Devices` to CA1002 → changed to `IReadOnlyList<string>`. **Deliberately does not solve profile source-of-truth** (stub returns a default profile; reconciling `IProfileStore`/`profile.json` with NINA's `ActiveProfile` is an execution-engine concern — prototypes never execute). OpenAstroAra.Test 491→**504** (+13); 523 total.

### §38 execution engine (post-§38k)

- ✅ **§38 real SequencerService (#319)** — **first real sequence execution.** Replaces `PlaceholderSequencerService`'s mock `Task.Delay` loop with a `SequencerService` that deserializes the saved body and drives it through NINA's inherited `Sequencer` (full container semantics: conditions, loops, triggers, nesting). Background worker emits the §60.9 WS lifecycle events + maintains the §28 active-run checkpoint; Abort/Stop cancel via the run's CTS; top-level boundary has a justified CA1031 broad catch (→ Failed + notify, never crash). No-equipment instructions execute for real against the headless stub set; equipment-bound ones no-op. **Deferred** (PORT_TODO "Execution-engine TODOs"): Pause/Resume (no pause hook in the headless engine — accepted no-ops, not faked) + precise `frames_completed` (needs instruction-level hooks). OpenAstroAra.Test 504→**509** (+5). **Merged after 13 review rounds** (the loop caught a production-blocking AOT globalization crash + a long series of real concurrency/lifecycle bugs, each fixed with a test).
- ✅ **§38 precise progress (#320)** — `frames_completed` + `current_instruction_index` now reflect real instruction completion: the worker flattens the deserialized tree to leaf instructions and counts terminal-status leaves (+ the running leaf's index) on each `IProgress` tick. `frames_total` = leaf count. OpenAstroAra.Test 514→**515**.

### §14e — Alpaca simulator integration foundation

- ✅ **§14e simulator harness (#321)** — pinned the ASCOM OmniSim simulators (MIT) per §14.5.1: `fixtures/SIMULATORS_VERSION.md` pins release v0.4.0 + SHA-256 of the linux-x64/aarch64/macos-x64 artifacts; `scripts/get-alpaca-simulators.sh` downloads + verifies + extracts (gitignored, not committed); a CI `alpaca-sim-smoke` job runs the sim headless on the linux-x64 runner and asserts its Alpaca API exposes a Camera device; `SimulatorVersionPinTest` structurally validates the pin file. (User downloaded the OmniSim **source** to `~/Documents/GitHub/ASCOM.Alpaca.Simulators` for reference; CI uses the pinned **release** artifact.) The correct headless invocation is a single `--urls=http://*:32323` process — `--set-no-browser` forces OmniSim's `ProcessArgs()` early-exit before Kestrel binds, and the global-mutex single-instance IPC makes a second copy forward its args and exit, so a pre-run reset never configures the live server.
- ✅ **§14e discovery integration test (PR2, #322)** — `AlpacaDiscoveryIntegrationTest` (`[Category("Integration")]`) runs the daemon's `AlpacaEquipmentDiscoveryService.DiscoverAsync` against a live OmniSim and asserts the UDP-broadcast discovery path (port 32227) surfaces the simulated Camera + Telescope advertising `:32323`. Self-gates via an HTTP probe of `:32323` (`Assert.Ignore` when no sim answers) so it skips cleanly in unit runs / on dev boxes; retries discovery up to 6× to absorb UDP timing flakiness. A dedicated `alpaca-sim-integration` CI job starts the sim and runs `--filter TestCategory=Integration`; the solution-wide `analyzer-gate` test run excludes that category (`TestCategory!=Integration`). Verified green 2/2 (not skipped) on the hosted linux-x64 runner.
- ✅ **§14e first real device service — SafetyMonitor (PR3)** — `SafetyMonitorService` replaces `PlaceholderSafetyMonitorService` as the `ISafetyMonitorService` singleton. On connect it constructs an `ASCOM.Alpaca.Clients.AlpacaSafetyMonitor` from the discovered host/port/device-number and drives the §60.5 lifecycle: `ConnectAsync` flips to `Connecting` and does the blocking Alpaca connect on a background task (202 returned immediately); `GetAsync` reports `Connected`/`Error` + a live `IsSafe` read (off-thread); `DisconnectAsync` tears the client down. Thread-safe via a reentrant `_gate` lock with supersede handling for disconnect/connect races; catch-all boundaries carry `CA1031` log-and-recover suppressions (mirroring `SequencerService`). Coverage: `SafetyMonitorServiceTest` (sim-free: null-before-select, dead-port→`Error`, disconnect→`Disconnected`; runs in the normal suite) + `SafetyMonitorConnectIntegrationTest` (live discover→connect→read→disconnect under `[Category("Integration")]`). **Next:** unify this with the Sequencer's `ISafetyMonitorMediator` so `WaitUntilSafe` reads the live device, then replicate the pattern across the remaining 11 device types.

> Tracking-doc gap note (refreshed 2026-06-09): §38k-9..12 had landed on `master` before this refresh but were only documented through §38k-8. The four mediator stubs (`OpenAstroAra.Server/Services/Equipment/Headless{SafetyMonitor,Telescope,Guider,Focuser}Mediator.cs`) are in place. Current suite = 453 (434 OpenAstroAra.Test + 14 Stretch + 5 Fits); the per-PR `+N tests` running totals above (through 450) predate the −16 `ResponseTests` reduction made during the analyzer-compliance pass (PR #313). Separately, #313 retouched each `Headless*Mediator.GetDevice()` from `null!` to `throw NotSupportedException` — no behavioral change to the §38k prototypes (no caller invokes `GetDevice()` yet).

Future §38 sub-PRs (queued, blocked on equipment-side prerequisites):
- **§38k-22+ profile-bound instructions + capture path** — what remains after the device-mediator stub set (11/11) + `SynchronizeDome` (§38k-21) landed. All blocked on a non-device dependency: `Dither` + `SwitchFilter` + the **Connect capstone** (`ConnectAllEquipment`/`ConnectEquipment`/`SwitchProfile` + the `internal` `Disconnect*` made `public`) need **`IProfileService`** wired into the headless daemon; the full **`TakeExposure` capture path** needs an `IImagingMediator` stub + the image pipeline + exposure-info plumbing. Per playbook §14.5 line 1281, NSubstitute mocks suffice for unit tests; integration tests use real Alpaca simulators per §14.5.1. (Every device-mediator stub + every instruction prototype that depends only on device mediators / `IDomeFollower` landed in §38k-9…21.)
- **§14e Alpaca simulator pinning** — v0.4.0 already published by ASCOMInitiative; un-block per playbook §14.5.1 (SHA-256 download + pre-PR gate + weekly upgrade-check workflow). Once landed, integration tests against real simulators become possible.
- **§38.7 disk-shipped starter templates** — `lrgb-dso.json`, `narrowband-shoo.json`, `comet.json` packaged into the `.deb` at `/opt/openastroara/templates/`. The DiskSequenceTemplateService already discovers `*.json` in the templates dir; needs the actual JSON files + Server.csproj `Content` entries.

### Phase 14 CI matrix expansion (PRs #187 + #188)

Phase 14 §14.3 cross-platform expansion of the existing client-test + server-build jobs.

- ✅ **14c (#187)** — `client-test` job extended from `ubuntu-latest` to `{ubuntu, macos, windows}` matrix. `defaults.run.shell: bash` portable across all three; `fail-fast: false`.
- ✅ **14d (#188)** — server-e2e. After the existing arm64 image build, `docker run` it via qemu and probe `/healthz` (text "ok" per §60.4) + `/api/v1/server/info` (Guid serialization through AOT in the arm64 binary). Catches arm64-specific regressions the x64-host smoke gate doesn't see.

Remaining Phase 14 work: 14e — Alpaca-simulator pinning + integration tests (deferred pending user direction on which simulator to use).

### Phase 0.5p-followup buildfix — Core + Astrometry + Equipment cleanup (PR #43)
- ✅ `OpenAstroAra.Core` — `Notification.cs` scrubbed (CustomDisplayPart references removed; warning/error variants now route to `Logger` with `[CallerXxx]` attribute propagation so the original call site is preserved); `MyMessageBox.cs` `Show(...)` maps affirmative defaults (Yes→No, OK→Cancel) to safe non-affirmative results to prevent `SequenceHasChanged.AskHasChanged` silently auto-detaching; `System.Management 10.0.0` added for WMI usage in `Logger.cs` + `SerialPortProvider.cs`.
- ✅ `OpenAstroAra.Astrometry` — `DatabaseInteraction.cs` reduced to a minimal stub (`GetUT1_UTC` returns 0 with cancellation honored via `Task.FromCanceled<double>(token)`; `GetDisplayAlias` Levenshtein logic preserved); `AstroUtil.cs` + `TopocentricCoordinates.cs` cleaned of stale `using OpenAstroAra.Core.Database;`.
- ✅ `OpenAstroAra.Equipment` — 7 vendor-specific files deleted (EDCamera, MGENGuider, ASCOMInteraction, AllProSpikeAFlat, AlnitakFlatDevice, ArteskyFlatBox, PegasusAstroFlatMaster) per Phase 2 Alpaca-only collapse; Phase 6 `AlpacaEquipmentProvider.cs` SDK signature bug fixed (`deviceType:` → `deviceTypes:`, missing `logger:` arg added). 3 orphaned `OpenAstroAra.Test/FlatDevice/*` files also deleted.
- ⚠️ `OpenAstroAra.Sequencer` — 96 errors from `NINA.WPF.Base` references remain. Tracked in `design/PORT_TODO.md` as a dedicated `phase-0.5p-followup-sequencer` pass (substantive sub-PR-equivalent scope).

## Endpoint surface (as of Phase 9 merge)

**141 endpoint registrations across 11 endpoint files.** Functional today: `/healthz`, `/api/v1/server/info`, `/api/v1/equipment/discover/{type}`, `/api/v1/ws/catalog`. Remaining endpoints return 501 with RFC 7807 Problem bodies until per-area service implementations land — the surface itself is stable for WILMA client codegen (Phase 11+).

- `phase-12e-sky-atlas` — Phase 12e.1 Sky Atlas tab body + Data Manager 4-tab modal stub + §36.13 sky-data-missing banner. webview_flutter Aladin embed + real /api/v1/data-manager/* downloads land in 12e.2.

## Next

- **Real `/api/v1/ws` upgrade handler** — §60.9 WS lifecycle on top of the 13.17 broadcaster/channel. Handshake validation (X-Ara-WS-Version: 1), 30s ping/60s pong heartbeat, resume protocol via `last_seen_seq` + `InMemoryWsServices.ResumeFromAsync`, RFC 6455 close codes (1000/1001/1009/1011/1012/4001–4004).
- **Real-infra ops** — server restart via systemd, FITS file download, frame catalog DB-backed bulk operations, session resume-target/restretch, `/sessions/{id}/hfr-analysis` aggregation (last 501-probe target).
- **Phase 14** — CI matrix expansion (cross-platform client-test macOS/Windows/Linux + server-e2e Docker arm64 + Alpaca-simulator pinning per §14.3 / §14.5). 14a–14d landed (14c #187 client-test matrix, 14d #188 server-e2e arm64); only **14e** (Alpaca-simulator pinning + integration tests) pending, deferred on user direction re: which simulator.
- **Phase 15** — TODO sweep + RPi smoke test + release v0.0.1-ara.1.

## Tag inventory

```text
phase-0.5a-complete   phase-0.5b-complete   phase-0.5c-complete
phase-0.5d-complete   phase-0.5f-complete   phase-0.5g-complete
phase-0.5n-complete   phase-0.5p-complete   phase-2-complete
phase-3-complete      phase-4-complete      phase-5-complete
phase-6-complete      phase-7-complete      phase-8-complete
phase-9-complete
```

(Phase 5-9 tags added retroactively in `port-progress-refresh` after the playbook tracking gap was caught.)
