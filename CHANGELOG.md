# Changelog

All notable changes to OpenAstro Ara are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versioning follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html) —
pre-1.0, breaking changes can happen in any release per the playbook §0.6.

For Ara-specific release tagging convention (`v0.0.1-ara.N`), see
[design/PORT_PLAYBOOK.md §34.5](design/PORT_PLAYBOOK.md). Phase boundary tags
(`phase-N-complete`) are NOT release tags — they're internal milestone markers
used by the port's `port/ara → master` promotion cadence (playbook §22.0).

## How to update this file

Every PR that lands a user-visible change adds a bullet to the **[Unreleased]**
section as part of its diff (convention enforced by the port-driver skill, see
`.claude/skills/port-driver/SKILL.md` step 4). Pick the matching subsection:

- **Added** for new features, endpoints, settings, or capabilities.
- **Changed** for changes in existing behavior.
- **Deprecated** for soon-to-be removed features.
- **Removed** for now-removed features or files.
- **Fixed** for bug fixes.
- **Security** for vulnerability fixes.

Mechanical PRs (pure renames, pure deletes during demolition, doc-only edits to
internal design files) don't need a CHANGELOG entry. When in doubt, add one.

At release time (e.g. `v0.0.1-ara.1`), the **[Unreleased]** section is renamed
to `[0.0.1-ara.1] - YYYY-MM-DD` and a fresh `[Unreleased]` placeholder is added
at the top. This happens in the same commit that pushes the release tag.

---

## [Unreleased]

### Added
- `CHANGELOG.md` at repo root (Keep-a-Changelog format) with backfilled history through `phase-10.5-complete` + the going-forward `[Unreleased]` convention. Convention is reminded by `.claude/skills/port-driver/SKILL.md` step "Open the PR".
- **Phase 11 scaffold** — Flutter WILMA client at `client/openastroara_client/` (`org.openastro.openastroara`, platforms = macos/windows/linux per §18.G mobile-deferred-to-v0.1.0). Pinned Flutter 3.44.0 via `.flutter-version` + pubspec `environment.flutter`. Runtime deps: dio, web_socket_channel, multicast_dns, riverpod, flutter_riverpod, flutter_secure_storage, file_picker. First-run skeleton: `lib/models/server.dart` (AraServer), `lib/services/server_discovery_service.dart` (mDNS scan `_openastroara._tcp.local`), `lib/services/server_api.dart` (dio /api/v1/server/info handshake), `lib/state/server_state.dart` (Riverpod 3.x Notifier-based providers), `lib/screens/first_run_screen.dart` (discovery list + manual entry + handshake panel), `lib/main.dart` (Material 3 dark theme entry). `flutter analyze` clean.
- **Phase 12a — App shell + global infrastructure.** Material 3 dark theme using the §25.2 color tokens (`lib/theme/ara_colors.dart` + `lib/theme/ara_theme.dart`). `AppShell` with `NavigationRail` (5 tabs: Imaging / Framing / Sequencer / Sky Atlas / Options, all stubbed pointing at Phase 12c-12h follow-ups), top equipment bar with §25.3 device-type chips (CAM / FW / FOC / MOUNT / ROT / GUIDE / FLAT / SW / WX / SAFE / DOME — disconnected until Phase 12c wires the Alpaca chooser), bottom status bar with global `StatusIndicator` + bug-report `?` icon stub. `StatusIndicator` widget (§51 health-pill + §53 a11y semantics). `EquipmentChip` widget — the per-device visual primitive. Saved-server persistence via flutter_secure_storage (`SavedServerService` + `SavedServersNotifier` AsyncNotifier); the new `_RootRouter` in `main.dart` routes to FirstRunScreen if no servers saved or AppShell otherwise. FirstRunScreen got a "Save & continue" button — taps it after a successful handshake, writes to the saved-server store, the router auto-swaps to AppShell. Storage hardened against keyring failures so Linux without libsecret degrades to "no saved servers" instead of stranding the user on the error route.
- **Phase 12b — Profile wizard shell.** §37 18-screen / 7-stage wizard scaffold. `ProfileDraft` model (`lib/models/profile_draft.dart`) covers every wizard field — telescope/camera/filter wheel/focuser/mount/rotator/guider/plate-solve/autofocus/file-saving/imaging-defaults/safety/site/sky-data. `WizardController` (`lib/state/wizard_state.dart`) is a Riverpod auto-dispose Notifier with `next` / `back` / `skipCurrent` / `jumpTo` / `snapshot`; tracks skipped screens per §37.8. `ProfileWizard.steps` static catalog maps step number → stage label + screen title. `WizardShell` (`lib/screens/wizard/wizard_shell.dart`) renders progress bar + screen body + bottom nav (Back / Skip / Next / Save Profile) + Save & Exit action. 18 placeholder screens (`lib/screens/wizard/wizard_screens.dart`) — each shows the stage label + title + a one-paragraph description of what the real form will collect (sourced from §37.1-§37.7). Per-screen forms land one-by-one in Phase 12b follow-up PRs. AppShell's bottom status bar gets a "Run profile wizard" entry point that pushes the wizard as a full-screen route.

