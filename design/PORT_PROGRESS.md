# OpenAstro Ara — Port Progress

Single-page status. Updated on every phase boundary. Per PORT_PLAYBOOK.md §20.1, "Currently working on" must point at a specific file, endpoint, or sub-PR — never "various refactoring."

## Current

- **Phase:** §38 sequence library + engine foundation — §38j placeholder hardening (j-1…j-9) + §38k engine wiring foundation (k-1…k-18). The §38k arc gets the JSON-side engine wired end-to-end against `HeadlessSequencerFactory` with structural-container, utility-instruction, and no-equipment-condition prototypes (k-1…k-8), then the **equipment-mediator stub layer** (k-9…k-18): SafetyMonitor/Telescope/Guider/Focuser/Camera/FilterWheel/Rotator/Switch/Dome mediators + every instruction prototype that depends only on device mediators (WaitUntilSafe, SetTracking, the telescope-bound set, focuser/camera/rotator/switch/dome sets, StartGuiding/StopGuiding). The NINA → OpenAstroAra namespace remap in `JsonCreationConverter.GetType()` (§38k-6) closes the bug that made NINA-imported bodies fall to `UnknownSequence*` at the root.
- **Last merged:** PR #319 (§38 real execution engine — `SequencerService`) on `master`, after 13 review rounds that hardened concurrency/lifecycle and fixed a production-blocking AOT globalization crash (`SafeCulture`). **Precise instruction progress (PR #320)** is in review. (Mediator stubs were retouched in #313: `GetDevice()` `null!` → `throw NotSupportedException` — behavior-neutral for the prototypes.)
- **Currently working on:** **§38 progress tracking (PR #320)** — precise `frames_completed`/`current_instruction_index` from leaf-instruction `Status` during the run. OpenAstroAra.Test 514→515; clean under TWAE + invariant mode.
- **Next substantive work:** registration is done; what remains is **execution + the real data models**, which is a larger phase boundary, not more stub-and-register. The big items: (1) the **execution engine** — actually running a resolved sequence body, at which point the profile **source-of-truth** decision is made (reconcile `IProfileStore`/`profile.json` with NINA's `ActiveProfile` so executing instructions read user-edited settings; the `HeadlessProfileService` stub returning a default profile is swapped for the real service here); (2) the full **`TakeExposure` capture path** (`IImagingMediator` stub + the image pipeline + exposure-info plumbing). **Phase 14e Alpaca simulator pinning** per §14.5.1 (v0.4.0 already published by ASCOMInitiative — un-block the SHA-256 download + integration tests). OpenCvSharp4 + libraw wiring per §line-2105 to un-stub the `NotImplementedException`s in `Image/ImageData/`. `IXxxMediator → IXxxService` rename per §8.1 mapping table (cosmetic follow-up). v0.0.1-ara.1 release tag + RPi smoke test (user-blocked).

## Completed

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
