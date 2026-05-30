# OpenAstro Ara — Port Progress

Single-page status. Updated on every phase boundary. Per PORT_PLAYBOOK.md §20.1, "Currently working on" must point at a specific file, endpoint, or sub-PR — never "various refactoring."

## Current

- **Phase:** §60.9 real WS upgrade handler — sub-PRs A/B/C promoted to master via PR #175 (2026-05-30); sub-PR D on `port/ara` pending next promotion.
- **Last merged:** `ws-handler-d-heartbeat` — PR #176, 2026-05-30. `KeepAliveTimeout = 60s` enables .NET 10 framework-level pong-timeout enforcement (close 1011 on unresponsive client).
- **Currently working on:** Tracking refresh — document the four WS-handler sub-PRs in "Completed" and update the Next pointer past §60.9.
- **Next substantive work:** Real-infra ops on top of the now-live WS infrastructure — server restart via systemd, FITS file download, frame catalog DB-backed bulk ops, session resume-target/restretch, `/sessions/{id}/hfr-analysis` time-series aggregation (last 501-probe target). Plus Phase 14 CI matrix expansion (cross-platform client-test + Alpaca simulator pinning per §14.5).

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
- **Phase 14** — CI matrix expansion (cross-platform client-test macOS/Windows/Linux + server-e2e Docker arm64 + Alpaca-simulator pinning per §14.3 / §14.5). 14a + 14b landed; 14c+ pending.
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