---

## Pre-release history (backfilled 2026-05-26)

The port started by forking `nighttime-imaging/NINA` and demolishing the WPF
client to leave a `.NET 10` daemon + Flutter client architecture (see
`design/PORT_PLAYBOOK.md` for the full plan). The work below is grouped by
phase boundary; each phase ends with a `phase-N-complete` git tag.

### [phase-10.5-complete] - 2026-05-26

#### Added
- `packaging/debian/` overlay tree for the arm64 `.deb` package — `DEBIAN/control` (Recommends alpaca-bridge + openastro-phd2), `DEBIAN/postinst` (creates `openastroara` user, joins dialout/video/plugdev groups, sets `CAP_SYS_TIME` on the binary, refreshes `systemd-tmpfiles`, validates the sudoers drop-in, enables + starts the service), `DEBIAN/prerm` (graceful stop), `DEBIAN/postrm` (cleanup on purge), hardened `openastroara-server.service` per playbook §13 (NoNewPrivileges + ProtectSystem=strict + ProtectHome + PrivateTmp + RestrictAddressFamilies + CapabilityBoundingSet=CAP_SYS_TIME + SystemCallFilter=@system-service), `sudoers.d/openastroara` (passwordless sudo limited to update.sh + configure-storage.sh), `logrotate.d/openastroara` (daily rotation w/ copytruncate), `tmpfiles.d/openastroara.conf` (creates `/var/run/openastroara` on boot), `etc/openastroara/server.env.example`. (PR #49)
- `packaging/build-deb.sh` — assembles the `.deb` from a `dotnet publish` output + version string. Validates `visudo -cf` and runs `dpkg-deb --build --root-owner-group`. (PR #49)
- CI: `server-build` job now builds the `.deb` on every push and uploads it as an artifact (`openastroara-server_<version>_arm64.deb`, 30-day retention). (PR #49, PR #51)

#### Changed
- systemd unit `PrivateDevices=false` rationale moved off the inline trailing comment to a dedicated comment line — systemd-analyze doesn't accept inline comments on key=value lines. (PR #51)
- CI `.deb` version detection tightened to `git describe --tags --exact-match --match 'v*'` so non-release tags like `phase-10.5-complete` don't get used as Debian Version fields. (PR #51)

### [phase-10-complete] - 2026-05-26

#### Added
- Repo-root `Dockerfile` for the arm64 daemon (`mcr.microsoft.com/dotnet/runtime-deps:10.0-noble-chiseled-arm64v8` base; `EXPOSE 5555` to match the daemon's actual default port; `USER 1000` per §13 hardening). (PR #46)
- CI `server-build` job: `dotnet build` + `dotnet publish OpenAstroAra.Server -c Release -r linux-arm64 --self-contained -p:PublishAot=false` + ELF-arch verification + arm64 Docker buildx via QEMU. Implements the long-promised Phase-0.5p + Phase-4 CI growth. (PR #46)
- `.claude/skills/port-driver/SKILL.md` — autonomous port-driver Claude Code skill that drives sub-PRs under `/loop /port-driver` per design/COMMIT-PR-RULES.md (orient → branch → CR poll/fix → merge → advance phase). Includes the 2026-05-26 `/review` fallback policy for CR rate-limit. (PR #44, clarifications in PR #48)

#### Changed
- `OpenAstroAra.Core/Utility/Notification/Notification.cs` — warning/error variants now route to `Logger.Warning`/`Logger.Error` (with `[CallerXxx]` attribute propagation so the original call site is preserved) instead of dropping operational issues silently. Info/Success stay no-op. (PR #43)
- `OpenAstroAra.Core/MyMessageBox/MyMessageBox.cs` `Show(...)` — affirmative defaults (Yes/OK) map to safe non-affirmative results (No/Cancel) so `SequenceHasChanged.AskHasChanged` no longer silently auto-detaches. Real user choice replaces this when the §35/§60.9 modal-event flow lands. (PR #43)
- `OpenAstroAra.Core.csproj` — added `System.Management 10.0.0` for WMI usage in `Logger.cs` + `SerialPortProvider.cs`. (PR #43)
- `OpenAstroAra.Astrometry/DatabaseInteraction.cs` — reduced to `GetUT1_UTC` stub (honors `CancellationToken`) + `GetDisplayAlias` (full Levenshtein preserved). All other EF6-backed methods removed because their return types referenced deleted schema. (PR #43)

#### Removed
- `OpenAstroAra.Equipment/Equipment/MyCamera/EDCamera.cs` (Canon EDSDK vendor impl, ~1162 lines). (PR #43)
- `OpenAstroAra.Equipment/Equipment/MyGuider/MGENGuider.cs` (MGEN guider, ~596 lines). (PR #43)
- `OpenAstroAra.Equipment/Utility/ASCOMInteraction.cs` (ASCOM COM enumeration factory). (PR #43)
- `OpenAstroAra.Equipment/Equipment/MyFlatDevice/{AllProSpikeAFlat,AlnitakFlatDevice,ArteskyFlatBox,PegasusAstroFlatMaster}.cs` (vendor flat-device impls). (PR #43)
- `OpenAstroAra.Test/FlatDevice/{AlnitakFlatDeviceConnectTest,AlnitakFlatDeviceTest,PegasusAstroFlatmasterTest}.cs` (orphaned tests). (PR #43)

#### Fixed
- Phase 6 ASCOM Alpaca SDK signature bug in `AlpacaEquipmentProvider.cs`: `deviceType:` → `deviceTypes:` + added missing `logger: null` param. Built-but-broken since PR #38 — CI was sanity-only so the build error never surfaced. (PR #43)

### [phase-9-complete] - 2026-05-26

#### Added
- `OpenAstroAra.Server` Phase 9 endpoint surface — log/state + WebSocket + notifications + stats + system scaffold across ~141 endpoint registrations in 11 endpoint files. 75+ WS event tokens catalogued. `GET /api/v1/ws/catalog` returns the live catalog dump. (PR #41)

### [phase-8-complete] - 2026-05-26

#### Added
- `OpenAstroAra.Server` Phase 8 endpoint surface — image + session + backup + diagnostics scaffold. ~15 image DTOs (incl. composite quality score per §50.10, HFR analysis time series, backup-claim DTO), 4 service interfaces, 16 image endpoints, 3 diagnostics endpoints. (PR #40)

### [phase-7-complete] - 2026-05-26

#### Added
- `OpenAstroAra.Server` Phase 7 endpoint surface — sequence + calibration + mosaic scaffold. ~16 sequence records, 8 service interfaces, 16 sequence endpoints (incl. `PATCH /api/v1/sequences/{id}` for partial updates), 6 calibration endpoints, 6 mosaic endpoints (panel DTO includes §47.3 `crosses_ra_wrap` flag). (PR #39)

### [phase-6-complete] - 2026-05-26

#### Added
- `OpenAstroAra.Server` Phase 6 equipment scaffold + Alpaca discovery — 12 DTO records covering all device types, 12 service interfaces, functional `EquipmentDiscoveryService` (Alpaca UDP discovery on port 32227 via `ASCOM.Alpaca.Discovery.GetAscomDevicesAsync`), `GET /api/v1/equipment/discover/{type}` operational, per-device 501 stubs. Global `JsonStringEnumConverter` with `LowerCaseNamingPolicy`. (PR #38)

### [phase-5-complete] - 2026-05-26

#### Added
- `OpenAstroAra.Server/openapi.yaml` — full v0.0.1 API contract. 6 endpoint groups (Server / Equipment / Sequence / Image / Log / Stream). Cursor-based pagination per §60.2. `Idempotency-Key` header per §60.5. RFC 7807 `Problem` schema. `ServerStateSnapshot.ws_resume_token` for §60.9 resume. Full WebSocket protocol documented. OpenAPI 3.1 null unions throughout. (PR #37)

### [phase-4-complete] - 2026-05-25

#### Added
- `OpenAstroAra.Server` project (`Microsoft.NET.Sdk.Web`, `net10.0`, AOT in Release). Kestrel port resolution env → appsettings → 5555. `/healthz` + `/api/v1/server/info` operational. Scalar UI mounted at `/scalar/v1`.

### [phase-3-complete] - 2026-05-25

#### Changed
- `PHD2Guider.cs` now logs `openastro-phd2` vs upstream PHD2 detection via `PHDSubver`/`PHDVersion` substring match. Copyright mojibake fixed.

### [phase-2-complete] - 2026-05-24

#### Changed
- Equipment layer collapsed to Alpaca-only per §52. Added `IEquipmentProvider` per §6.2 with `DiscoverAsync` + `ConnectAsync<T>`.

### [phase-0.5p-complete] - 2026-05-24

#### Changed
- .NET 10 SDK pin (`global.json`) + csproj `TargetFramework` bumps. First build cleanup pass (Astrometry+Core+Test forced to `net10.0-windows` TFM, `UseWPF=true` restored where local build revealed deep WPF deps).
- Solution renamed `NINA.sln` → `OpenAstroAra.sln`. `.gitignore` rewritten.

#### Removed
- `OpenAstroAra.Core/Database/**` per §56 NINA-DB-greenfield (NINADbContext + BrightStars / Constellation / ConstellationBoundary / DsoDetail / EarthRotationParameters / VisualDescription / etc.).
- `OpenAstroAra.Core/Utility/Notification/CustomDisplayPart.xaml{,.cs}` (WPF toast UI).
- `OpenAstroAra.Core/MyMessageBox/MyMessageBoxView.xaml{,.cs}` (WPF dialog view).
- Various `Datatemplates.xaml{,.cs}` + `ProgressStyle.xaml` across Sequencer.

### [phase-0.5n-complete] - 2026-05-24

#### Changed
- Project renames: `NINA.Astrometry` → `OpenAstroAra.Astrometry`, `NINA.Profile` → `OpenAstroAra.Profile`, `NINA.Image` → `OpenAstroAra.Image`, `NINA.Equipment` → `OpenAstroAra.Equipment` (rename + cascade scrub of `using NINA.Equipment`), `NINA.Sequencer` → `OpenAstroAra.Sequencer`, `NINA.Platesolving` → `OpenAstroAra.PlateSolving`, `NINA.Test` → `OpenAstroAra.Test`.

### [phase-0.5g-complete] - 2026-05-24

#### Changed
- Project rename: `NINA.Core` → `OpenAstroAra.Core`.

### [phase-0.5f-complete] - 2026-05-24

#### Removed
- Stefan Berg / NINA branding stripped from user-facing strings.
- All non-English locale `.resx` files (en-US + en-GB only henceforth).

### [phase-0.5d-complete] - 2026-05-24

#### Removed
- ASCOM COM glue across `NINA.Equipment` (replaced by Alpaca in Phase 2).

#### Changed
- CI workflow hardened — `actions/checkout` pinned to commit SHA + `persist-credentials: false` per zizmor + CodeRabbit security findings on PR #12.

### [phase-0.5c-complete] - 2026-05-24

#### Removed
- All vendor SDK directories (Canon/Nikon/ZWO/QHY/etc.) and their vendor concrete equipment impls. Replaced by Alpaca-only providers in Phase 2.

### [phase-0.5b-complete] - 2026-05-23

#### Removed
- `NINA.MGEN`, `nikoncswrapper`, WiX setup projects, NINA Plugin scaffolding.

#### Added
- The four §1 tracking files (PORT_PLAYBOOK.md / COMMIT-PR-RULES.md / PORT_PROGRESS.md / PORT_TODO.md) added retroactively per playbook §1.

### [phase-0.5a-complete] - 2026-05-23

#### Removed
- `NINA/` WPF host project (~600+ files: View, ViewModel, Utility, Database, Resources, External, .sln deregister).
- `NINA.WPF.Base/` (~180+ files: ViewModel, Resources, Interfaces).
- `NINA.CustomControlLibrary/` (~67 files).
- Pure-delete WPF demolition, split across 6 sub-PRs (#4-#9) for CodeRabbit's 150-file-per-PR limit.

#### Added
- `prep-ci` baseline (PR #2): progressive `.github/workflows/ci.yml` placeholder, `.coderabbit.yaml` with `port/ara` in `auto_review.base_branches`, branch-naming Git-ref conflict fix (`port/ara/<name>` → flat `<name>`), AI merge authority granted.
- `rules-tighten` (PR #10): §19.1 merge-gate tightened (no merge on CR rate-limit), §22 periodic `port/ara → master` cadence added (replacing one-shot-at-Phase-15).
- The four §1 tracking files re-pinned in `tracking-files` (PR #11).

### Pre-Phase-0.5 — Design phase (2026-05-23, before any code change)

#### Added
- `design/PORT_PLAYBOOK.md` — 12k+ line port plan, baked across 6 design passes incorporating Tier-1 through Tier-3 decisions on AOT publishing, OpenAPI generation, FITS lib choice, systemd hardening, udev groups, watchdog model, port-number choice (5555), NTP strategy, WebSocket protocol, filename conventions, simulator pinning, API versioning, CORS policy, performance SLOs, Pi hardware matrix, USB cabling advisory, FITS corruption recovery, version compatibility matrix, network bring-up, GFS retention, equipment-DB deferral, NINA-DB greenfield decision, settings-registry gate, §73 exception policy, §8.1 DI mapping, §60.10 WS catalog, §74 CONTRIBUTING placeholder.
- `design/COMMIT-PR-RULES.md` — per-phase + sub-PR rhythm, branch naming, CR poll-and-fix loop, §19.1 merge-gate, `.coderabbit.yaml` config, Phase 12 8-sub-PR split, settings-registry + help-registry mechanical gates.
- `design/GAPS-ARA.md` — feature gaps between NINA and Ara's target.
- `design/PHD2-GAP.md` — PHD2 fork plan for `openastro-phd2`.
- `design/PORT_DECISIONS.md` — append-only decision log.
- `design/PORT_TODO.md` — append-only `TODO(port)` + deferred-CR-finding log.
- `design/API_CONTRACT.md` — early stub (superseded by `OpenAstroAra.Server/openapi.yaml` at Phase 5).

[Unreleased]: https://github.com/open-astro/openastro-ara/compare/phase-10.5-complete...HEAD
[phase-10.5-complete]: https://github.com/open-astro/openastro-ara/compare/phase-10-complete...phase-10.5-complete
[phase-10-complete]: https://github.com/open-astro/openastro-ara/compare/phase-9-complete...phase-10-complete
[phase-9-complete]: https://github.com/open-astro/openastro-ara/compare/phase-8-complete...phase-9-complete
[phase-8-complete]: https://github.com/open-astro/openastro-ara/compare/phase-7-complete...phase-8-complete
[phase-7-complete]: https://github.com/open-astro/openastro-ara/compare/phase-6-complete...phase-7-complete
[phase-6-complete]: https://github.com/open-astro/openastro-ara/compare/phase-5-complete...phase-6-complete
[phase-5-complete]: https://github.com/open-astro/openastro-ara/compare/phase-4-complete...phase-5-complete
[phase-4-complete]: https://github.com/open-astro/openastro-ara/compare/phase-3-complete...phase-4-complete
[phase-3-complete]: https://github.com/open-astro/openastro-ara/compare/phase-2-complete...phase-3-complete
[phase-2-complete]: https://github.com/open-astro/openastro-ara/compare/phase-0.5p-complete...phase-2-complete
[phase-0.5p-complete]: https://github.com/open-astro/openastro-ara/compare/phase-0.5n-complete...phase-0.5p-complete
[phase-0.5n-complete]: https://github.com/open-astro/openastro-ara/compare/phase-0.5g-complete...phase-0.5n-complete
[phase-0.5g-complete]: https://github.com/open-astro/openastro-ara/compare/phase-0.5f-complete...phase-0.5g-complete
[phase-0.5f-complete]: https://github.com/open-astro/openastro-ara/compare/phase-0.5d-complete...phase-0.5f-complete
[phase-0.5d-complete]: https://github.com/open-astro/openastro-ara/compare/phase-0.5b-complete...phase-0.5d-complete
[phase-0.5c-complete]: https://github.com/open-astro/openastro-ara/compare/phase-0.5b-complete...phase-0.5c-complete
[phase-0.5b-complete]: https://github.com/open-astro/openastro-ara/compare/phase-0.5a-complete...phase-0.5b-complete
[phase-0.5a-complete]: https://github.com/open-astro/openastro-ara/releases/tag/phase-0.5a-complete
