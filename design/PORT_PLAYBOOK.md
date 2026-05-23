# NINA → OpenAstro Ara: Headless Server + Flutter Client (Port Playbook)

**Audience:** an AI agent operating in a private session with no user in the loop, running on Claude Opus via Claude Code with `--dangerously-skip-permissions`.

**Mandate:** transform NINA's WPF codebase on `master` (3.2 line) into a two-product system called **ARA** (short brand) / **OpenAstro Ara** (long brand):

1. **OpenAstroAra.Server** — a headless ASP.NET Core daemon on .NET 10, running on a Raspberry Pi 4/5 (ARM64 Linux). Owns all imaging-session state. Speaks ASCOM Alpaca to equipment (typically via AlpacaBridge on the same Pi). Exposes a REST + WebSocket API.

2. **OpenAstroAra.Client** — a Flutter + Dart application running on **WILMA** (= Windows, iOS, Linux, Mac, Android — the five client platforms, one codebase). Connects to a server when imaging; otherwise self-sufficient for planning, browsing the sky atlas, editing sequences, viewing past frames.

The product model is ASIAir-like: server runs the night, client is for planning and monitoring. Close the laptop, imaging keeps going.

**Responsibility split (important — informs all later sections):**

| WILMA owns ("plan + view") | ARA Core owns ("hardware orchestration") |
|---|---|
| Sky atlas (Aladin Lite + bundled catalogs) | Equipment control (Alpaca devices) |
| Sequence editor (build offline, push when ready) | Sequence execution (receives + runs) |
| Framing assistant (FOV overlay, client compute) | Plate solving (during the actual session) |
| Profile editor (drafts local, sync to Pi) | Guiding (PHD2) |
| Image library viewer (downloaded frames) | Image capture + FITS storage on disk |
| Catalog data (HYG, NGC/IC, Caldwell, comets) | Session state, telemetry, recovery |
| Settings UI + safety policy editor | Equipment-side state machine |

This split is why **WILMA is not a thin client** — it's a planning workstation that can do real work without the Pi connected. The Pi is the hardware orchestrator that runs at night.

**Naming:** all references to `NINA.X` in this document describe the *current* project layout you'll find on disk. Phase 0.5 renames everything to `OpenAstroAra.X` (server-side) or carves it out for deletion. See §17.

**Repository:** single monorepo at `github.com/open-astro/openastro-ara`. Server (.NET) and client (Flutter) live in the same repo because the client is generated from the server's OpenAPI spec — they must move together. Final layout after the port:

```
openastro-ara/                              (repo root, branch: port/ara)
├── README.md  NOTICE.md  LICENSE.txt  COPYING  AUTHORS
├── DEPLOY.md  RELEASE_NOTES.md  3rd-party-licenses.txt
├── global.json  OpenAstroAra.sln  .gitignore  Dockerfile
├── .github/workflows/   (ci.yml, release.yml)
│
├── design/                                 ← working/design docs (NOT shipped)
│   ├── PORT_PLAYBOOK.md                    ← this file
│   ├── GAPS-ARA.md                         ← gap-tracking
│   ├── COMMIT-PR-RULES.md                  ← per-PR strategy (in progress)
│   ├── PORT_DECISIONS.md                   ← created Phase 0.5 (append-only log)
│   ├── PORT_TODO.md                        ← created Phase 0.5
│   ├── PORT_PROGRESS.md                    ← created Phase 0.5
│   └── API_CONTRACT.md                     ← created Phase 5
│
├── OpenAstroAra.Core/                      ← server-side .NET projects at repo root
├── OpenAstroAra.Astrometry/                  (kept at root, matching NINA's layout —
├── OpenAstroAra.Profile/                      simpler port, no directory moves)
├── OpenAstroAra.Image/
├── OpenAstroAra.Equipment/
├── OpenAstroAra.Sequencer/
├── OpenAstroAra.PlateSolving/
├── OpenAstroAra.Server/
│   ├── openapi.yaml                        ← source of truth, regenerates Dart client
│   ├── Program.cs
│   └── Contracts/                          (DTOs)
├── OpenAstroAra.Test/
│
└── client/
    └── openastroara_client/                ← Flutter project (created in Phase 11)
        ├── pubspec.yaml
        ├── lib/
        │   ├── main.dart
        │   ├── api/generated/              ← regenerated from ../../OpenAstroAra.Server/openapi.yaml
        │   ├── screens/  state/  theme.dart
        │   └── ...
        ├── ios/  android/  macos/  windows/  linux/
        └── test/  integration_test/
```

**CI path filters** keep .NET jobs from running on `client/`-only changes and vice versa (see §14.3).

---

## Table of Contents

**Operating rules + design principles + tracking (§0–1)**
- [§0 Read this first — operating rules](#0-read-this-first--operating-rules)
- [§0.5 Design principles](#05-design-principles)
- [§1 Branch + tracking files](#1-branch--tracking-files)

**Architecture + phased plan (§2–3)**
- [§2 Target stack (locked)](#2-target-stack-locked--do-not-deviate)
- [§3 Phased plan (execute strictly in order)](#3-phased-plan-execute-strictly-in-order)

**Per-phase implementation (§4–15)**
- [§4 Phase 0.5 — Fork hygiene + project demolition](#4-phase-05--fork-hygiene--project-demolition)
- [§5 Phase 1 — Bump non-UI projects to .NET 10](#5-phase-1--bump-non-ui-projects-to-net-10)
- [§6 Phase 2 — Equipment to Alpaca-only](#6-phase-2--equipment-to-alpaca-only)
- [§7 Phase 3 — PHD2 client repoint](#7-phase-3--phd2-client-repoint)
- [§8 Phase 4 — OpenAstroAra.Server scaffold](#8-phase-4--openastroaraserver-scaffold)
- [§9 Phase 5 — API contract](#9-phase-5--api-contract)
- [§10 Phases 6–9 — Implement endpoints](#10-phases-69--implement-endpoints)
- [§11 Phase 10 — Server smoke test](#11-phase-10--server-smoke-test)
- [§12 Phases 11–13 — Flutter client](#12-phases-1113--flutter-client)
- [§13 RPi deployment](#13-rpi-deployment-phase-10--phase-15)
- [§14 Testing (Phase 14)](#14-testing-phase-14)
- [§15 Build + verification gate](#15-build--verification-gate-run-after-every-phase)

**Governance + cross-cutting (§16–24)**
- [§16 Stuck-state policy](#16-stuck-state-policy)
- [§17 Fork hygiene — naming, MPL preservation](#17-fork-hygiene--naming-identifiers-mpl-preservation)
- [§18 Feature decisions (baked-in A–I)](#18-feature-decisions-baked-in)
- [§19 Auto-approve safety rails](#19-auto-approve-safety-rails)
- [§20 Quota-resume protocol](#20-quota-resume-protocol)
- [§21 Localization](#21-localization)
- [§22 Final pass (Phase 15)](#22-final-pass-phase-15)
- [§23 Quick reference — bash one-liners](#23-quick-reference--bash-one-liners)
- [§24 What "done" looks like](#24-what-done-looks-like)

**UI + design (§25)**
- [§25 Visual design reference — cloning NINA's UX](#25-visual-design-reference--cloning-ninas-ux)

**Technical deep-dives (§26–28)**
- [§26 Image processing on Linux — OpenCvSharp4](#26-image-processing-on-linux--opencvsharp4-migration)
- [§27 Connection policy — single-client at a time](#27-connection-policy--single-client-at-a-time)
- [§28 Sequence durability & crash recovery](#28-sequence-durability--crash-recovery)

**Storage + first-run + sync (§29–31)**
- [§29 Storage / disk-space policy (USB MANDATORY)](#29-storage--disk-space-policy)
- [§30 First-run + launch flow (client)](#30-first-run--launch-flow-client)
- [§31 Time + location sync (WILMA waterfall)](#31-time--location-sync-wilma-waterfall)

**Resilience + updates + distribution (§32–34)**
- [§32 Network resilience (WILMA ↔ Pi)](#32-network-resilience-wilma--pi)
- [§33 Version compatibility + WILMA-pushed updates](#33-version-compatibility--wilma-pushed-updates-asiair-model)
- [§34 Distribution + install (apt.openastro.net)](#34-distribution--install-aptopenastronet)

**Safety + sky atlas + wizard (§35–37)**
- [§35 Safety policies (user-configurable)](#35-safety-policies-user-configurable-per-profile)
- [§36 Sky imagery + Data Manager](#36-sky-imagery--data-manager-wilma)
- [§37 Profile setup wizard](#37-profile-setup-wizard)

**Sequence + calibration + image library (§38–41)**
- [§38 Sequence file format + NINA import](#38-sequence-file-format--nina-json-import)
- [§39 Calibration frames + session-matching flats](#39-calibration-frames--session-metadata-driven-auto-flats)
- [§40 Captured-image library workflow](#40-captured-image-library-workflow)
- [§41 Mobile companion mode (iOS/Android)](#41-mobile-companion-mode-ios--android)

**Operational features (§42–44)**
- [§42 Hardware fault recovery](#42-hardware-fault-recovery-per-equipment)
- [§43 Backup + restore](#43-backup--restore)
- [§44 Real-time backup stream to desktop WILMA](#44-real-time-backup-stream-to-desktop-wilma)

**Polar alignment + notifications + mosaic + auto-cal (§45–48)**
- [§45 Polar alignment — iPolar-style loop](#45-polar-alignment--ipolar-style-continuous-loop)
- [§46 Notifications system](#46-notifications-system)
- [§47 Mosaic imaging (multi-panel)](#47-mosaic-imaging-multi-panel)
- [§48 Auto-flats and dark library](#48-auto-flats-and-dark-library-sequence-automation)

**API docs + analytics + diagnostics (§49–51)**
- [§49 API documentation serving (Swagger UI)](#49-api-documentation-serving)
- [§50 Session analytics + Stats dashboard](#50-session-analytics--stats-dashboard)
- [§51 Real-time acquisition diagnostics](#51-real-time-acquisition-diagnostics--smart-corrections)

**Hardware philosophy + a11y + privacy (§52–54)**
- [§52 Mount handling — Alpaca-only commitment](#52-mount-handling--alpaca-only-commitment--feature-detection)
- [§53 Accessibility (WCAG AA-leaning)](#53-accessibility-wcag-21-aa-leaning-baseline)
- [§54 Bug report submission + PII handling](#54-bug-report-submission--pii-handling)

**Forward-looking (§55–56)**
- [§55 v0.1.0+ Roadmap (consolidated)](#55-v010-roadmap-consolidated)
- [§56 Migrating from NINA](#56-migrating-from-nina)

**API conventions (§60)**
- [§60 API conventions](#60-api-conventions)

**Mount safety + meridian flip + autofocus + dither + guider + discoverability (§57–59, §61–63)**
- [§57 Stop Mount + slew safety](#57-stop-mount--slew-safety)
- [§58 Meridian flip workflow](#58-meridian-flip-workflow)
- [§59 Autofocus — Smart Focus + Classic AF fallback](#59-autofocus--smart-focus--classic-af-fallback)
- [§61 Smart settings search](#61-smart-settings-search)
- [§62 Dither policy](#62-dither-policy)
- [§63 PHD2 lifecycle + profile/dark-library push](#63-phd2-lifecycle--profiledark-library-push)

**Live view + image stretching + concurrency + security (§64–67)**
- [§64 Live View / Loop Imaging](#64-live-view--loop-imaging)
- [§65 Image stretching pipeline + preview API](#65-image-stretching-pipeline--preview-api)
- [§66 Server concurrency model](#66-server-concurrency-model)
- [§67 Security model](#67-security-model)

---

## 0. Read this first — operating rules

1. **No questions.** If you would otherwise ask "which option do you prefer?", pick the option this document recommends. If silent, pick the option that minimizes diff size, write a one-line note in `design/PORT_DECISIONS.md`, and continue.
2. **No scope creep.** This is a *port + restructure*, not a redesign. The sequencer, equipment state machines, profile schema, coordinate math, plate-solver integration, PHD2 client, and image processing logic all come from NINA as-is. Do not "improve" working logic — just move it across the new boundary.
3. **No half-finished states.** Always work on `port/ara` (the working branch for the entire port). Each commit must leave the solution buildable for everything ported so far.
4. **Cite when stuck.** When you genuinely cannot translate a construct, leave a `// TODO(port): <one sentence>` and a placeholder that compiles, log it in `design/PORT_TODO.md`, and move on. Sweep TODOs in Phase 15.
5. **Verify continuously.** After every phase, run the build + tests gate in §15. Do not start the next phase until the gate is green for everything completed so far.
6. **Commit cadence.** One commit per logical unit (one project converted, one endpoint implemented, one view ported). Commit messages: `port(<area>): <what>`. Never amend; always new commits. Never `--no-verify`.
7. **No upstream plugin compatibility.** ARA is a hard fork. The plugin SDK is **deferred to v0.1.0** — Phase 0.5 deletes the plugin loader and plugin browser UI entirely. Do not preserve any compatibility with NINA plugins.
8. **Full-auto operation.** You are running with auto-approve on. Hard git safety rails (§19) apply unconditionally — no force pushes, no `--no-verify`, no destructive ops outside the explicit deletion lists.
9. **Tag every phase boundary; open the PR; wait for merge before the next phase.** Per `design/COMMIT-PR-RULES.md`, the port ships as a sequence of phase PRs (plus sub-PRs within Phase 0.5 and Phase 12) targeting the `port/ara` integration branch. At the end of each phase or sub-phase, after the §15 gate is green: tag with `git tag phase-N[-letter]-complete && git push --tags`, update `design/PORT_PROGRESS.md`, push the sub-branch, open the PR, run the CodeRabbit poll-and-fix loop (see COMMIT-PR-RULES.md), then **wait for the user to merge** before pulling the updated `port/ara` and starting the next phase or sub-phase. AI never merges (permanent rule, see §19.1). Auto-continuation across sub-PRs within a phase happens automatically after merge; between phases the same auto-continuation applies unless the user has paused.
10. **Quota interruption is normal.** When the model session hits its weekly limit and resumes, the first action is to read `design/PORT_PROGRESS.md` to find out where to continue. See §20.

---

## 0.5. Design principles

Three pillars guide every UX decision in this document. Every section that follows should be re-readable through this lens — if a feature regresses against any of these principles, it's wrong even if it technically works.

1. **Better than NINA.** ARA imitates NINA's layout (§25) to make existing users feel at home, but every UX decision *beyond* layout is evaluated against "would a NINA user notice this is better?" If a decision lands merely equivalent to NINA, the bar isn't met. Areas where ARA must clearly beat NINA: settings discoverability (§61), real-time diagnostics (§51), session analytics (§50), calibration workflows (§39), mount safety (§57).

2. **AI-assisted design, native polish.** ARA's design is produced with AI collaboration, which lets us spend more time on UX coherence than NINA's solo-developer model could. We do **not** ship an AI assistant inside v0.0.1 — we ship the *outcome* of using AI during design: better defaults, better discoverability, better wording, fewer half-finished surfaces, fewer abandoned settings panels.

3. **Robust capabilities, friendly surfaces.** All the power-user features (§35 safety policies, §39 calibration, §47 mosaics, §50 stats, §51 diagnostics, §57 mount safety, §58 meridian flip) ship in v0.0.1, but **finding them must be easy**. The §61 smart settings search makes every setting discoverable in under 10 seconds without prior knowledge of where it lives. PR review rule: a setting that isn't searchable doesn't merge.

The AI executing this port should reject its own work if a feature lands behind buried menus, vague labels, or "find the right tab" UX. Capability without discoverability is a regression against NINA, not an improvement.

---

## 1. Branch + tracking files

```bash
git fetch origin
git checkout port/ara   # branch already exists
```

Create four tracking files in the `design/` directory and commit them empty (`design/` already exists and contains `PORT_PLAYBOOK.md`, `GAPS-ARA.md`, `COMMIT-PR-RULES.md`):

- `design/PORT_DECISIONS.md` — append-only log of every non-obvious decision, with file:line refs.
- `design/PORT_TODO.md` — append-only list of every `TODO(port)` and `PORT_BLOCKED` you leave in code, grouped by phase.
- `design/PORT_PROGRESS.md` — single-page status, see §20.1.
- `design/API_CONTRACT.md` — append-only design log for the server↔client API; one entry per endpoint or wire-shape decision.

**First commit:** `port(setup): add port tracking files`.

---

## 2. Target stack (locked — do not deviate)

### 2.1 Server (OpenAstroAra.Server)

| Concern | Value |
|---|---|
| Language | C# |
| Runtime | **.NET 10** (current LTS through Nov 2028) |
| Web framework | ASP.NET Core minimal API |
| Target frameworks | pure `net10.0` — no multi-targeting, no `-windows` anywhere |
| SDK pin | `global.json` pinning SDK 10.0.x with `rollForward: latestFeature` |
| Hosting | Kestrel listening on configurable port (default **5555**), no reverse proxy. Overridable via `OPENASTROARA_PORT` env var or `appsettings.json`. mDNS advertises the actual bound port (§32.4). |
| Persistence | SQLite via `Microsoft.Data.Sqlite` 10.0.x for session/profile; FITS files on disk |
| Equipment | **Alpaca only.** `ASCOM.Alpaca.Components` 2.1.0+. No ASCOM COM, no native vendor SDKs. |
| Guiding | PHD2 JSON-RPC client (existing NINA code, repointed to openastro-phd2) |
| Plate solving | ASTAP external process (cross-platform binary) |
| Image format | FITS via existing NINA codecs; preview JPEGs generated server-side via **OpenCvSharp4** (replaces `System.Drawing.Common` which doesn't work on Linux ARM64 — see §26) |
| Logging | Serilog (already in NINA) → file sink at `/var/log/openastroara/` (Linux) |
| Discovery | mDNS announce `_openastroara._tcp.local` via `Zeroconf` NuGet |
| Deployment | systemd service `openastroara-server.service`, runs as `openastroara` user |
| Target hardware | RPi 4 (4GB+) or Pi 5 (any), ARM64 Linux (Raspberry Pi OS Bookworm / Ubuntu 24.04 ARM64). Also runs on x64 Linux for development. |
| Inherited from NINA | `Core`, `Astrometry`, `Profile`, `Image`, `Equipment`, `Sequencer`, `Platesolving` |
| Deleted from NINA | main `NINA` (WPF host), `NINA.WPF.Base`, `NINA.CustomControlLibrary`, `NINA.MGEN`, `NINA.Plugin`, `NINA.Setup`, `NINA.SetupBundle`, `nikoncswrapper`, all vendor SDK folders, DirectShow webcam code |

### 2.2 Client (OpenAstroAra.Client)

| Concern | Value |
|---|---|
| Language | Dart |
| Framework | Flutter (stable channel, 3.x+ — pick latest at port time) |
| Target platforms | macOS, iOS, Android, Windows, Linux desktop |
| HTTP client | `dio` (supports interceptors and progress callbacks for image downloads) |
| WebSocket | `web_socket_channel` |
| State management | Riverpod |
| API client | generated from server's OpenAPI spec via `openapi_generator` |
| mDNS discovery | `multicast_dns` plugin (cross-platform) |
| Secure storage | `flutter_secure_storage` reserved for future v0.1.0 remote-access tokens (§67.4); not used in v0.0.1 |
| File picker | `file_picker` plugin |
| Image rendering | Flutter's built-in `Image` widget for JPEG previews; FITS handled by a Dart FITS package (or inline parser if no suitable package exists) |
| Build outputs | macOS `.app` + `.dmg`, iOS `.ipa`, Android `.apk`/`.aab`, Windows `.exe`/`.zip`, Linux AppImage |
| App icons | placeholder during AI port; user supplies real assets pre-release |

### 2.3 Wire protocol

| Concern | Value |
|---|---|
| Transport | HTTP/1.1 over TCP (no TLS in v0.0.1 — local LAN only; opt-in TLS for v0.1.0) |
| Encoding | JSON for ops; JPEG bytes for image previews; FITS bytes for full-frame downloads |
| Operations | REST endpoints under `/api/v1/...` |
| Live updates | WebSocket at `/api/v1/stream` — server pushes sequence progress, frame complete, log lines, equipment state changes |
| Authentication | **None in v0.0.1.** Trusted-LAN posture matching ASCOM Alpaca + ZWO ASIAir; see §67 for the full security model. All endpoints open. v0.1.0 introduces opt-in remote-access mode with TLS + token auth for over-internet deployments. |
| Discovery | mDNS service type `_openastroara._tcp.local`; TXT records expose version, hostname, port |
| Contract | OpenAPI 3.1 spec at `OpenAstroAra.Server/openapi.yaml` — source of truth; Dart client and server-side validation both derive from it |

---

## 3. Phased plan (execute strictly in order)

```
Phase 0.5 — Fork hygiene + project demolition
            §4, §17, §18 (decisions) — rename, license headers, delete WPF/plugins/vendor SDKs/WiX/WebView2/MGEN/COM
            **Split into 16 sub-PRs (0.5a–0.5p) per `design/COMMIT-PR-RULES.md`** — DELETE before RENAME pattern, each sub-PR
            stays under CodeRabbit's 200-file free-tier limit. See COMMIT-PR-RULES.md for the full mapping. Order:
            0.5a (delete WPF UI) → 0.5b (delete MGEN/nikoncswrapper/WiX/Plugin) → 0.5c (delete vendor SDKs, may sub-split) →
            0.5d (delete ASCOM COM) → 0.5e (delete WebView2 refs) → 0.5f (strip Stefan branding + license headers) →
            0.5g–0.5n (project renames: Core → Astrometry → Profile → Image → Equipment → Sequencer → PlateSolving → Test) →
            0.5o (rename solution + global identifiers) → 0.5p (.NET 10 bump; may absorb Phase 1).
Phase 1   — Bump non-UI projects to .NET 10
            §5 (may be absorbed into 0.5p per COMMIT-PR-RULES.md)
Phase 2   — Equipment layer to Alpaca-only
            §6, §52 (Alpaca-only philosophy)
Phase 3   — Repoint PHD2 client at openastro-phd2
            §7
Phase 4   — Create OpenAstroAra.Server (ASP.NET Core skeleton)
            §8, §29 (storage config flow, USB-mandatory), §31 (time-sync foundation)
Phase 5   — Define API contract + OpenAPI spec + Swagger UI
            §9, §38 (sequence schema), §49 (Swagger UI)
Phase 6   — Equipment endpoints + fault detection + dew/switch handling
            §6, §42 (per-equipment fault recovery), §45 (polar alignment endpoints)
Phase 7   — Sequence endpoints + calibration + mosaic + auto-flats prompt
            §38, §39, §47, §48
Phase 8   — Image endpoints + previews + composite quality score + real-time diagnostics monitor loop
            §40 (server side), §44 (backup stream queue), §50.10 (composite score), §51 (diagnostic loop)
Phase 9   — Log/status endpoints + WebSocket stream + notifications + Stats endpoints
            §46, §50 (server-side analytics), §28 (recovery)
Phase 10  — Server smoke test on Linux x64 + ARM64
            §11, gate checks for all server-side endpoints + linux-arm64 publish
Phase 11  — Flutter client scaffold + first-run + server connect + handshake + a11y baseline
            §12, §30, §53 (a11y from the start; StatusIndicator widget)
Phase 12  — Flutter views: app shell + all main tabs (Imaging, Framing, Sequencer, Sky Atlas, Image Library, Stats, Settings)
            §25, §36 (Sky Atlas/Aladin + Data Manager), §37 (wizard), §40 (image library UI), §50 (Stats dashboard UI),
            §51 (Health Indicator + Diagnostic Panel UI), §41 (mobile companion shell selection),
            §32 (disconnect modal), §35 (safety UI), §54 (bug report flow), §61 (smart settings search),
            §63 (PHD2 settings), §64 (Live View UI), §65 (stretch picker + manual sliders)
            **Split into 8 sub-PRs (12a–12h) per `design/COMMIT-PR-RULES.md` — see that doc for the full mapping table.**
            Order: 12a (shell) → 12b (wizard) → 12c–12g (independent feature tabs, any order) → 12h (Settings + search registry, last to consolidate).
Phase 13  — Image preview pipeline end-to-end (server JPEG gen + client display + pinch-zoom)
            §12.5, §40.2
Phase 14  — Tests + GitHub Actions CI matrix
            §14, §14.3
Phase 15  — TODO sweep + RPi smoke test + release v0.0.1-ara.1
            §22, DEPLOY.md + README written, .deb published, .dmg/.exe/.AppImage on GitHub Releases (desktop only per §18.G; mobile deferred to v0.1.0)
```

**Sub-PR rhythm (Phase 0.5 + Phase 12):** Each sub-PR is opened as a separate GitHub PR targeting `port/ara`. AI runs `scripts/pre-pr-check.sh` (§14.4) → opens PR with screenshots if user-visible UI changed → CodeRabbit poll-and-fix loop runs (60 s polling per COMMIT-PR-RULES.md) → user merges → AI pulls updated `port/ara` and starts the next sub-PR automatically. Phase 15 final PR goes `port/ara → master`.

Do **not** start Phase N+1 until Phase N (and all its sub-PRs) passes the §15 gate AND has been merged into `port/ara` by the user.

**Cross-cutting work:** distribution path (§34 .deb on apt.openastro.net) is set up during Phase 14 CI. Documentation (DEPLOY.md, NOTICE.md, README, MOUNT_TIPS.md) is written incrementally during the relevant phases — the AI updates docs as each feature lands, not at the end. Migration guide (§56) is written during Phase 15.

---

## 4. Phase 0.5 — Fork hygiene + project demolition

### 4.1 Project rename

1. Rename every kept `NINA.X` csproj file and directory to `OpenAstroAra.X` (mapping below).
2. `NINA.sln` → `OpenAstroAra.sln` with all new paths.
3. Global namespace rename `NINA.X` → `OpenAstroAra.X` in every `.cs` file via bulk find-replace.
4. Update assembly name, root namespace, `AssemblyTitle` in each csproj.
5. `NINA.sln.licenseheader` → `OpenAstroAra.sln.licenseheader`.

Kept-project mapping:

| Old | New |
|---|---|
| `NINA.Core` | `OpenAstroAra.Core` |
| `NINA.Astrometry` | `OpenAstroAra.Astrometry` |
| `NINA.Profile` | `OpenAstroAra.Profile` |
| `NINA.Image` | `OpenAstroAra.Image` |
| `NINA.Equipment` | `OpenAstroAra.Equipment` |
| `NINA.Sequencer` | `OpenAstroAra.Sequencer` |
| `NINA.PlateSolving` | `OpenAstroAra.PlateSolving` |
| `NINA.Test` | `OpenAstroAra.Test` |

Build must succeed after rename on Windows (`dotnet build OpenAstroAra.sln -c Debug`). WPF UI is still present at this point — only renaming.

Commit: `port(fork): rename NINA.* projects and namespaces to OpenAstroAra.*`.

### 4.2 Delete WPF UI + irrelevant projects

Delete unconditionally:

- `NINA/` (main WPF project — App.xaml, MainWindow, all Views/ViewModels, `NINA/Resources/`)
- `NINA.WPF.Base/`
- `NINA.CustomControlLibrary/`
- `NINA.Plugin/`
- `NINA.MGEN/`
- `NINA.Setup/`, `NINA.SetupBundle/` (WiX)
- `nikoncswrapper/`
- All per-vendor folders under `OpenAstroAra.Equipment/SDK/CameraSDKs/` (Canon, Nikon, ZWO/ASI, QHY, Atik, PlayerOne, Altair, Touptek, FLI, SBIG, Omegon, Meade DSI, DirectShow webcams)
- All `NINA/External/<vendor>/` folders containing bundled DLLs
- `Accord.Imaging/` — audit first; if anything kept truly references it, vendor only the specific source files needed and delete the rest

For each deleted equipment device class, **leave the abstraction interface** (`ICamera`, `ITelescope`, `IFocuser`, `IFilterWheel`, `IRotator`, `IFlatPanel`, `ISwitch`, `IWeatherData`, `IDome`, `ISafetyMonitor`, `IGuider`) intact in `OpenAstroAra.Equipment` — only concrete implementations are deleted.

Update `OpenAstroAra.sln` to remove deleted project references.

Commit: `port(demolish): delete WPF UI, vendor SDKs, MGEN, WiX, plugin host`.

### 4.3 Strip Stefan branding

- Any hardcoded `nighttime-imaging.eu` URLs → `github.com/open-astro/openastro-ara`
- "NINA"/"N.I.N.A." in remaining log lines, config paths, exception messages → "OpenAstro Ara"
- Patreon/donate references → delete
- `crowdin.yml` → delete; non-English `Locale.*.resx` → delete (English-only, §18.E)
- Sectigo cert thumbprint + `signtool` invocation → delete
- `AstrophotographyBuddy_TemporaryKey.pfx` → delete
- Logo files (`Logo_Nina.ico`, splashes) → delete; server has no UI

Commit: `port(fork): strip Stefan branding, swap URLs to open-astro`.

### 4.4 License headers

Per §17.3, append the Open Astro copyright line on every modified file. MPL header stays. Stefan's existing copyright line stays.

### 4.5 Phase 0.5 gate

Before tagging `phase-0.5-complete`:

- `dotnet build OpenAstroAra.sln -c Debug` succeeds (kept projects build; deleted ones gone).
- `OpenAstroAra.sln` does not reference deleted projects.
- No "NINA" / "N.I.N.A." in user-visible strings in kept projects (verify with `grep`).
- `LICENSE.txt`, `COPYING`, `AUTHORS` unchanged. `NOTICE.md` exists at repo root.
- Previously-passing tests in `OpenAstroAra.Test` still pass (tests that depended on deleted WPF code are deleted, not skipped).
- `bin/Debug/` contains no vendor SDK DLLs, `nikoncswrapper.dll`, MGEN binaries, or WPF assemblies.

### 4.6 `.gitignore` rewrite (Phase 0.5o sub-PR scope)

NINA's `.gitignore` was tailored to a Visual Studio + WiX + ClickOnce + Windows-only workflow with no Flutter. ARA's `.gitignore` adds Flutter cache paths + ARA-specific outputs + tool caches and drops the Windows-specific patterns no longer relevant.

Phase 0.5o sub-PR (rename solution + global identifiers) is the natural home — it already touches solution-level files. The complete replacement contents:

```gitignore
# === Build outputs ===
**/bin/
**/obj/
**/publish/
**/AppPackages/
**/*.dll.config
**/*.pdb
**/*.user
**/*.suo
**/.vs/
**/*.cache

# === .NET tool / SDK caches ===
.dotnet/
.nuget/
**/global.json.lock
**/project.lock.json

# === Flutter / Dart ===
client/openastroara_client/.dart_tool/
client/openastroara_client/build/
client/openastroara_client/.flutter-plugins
client/openastroara_client/.flutter-plugins-dependencies
client/openastroara_client/.packages
client/openastroara_client/pubspec.lock  # NOT ignored — keep for reproducibility (un-comment to ignore)
client/openastroara_client/ios/Pods/
client/openastroara_client/android/.gradle/
client/openastroara_client/android/local.properties
client/openastroara_client/linux/flutter/ephemeral/
client/openastroara_client/macos/Flutter/ephemeral/
client/openastroara_client/windows/flutter/ephemeral/
client/openastroara_client/web/  # not built in v0.0.1 per §18.G

# === ARA-specific runtime + dev paths ===
/publish/
/artifacts/
/coverage/
OpenAstroAra.Server/openapi.yaml.bak
OpenAstroAra.Test/fixtures/alpaca-simulators/      # auto-downloaded per §14.5.1
OpenAstroAra.Test/TestResults/

# === Editor / IDE ===
.vscode/*
!.vscode/settings.json       # commit project-level settings
!.vscode/extensions.json     # commit recommended extensions
!.vscode/launch.json         # commit shared launch configs
.idea/                       # JetBrains Rider — wholesale ignored
*.iml
*.swp
*.swo
*~

# === macOS / Linux / Windows OS files ===
.DS_Store
Thumbs.db
desktop.ini

# === Secrets (per §19.4 safety) ===
*.pfx
*.key
*.pem
*.env
appsettings.Secrets.json
secrets.dart

# === Logs / runtime state (per §29.9) ===
*.log
*.log.*.gz
*.tmp
*.bak

# === Local dev databases ===
*.db-journal
*.db-wal
*.db-shm

# === Removed-during-port artifacts (Phase 0.5 cleanup) ===
# These directories no longer exist but ignore patterns
# remain to catch any stray reintroduction:
WiX/
**/MGEN/
**/nikoncswrapper/
**/Setup/
```

**Notes for Phase 0.5o sub-PR:**

- Replace NINA's existing `.gitignore` wholesale (don't append)
- Run `git status` after to verify no previously-tracked files become untracked (an existing tracked file matching a new pattern keeps tracking; the pattern only affects untracked-future-additions)
- If any tracked file should be removed AND ignored, do a separate explicit `git rm --cached <path>` step (don't conflate "stop tracking" with "ignore future")
- `OpenAstroAra.Test/fixtures/alpaca-simulators/` is gitignored per §14.5.1 — the simulators are CI-downloaded at job start, not committed
- `pubspec.lock` is intentionally NOT ignored — committed for reproducible Flutter builds (matches Dart's recommendation for apps; libraries differ but ARA is an app)

---

## 5. Phase 1 — Bump non-UI projects to .NET 10

1. Pin SDK with `global.json` at repo root:
   ```json
   {
     "sdk": {
       "version": "10.0.100",
       "rollForward": "latestFeature"
     }
   }
   ```
   (Use whatever 10.0.x is current — `dotnet --list-sdks`.)

2. Every kept `OpenAstroAra.*.csproj`:
   - `<TargetFramework>net8.0-windows</TargetFramework>` → `<TargetFramework>net10.0</TargetFramework>`
   - `<TargetFramework>net8.0</TargetFramework>` → `<TargetFramework>net10.0</TargetFramework>`
   - Delete `<UseWPF>`, `<UseWindowsForms>`, `<ImportWindowsDesktopTargets>`, `<EnableWindowsTargeting>`.

3. Bump every `Microsoft.Extensions.*` and `System.*` package from 8.0.x to 10.0.x (`dotnet list package --outdated` finds them):
   - `Microsoft.Extensions.DependencyInjection` 8.0.1 → 10.0.x
   - `System.Drawing.Common` 8.0.10 → 10.0.x
   - `System.Runtime.Caching` 8.0.1 → 10.0.x
   - `System.IO.Ports` 8.0.0 → 10.0.x
   - `System.ComponentModel.Composition` 8.0.0 → 10.0.x
   - `System.Management` → **delete** (WMI; not needed post-§4.2)
   - `System.Data.SQLite` → switch to `Microsoft.Data.Sqlite` 10.0.x (actively maintained, ARM64-native)

4. Verify cross-platform: `dotnet restore && dotnet build OpenAstroAra.sln -c Debug` on Windows, macOS, Linux ARM64.

5. Run tests. Fix any analyzer escalations or obsoleted APIs.

Commit: `port(net10): bump all projects to net10.0; add global.json`.

---

## 6. Phase 2 — Equipment to Alpaca-only

### 6.1 Csproj

`OpenAstroAra.Equipment/OpenAstroAra.Equipment.csproj`:

```xml
<PropertyGroup>
  <TargetFramework>net10.0</TargetFramework>
</PropertyGroup>

<ItemGroup>
  <PackageReference Include="ASCOM.Alpaca.Components" Version="2.1.0" />
  <PackageReference Include="ASCOM.Alpaca.Device" Version="2.1.0" />
  <PackageReference Include="ASCOM.Tools" Version="2.1.0" />
</ItemGroup>
```

Remove `ASCOM.Com.Components` if present. Delete `#if WINDOWS` blocks.

### 6.2 Provider

```csharp
public interface IEquipmentProvider {
    string Id { get; }              // "alpaca"
    string DisplayName { get; }     // "ASCOM Alpaca"
    Task<IReadOnlyList<DiscoveredDevice>> DiscoverAsync(DeviceType type, CancellationToken ct);
    Task<T> ConnectAsync<T>(DiscoveredDevice device, CancellationToken ct) where T : class;
}
```

Single impl: `AlpacaEquipmentProvider`. Uses `AlpacaDiscovery` for broadcast UDP on port 32227. Hands out typed proxies from `ASCOM.Alpaca.Components`.

### 6.3 Strip COM call sites

Find and delete every `ASCOM.Com`, `ASCOM.DriverAccess` (COM path), `Marshal.GetActiveObject` reference in `OpenAstroAra.Equipment`. Replace device-instantiation paths with Alpaca equivalents.

Commit: `port(equipment): collapse to Alpaca-only`.

---

## 7. Phase 3 — PHD2 client repoint

Existing PHD2 client at `OpenAstroAra.Equipment/Equipment/MyGuider/PHD2/` speaks PHD2's JSON-RPC over TCP. openastro-phd2 preserves the protocol; minimal change:

- Default host: keep `localhost:4400`. Server runs on Pi; PHD2 typically runs on same or sibling Pi.
- On connect: call `get_app_state`, log version. If response identifies as openastro-phd2, log "Connected to openastro-phd2 vX on Linux."

Commit: `port(equipment): default PHD2 client to openastro-phd2`.

---

## 8. Phase 4 — OpenAstroAra.Server scaffold

```bash
dotnet new web -n OpenAstroAra.Server -f net10.0
```

`OpenAstroAra.Server.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\OpenAstroAra.Core\OpenAstroAra.Core.csproj" />
    <ProjectReference Include="..\OpenAstroAra.Astrometry\OpenAstroAra.Astrometry.csproj" />
    <ProjectReference Include="..\OpenAstroAra.Profile\OpenAstroAra.Profile.csproj" />
    <ProjectReference Include="..\OpenAstroAra.Image\OpenAstroAra.Image.csproj" />
    <ProjectReference Include="..\OpenAstroAra.Equipment\OpenAstroAra.Equipment.csproj" />
    <ProjectReference Include="..\OpenAstroAra.Sequencer\OpenAstroAra.Sequencer.csproj" />
    <ProjectReference Include="..\OpenAstroAra.PlateSolving\OpenAstroAra.PlateSolving.csproj" />
  </ItemGroup>

  <PropertyGroup>
    <!-- AOT mode per §71 -->
    <PublishAot>true</PublishAot>
    <InvariantGlobalization>true</InvariantGlobalization>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.AspNetCore.OpenApi" Version="10.0.*" />
    <PackageReference Include="Scalar.AspNetCore" Version="2.*" />  <!-- AOT-friendly Swagger UI replacement per §71.3 -->
    <PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" Version="10.0.*" />
    <PackageReference Include="Microsoft.Data.Sqlite" Version="10.0.*" />
    <PackageReference Include="Serilog.AspNetCore" Version="9.*" />
    <PackageReference Include="Zeroconf" Version="3.*" />
  </ItemGroup>
</Project>
```

`Program.cs` skeleton — mirror NINA's `CompositionRoot` DI registrations for non-UI services, CORS, WebSocket support, mDNS announce. No auth middleware (per §67). Endpoints are mapped in subsequent phases. AOT discipline per §71 — all DTOs go through `AraJsonContext` source generator; no runtime reflection.

Commit: `port(server): scaffold OpenAstroAra.Server with DI from NINA CompositionRoot`.

---

## 9. Phase 5 — API contract

Hand-write `OpenAstroAra.Server/openapi.yaml`. Endpoint groups:

| Group | Path prefix | Purpose |
|---|---|---|
| Server | `/api/v1/server` | Version, capabilities, handshake, current state summary |
| Equipment | `/api/v1/equipment/{type}` | List/connect/control cameras, mounts, focusers, filter wheels, rotators, etc. |
| Sequence | `/api/v1/sequence` | Load JSON, validate, start/pause/resume/abort, status |
| Image | `/api/v1/image` | List frames, download FITS, request preview JPEG |
| Log | `/api/v1/log` | Recent log lines |
| Stream | `/api/v1/stream` | Single WebSocket for live updates |

**Auth:** none in v0.0.1 per §67 (trusted-LAN posture matching ASCOM Alpaca + ZWO ASIAir). All endpoints open. No `X-OpenAstroAra-Token` header, no rate limiting on auth attempts, no `/api/v1/server/handshake` token-validation endpoint. v0.1.0 introduces opt-in remote-access mode that re-adds TLS + token auth for over-internet deployments.

**Versioning:** URL versioning (`/api/v1/...`). Within v0.x, breaking changes within `/api/v1/` permitted and documented in `design/API_CONTRACT.md`.

**WebSocket protocol:** client connects without auth (per §67). Server sends JSON `{ "type": "equipment.state" | "sequence.progress" | "log.line" | "frame.complete" | ..., "ts": "...", "payload": {...} }`. Client may send `{"type":"subscribe","channels":["log","frames"]}` to filter. Default: all channels.

Commit: `port(api): define OpenAPI v1 contract`.

---

## 10. Phases 6–9 — Implement endpoints

Each phase implements one endpoint group. Pattern per endpoint:

1. Add route in the appropriate `Map*Endpoints` extension.
2. Inject the existing NINA service that owns the underlying state (e.g., `IEquipmentMediator` → rename to `IEquipmentService`, drop the UI-flavored "Mediator" naming).
3. Map request DTOs to internal types; map internal types back to response DTOs. DTOs live in `OpenAstroAra.Server/Contracts/`.
4. Hook progress/state-change events into the WebSocket broadcaster.
5. Write a `WebApplicationFactory`-based test in `OpenAstroAra.Test`.
6. Update `openapi.yaml`.

Commit per endpoint group: `port(api): equipment endpoints`, etc.

---

## 11. Phase 10 — Server smoke test

### 11.1 Cross-platform publish

```bash
dotnet publish OpenAstroAra.Server -c Release -r linux-arm64 --self-contained -o ./publish/arm64
dotnet publish OpenAstroAra.Server -c Release -r linux-x64   --self-contained -o ./publish/x64
```

Both must succeed.

### 11.2 Docker

`Dockerfile` at repo root:

```dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:10.0-bookworm-slim-arm64v8
WORKDIR /app
COPY publish/arm64/ ./
EXPOSE 5400
USER 1000
ENTRYPOINT ["./OpenAstroAra.Server"]
```

```bash
docker build -t openastroara-server:arm64 .
docker run --rm -p 5400:5400 openastroara-server:arm64
curl http://localhost:5400/api/v1/server/info     # expect 200
```

### 11.3 Real Pi (if available)

Copy `publish/arm64/` to `/opt/openastroara/`, create systemd unit (§13), `systemctl enable --now openastroara-server`. Hit the same endpoint from another LAN host. Verify mDNS via `dns-sd -B _openastroara._tcp` (macOS) or `avahi-browse -t _openastroara._tcp` (Linux).

If no physical Pi: the Docker arm64 image is sufficient.

Commit: `port(server): smoke test on linux-arm64`.

---

## 12. Phases 11–13 — Flutter client

### 12.1 Scaffold

```bash
mkdir client
cd client
flutter create --org org.openastro --project-name openastroara \
    --platforms macos,ios,android,windows,linux openastroara_client
cd openastroara_client
flutter pub add dio web_socket_channel multicast_dns riverpod flutter_riverpod \
    flutter_secure_storage file_picker
flutter pub add --dev openapi_generator build_runner
```

Configure `openapi_generator` to read `../../OpenAstroAra.Server/openapi.yaml`, generate Dart client into `lib/api/generated/`. Run via `dart run build_runner build`.

### 12.2 First-run flow

The first screen:
1. **Server discovery** — mDNS scan for `_openastroara._tcp.local`. List discovered servers with hostname, version. Manual "Add server by IP:port" option below.
2. **Connect** — once a server is selected, WILMA calls `/api/v1/server/info` to confirm reachability + version compatibility (§33). On 200, save server's hostname + port to local state and connect. No token, no auth (§67).
3. **Connected** — navigate to the main app shell.

### 12.3 App shell — clone NINA's layout

The client UX deliberately mirrors NINA so existing users feel at home. **See §25 for the full visual design reference**, which the AI must read before writing any client UI code. Brief summary here:

**Desktop layout** (macOS / Windows / Linux):
- **Top equipment bar** — horizontal strip of device icons (camera, filter wheel, focuser, mount, rotator, guider, dome, switch, flat panel, weather, safety monitor). Each shows a connection indicator (gray = disconnected, green = connected, red = error). Click to open chooser; double-click to connect/disconnect.
- **Left panel** (collapsible) — profile selector + active equipment chooser dropdowns + manual control widgets per connected device.
- **Center workspace** — tabbed:
  - **Imaging** — main image viewer, live preview, exposure controls, histogram overlay, plate-solve overlay
  - **Framing Assistant** — target search + sky chart + rotation preview
  - **Sequencer** — tree-based instruction editor (Areas → Targets → Instructions)
  - **Sky Atlas** — DSO catalog browser
  - **Options** — settings tree (Equipment, Imaging, Plate Solving, Astrometry, etc.)
- **Right panel** (collapsible) — histogram + image statistics, plate solve results, log tail.
- **Bottom status bar** — clock (local + sidereal), current sequence operation, progress bar, connection state to server.

**Mobile layout** (iOS / Android):
- Bottom tab bar with 5 destinations: **Status**, **Equipment**, **Sequence**, **Images**, **More** (settings, logs).
- The "Status" tab is the mobile-shaped Dashboard from §12.3's earlier description: at-a-glance state of the imaging session. Mobile is read-mostly / nudge-only — full editing UX is desktop-class.

**Theme:** dark, astro-friendly. Near-black background (`#1a1a1a`), darker panels (`#262626`), accent colors for status (green `#4caf50`, yellow `#ffb300`, red `#e53935`, blue `#42a5f5`), text in soft white (`#e0e0e0`). Exact tokens defined in §25.

**Resizable splits:** use `multi_split_view` Flutter package for resizable side panels. **Dockable/rearrangeable panels are a v0.1.0 feature** — for v0.0.1, panels are positioned fixed (left/right/bottom) and only resizable, not draggable.

**No bitmap icons from NINA.** All icons are placeholders (Flutter's `Icons.*` material set or simple SVG primitives) until the user supplies real ARA assets. Layout is the clone; pixels are original.

### 12.4 State management

Riverpod providers per section. WebSocket connection is a singleton provider streaming typed events. Each section subscribes to its slice.

### 12.5 Image preview pipeline (Phase 13)

Server side, on capture complete:
1. Sequencer writes FITS to disk (existing NINA path).
2. `IImagePreviewGenerator` produces downscaled JPEG (max 1920×1080, quality 80, stretched per user setting) saved as `<frame>.preview.jpg`.
3. WebSocket sends `{"type":"frame.complete","payload":{"id":"...","previewUrl":"/api/v1/image/{id}/preview","fitsUrl":"/api/v1/image/{id}/fits","previewBytes":N}}`.
4. Client receives event, fetches preview JPEG, displays. Full FITS pulled only on user-initiated download.

Commit per stage: `port(client): mDNS discovery + connect`, `port(client): app shell`, `port(client): equipment view`, etc.

---

## 13. RPi deployment (Phase 10 + Phase 15)

Per-Pi setup (one-time — AI documents in `DEPLOY.md`):

```bash
sudo useradd -r -s /usr/sbin/nologin openastroara
sudo mkdir -p /opt/openastroara /etc/openastroara /var/log/openastroara
sudo chown -R openastroara:openastroara /opt/openastroara /var/log/openastroara
sudo chown root:openastroara /etc/openastroara
sudo chmod 750 /etc/openastroara
```

Per release, GitHub Actions builds:
- `linux-arm64` self-contained, gzipped as `openastroara-server-0.0.1-ara.N-linux-arm64.tar.gz`.
- Optional `.deb` via `dotnet-packaging` (lower priority; tarball works).

systemd unit at `/etc/systemd/system/openastroara-server.service` (installed by .deb postinst per §34.3):

```ini
[Unit]
Description=OpenAstro Ara Server
Documentation=https://github.com/open-astro/openastro-ara
After=network-online.target
Wants=network-online.target

[Service]
Type=notify
User=openastroara
Group=openastroara
ExecStart=/opt/openastroara/OpenAstroAra.Server
EnvironmentFile=/etc/openastroara/server.env
WorkingDirectory=/var/lib/openastroara

Restart=on-failure
RestartSec=3

# === Watchdog (per fourth-pass Tier 2 #8) ===
# Server's hosted-service heartbeat task pings sd_notify("WATCHDOG=1")
# every 30s; systemd auto-restarts if no ping for 60s.
WatchdogSec=60

# === Hardening per §67 (defense in depth on trusted LAN) ===

# Process restrictions
NoNewPrivileges=true
LockPersonality=true

# Filesystem isolation
ProtectSystem=strict
ProtectHome=true
PrivateTmp=true
PrivateDevices=false   # cameras + USB serial need /dev access via udev rules
ReadWritePaths=/var/log/openastroara
ReadWritePaths=/var/lib/openastroara
ReadWritePaths=/media/openastroara
ReadWritePaths=/var/run/openastroara
ReadWritePaths=/etc/openastroara

# Network restrictions
RestrictAddressFamilies=AF_INET AF_INET6 AF_UNIX

# Capability restrictions (CAP_SYS_TIME for §31 time sync; nothing else)
CapabilityBoundingSet=CAP_SYS_TIME
AmbientCapabilities=CAP_SYS_TIME

# Syscall filter (allow standard service syscalls only)
SystemCallFilter=@system-service
SystemCallErrorNumber=EPERM

# Standard output to journald (Serilog also writes to /var/log/openastroara per §29.9)
StandardOutput=journal
StandardError=journal
SyslogIdentifier=openastroara

[Install]
WantedBy=multi-user.target
```

**Watchdog implementation note (Phase 4 / §66):** the server registers a background `IHostedService` that calls `Systemd.Notify("WATCHDOG=1")` every 30 seconds via the `Mono.Unix.Native` interop OR a thin P/Invoke wrapper around `sd_notify(0, "WATCHDOG=1")` from libsystemd. The heartbeat task checks that the main capture pipeline + WebSocket dispatcher are responsive (each maintains a last-tick timestamp; if any is > 45 s stale, the watchdog skips its ping — letting systemd kill + restart). Hung-daemon detection per §66.

**Test cases (added §14.1):**
- `watchdog_pings_systemd_every_30s` — verify sd_notify call rate against a mock systemd socket
- `hung_capture_pipeline_stops_watchdog_ping` — inject a hang into the capture worker, assert watchdog stops pinging within 45 s
- `systemd_unit_passes_systemd-analyze_security` — CI runs `systemd-analyze security openastroara-server.service`; gates on exposure level ≤ 3.0 (medium)

**Audit:** run `systemd-analyze security openastroara-server.service` after install — should report exposure level around 2.0-3.0 (medium safety) with the hardening above. Going lower requires the paranoid hardening set; deferred per the §13.X decision but considered if/when a security review prompts it.

---

## 14. Testing (Phase 14)

Three test layers per component, plus a pre-PR gate the AI runs before opening any PR. Coverage is soft (no hard %); equipment simulators provide all hardware fixtures.

### 14.1 Server tests — three layers

`OpenAstroAra.Test` (already exists from NINA — preserved per §17). Layers:

**Unit tests** — xUnit + NSubstitute. Per-class business logic, mocked dependencies. Fast (~ms). Inherited NINA tests stay in place; new code adds unit tests at the same time.

**Integration tests** — xUnit + `WebApplicationFactory<Program>`. Real SQLite, real file I/O, in-process Alpaca simulators per §14.5. Tests the slices that unit tests can't (e.g., §28.2 recovery flow, §28.7 FITS atomic-write, §29.1.4 configure-storage helper invocation, §39 calibration matching, §63 PHD2 lifecycle). Mid-speed (~hundreds of ms per test).

**E2E smoke tests** — actual published binary on Linux ARM64 (Docker container or Pi). Runs the full server + a real openastro-phd2 + Alpaca simulators, exercises a 2-target sequence end-to-end, verifies frames land + DB rows match + previews generate. Slow (~minutes). Runs in CI per phase (gated) and manually on a real Pi at Phase 15.

**Settings registry tests** — `scripts/check-settings-registry.mjs` invoked from `dotnet test` for any PR that touches profile schema (per COMMIT-PR-RULES.md settings-registry gate). Verifies every new setting has a registry entry with `id`, `label`, `description`, non-empty `keywords`, valid `path`.

### 14.2 Flutter tests — three layers

In `client/openastroara_client/`:

**Widget tests** — `flutter_test` + `mocktail`. Per-widget rendering + interaction. API client mocked (the generated client accepts a `Dio` — inject `MockDio`). Fast.

**Integration tests** — `integration_test` package. Full app on a Flutter test device, mocked server responses. Tests user flows: first-run → server discovery → connect → wizard → main app; sequence editor → save → run; frame viewer → stretch picker. Mid-speed.

**Manual UI verification** — for any PR touching user-visible UI (per §14.6), AI launches `flutter run -d linux`, exercises the changed flow, captures screenshots (golden path + at least one edge case), attaches to PR description before opening. Catches visual regressions widget tests miss (layout shifts, theme issues, accessibility regressions).

**Settings registry tests** — same `scripts/check-settings-registry.mjs` runs in `flutter test` for any PR touching settings UI (per COMMIT-PR-RULES.md gate).

### 14.3 CI matrix (`.github/workflows/ci.yml`)

| Job | Runner | Steps |
|---|---|---|
| server-build | `ubuntu-latest` | `dotnet build`, `dotnet test` (unit + integration), publish `linux-arm64` + `linux-x64` |
| server-e2e | `ubuntu-latest` | Docker-based E2E smoke against a Linux ARM64 image (qemu) — runs on phase boundaries only |
| client-macos | `macos-latest` | `flutter build macos --release`, `flutter build ios --no-codesign` |
| client-windows | `windows-latest` | `flutter build windows --release` |
| client-linux | `ubuntu-latest` | `flutter build linux --release`, `flutter build apk --release` |
| client-test | `ubuntu-latest` | `flutter test`, `flutter test integration_test/`, `flutter analyze` |
| settings-registry | `ubuntu-latest` | `node scripts/check-settings-registry.mjs --pr-diff` — fails PR if new settings lack registry entries |

On tag `v0.0.1-ara.*`, also a `release` job uploading artifacts to a GitHub Release.

Commit: `port(ci): GitHub Actions for server + Flutter client + registry gate`.

### 14.4 Pre-PR gate (`scripts/pre-pr-check.sh`)

The AI runs this before every `gh pr create`. Exit code 0 = green; anything else = AI fixes + reruns before opening the PR. Same checks run in CI, so a green pre-PR gate predicts a green CI run.

```bash
#!/usr/bin/env bash
# scripts/pre-pr-check.sh
# Runs the same checks CI runs, locally, before opening a PR.
# Exit non-zero on any failure.
set -euo pipefail

CHANGED_FILES="$(git diff --name-only origin/port/ara...HEAD)"

if echo "$CHANGED_FILES" | grep -qE '\.cs$|\.csproj$|\.sln$|openapi\.yaml$'; then
  echo "→ Server changes detected; running C# gate..."
  dotnet format --verify-no-changes
  dotnet build -c Release --nologo
  dotnet test -c Release --logger "console;verbosity=minimal"
  dotnet publish OpenAstroAra.Server -c Release -r linux-arm64 --self-contained -o /tmp/pre-pr-publish
  if echo "$CHANGED_FILES" | grep -q 'openapi\.yaml$'; then
    scripts/check-openapi-lint.sh
  fi
fi

if echo "$CHANGED_FILES" | grep -qE '^client/.*\.(dart|yaml)$|pubspec\.yaml$'; then
  echo "→ Client changes detected; running Flutter gate..."
  pushd client/openastroara_client >/dev/null
  dart format --set-exit-if-changed .
  flutter analyze
  flutter test
  if find integration_test/ -name '*.dart' -newer ../../.git/HEAD 2>/dev/null | grep -q .; then
    flutter test integration_test/
  fi
  flutter build linux --release
  popd >/dev/null
fi

if echo "$CHANGED_FILES" | grep -qE 'lib/screens/settings/|lib/settings/registry\.dart$|profile\.schema\.json$'; then
  echo "→ Settings change detected; running registry gate..."
  node scripts/check-settings-registry.mjs --staged
fi

echo "✓ Pre-PR gate green"
```

**For PRs touching user-visible Flutter UI:** the AI additionally runs §14.6 manual UI verification (screenshots), which the script doesn't automate — that step happens outside `pre-pr-check.sh` and the AI attaches screenshots to the PR description manually.

**Runtime budget:** typical full run ~2–5 minutes on a modern laptop. Acceptable cost before opening a PR.

**Failure handling:** script exits non-zero with a clear message; AI fixes the failure + reruns the script; only opens the PR once green. No `--no-verify`-style bypass.

### 14.5 Equipment simulators (test fixtures)

ARA does NOT test against real hardware in CI. Three simulator sources:

**Alpaca simulators** — Camera, Mount, Focuser, FilterWheel, Rotator, Dome, Switch, ObservingConditions, SafetyMonitor. Source: [ASCOMInitiative/ASCOM.Alpaca.Simulators](https://github.com/ASCOMInitiative/ASCOM.Alpaca.Simulators) (separate repo from ASCOMPlatform — these are the Alpaca-native simulators specifically). Pre-built release artifacts downloaded from GitHub Releases; see §14.5.1 for pinning + upgrade policy.

**PHD2 simulator** — PHD2's built-in `Simulator` camera + `Simulator` mount drivers. openastro-phd2 ships with the same. Test harness launches headless openastro-phd2 (`xvfb-run -a openastro-phd2 --headless --headless-auto-connect`) per §63.2, sets the profile to use simulators, connects via JSON-RPC.

**Custom in-process fakes** — for fast unit tests where a real simulator is overkill (e.g., testing the §28 recovery state machine without actually polling Alpaca), tests use NSubstitute mocks of the Alpaca interfaces. Integration tests use the real simulators above.

Real-hardware testing is maintainer-run at release boundaries (Phase 15 manual smoke on a real Pi with at least one real ZWO camera + one real mount). Not in CI.

### 14.5.1 Alpaca simulator version pinning + auto-PR upgrade workflow

The Alpaca simulators are a load-bearing test dependency — every integration + E2E test runs against them. Pin behavior is policy, not implementation detail.

**Current pin: v0.4.0** (released by ASCOMInitiative, the most recent at the time of this section's authoring). Pinned via `OpenAstroAra.Test/fixtures/SIMULATORS_VERSION.md`:

```markdown
# Alpaca simulators — version pin

Source: https://github.com/ASCOMInitiative/ASCOM.Alpaca.Simulators
Pinned release: v0.4.0
Pinned SHA: <full commit SHA at the release tag>
Downloaded artifacts:
  - ascom.alpaca.simulators-linux-x64.zip   sha256: <...>
  - ascom.alpaca.simulators-linux-arm64.zip sha256: <...>
  - ascom.alpaca.simulators-macos-x64.zip   sha256: <...> (dev only)
  - ascom.alpaca.simulators-macos-arm64.zip sha256: <...> (dev only)
  - ascom.alpaca.simulators-win-x64.zip     sha256: <...> (dev only)
Last verified: 2026-05-23
Verified by: <committer name + commit SHA>
```

Binaries themselves are gitignored at `OpenAstroAra.Test/fixtures/alpaca-simulators/`; CI + pre-PR gate downloads them on demand using the pinned SHA-256 checksums as the integrity gate.

**Pre-PR gate behavior (§14.4):** the script checks for `fixtures/alpaca-simulators/` existence + checksum match; if missing or mismatched, auto-downloads from the pinned release URL + verifies SHA-256. Cached locally between runs.

**CI workflow:** same auto-download step at the start of every CI job. Caches the download via GitHub Actions cache keyed by the pinned SHA so subsequent runs reuse the cached binaries.

**Weekly upgrade-check workflow** (`.github/workflows/check-alpaca-simulators.yml`):

```yaml
name: Check Alpaca Simulators for updates
on:
  schedule:
    - cron: '0 8 * * 1'  # Every Monday 08:00 UTC
  workflow_dispatch:

jobs:
  check-upstream:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - name: Query latest release
        id: latest
        run: |
          latest=$(gh api repos/ASCOMInitiative/ASCOM.Alpaca.Simulators/releases/latest --jq .tag_name)
          echo "tag=$latest" >> $GITHUB_OUTPUT
      - name: Read pinned version
        id: pinned
        run: |
          pinned=$(grep 'Pinned release:' OpenAstroAra.Test/fixtures/SIMULATORS_VERSION.md | awk '{print $3}')
          echo "tag=$pinned" >> $GITHUB_OUTPUT
      - name: Open upgrade PR if new release
        if: steps.latest.outputs.tag != steps.pinned.outputs.tag
        run: |
          # Branch: chore/bump-alpaca-simulators-<new-tag>
          # Downloads new artifacts, updates SIMULATORS_VERSION.md, runs smoke tests,
          # opens PR with regression report
          ./scripts/bump-alpaca-simulators.sh "${{ steps.latest.outputs.tag }}"
```

The script `scripts/bump-alpaca-simulators.sh`:
1. Downloads the new release artifacts
2. Updates `SIMULATORS_VERSION.md` with new tag + SHA + checksums + ISO date
3. Runs the §14.1 server integration tests against the new simulators
4. Generates a regression report (which existing test names pass/fail; new event shapes detected)
5. Opens a PR with body: "Bump Alpaca simulators v0.4.0 → vX.Y.Z. Regression test results: N passed, N failed. Upstream changelog: <link>"
6. PR follows the standard CodeRabbit poll-and-fix loop (COMMIT-PR-RULES.md); user reviews + merges if green

If the upstream API has breaking changes, the PR's failing tests document exactly what changed — informs whether ARA needs adaptation code or whether the change is benign.

**Manual override:** `workflow_dispatch` lets the maintainer trigger the upgrade-check on demand (e.g., to grab a security fix without waiting for Monday).

**License clarity:** ASCOM.Alpaca.Simulators is MIT-licensed; we are NOT redistributing the binaries (they're CI-downloaded artifacts, not committed). Repository stays clean of upstream binaries.

**§14.1 test cases (added):**

- `simulator_version_pin_file_parses_and_matches_release` — CI step verifies SIMULATORS_VERSION.md is well-formed + the pinned tag exists upstream
- `simulator_checksums_match_downloaded_artifacts` — pre-PR gate + CI checksum verification
- `auto_pr_workflow_dry_run` — `workflow_dispatch` trigger with `--dry-run` exits 0 + emits a "would open PR for vX.Y.Z" log line

**§61 search registry entries:**

- `testing.simulator_pin` — keywords: `simulator version, alpaca simulator pin, fixture upgrade, simulator update workflow`
- `testing.bump_simulators` — keywords: `bump simulators, upgrade alpaca simulators, simulator pr workflow`

**Cross-references:**

- §14.1 — server integration tests use these simulators
- §14.3 — CI matrix downloads the pinned version at job start
- §14.4 — pre-PR gate auto-downloads if missing
- §14.5 — parent section (this is a subsection)
- COMMIT-PR-RULES.md — bump PRs follow the standard CodeRabbit poll-and-fix loop

### 14.6 Manual UI verification + screenshots

For any Flutter PR that touches user-visible UI, AI runs:

```bash
cd client/openastroara_client
flutter run -d linux &
# Exercise the changed flow + capture screenshots
# Tools: scrot, gnome-screenshot, or Flutter's built-in screenshot
```

Required screenshots:
- **Golden path** — the primary changed flow as the user would experience it
- **At least one edge case** — empty state, error state, narrow layout, or whatever's relevant

Screenshots attached to PR description in a `## Screenshots` section. AI captions each: what it shows + what was changed.

When to skip screenshots:
- PR touches only server code, only tests, only docs, only build config
- PR touches Flutter code but exclusively non-UI (e.g., model classes, generated API client, state-management plumbing)
- PR is a pure refactor with no visual change (AI must explicitly state "no visual change" in PR description)

When AI can't run the UI locally (e.g., CI environment, headless), AI states this explicitly in the PR: *"Manual UI verification could not run in this environment; flagging for human screenshot during review."* User is expected to do screenshots before merging.

### 14.7 Coverage policy

**v0.0.1: soft target.** No hard percentage. Requirements:

1. **All existing NINA tests still pass.** The inherited test suite stays green throughout the port; a passing baseline is non-negotiable.
2. **New or substantively changed code ships with tests.** "Substantively changed" means logic changed; pure renames, pure formatting, pure import-path adjustments don't require new tests.
3. **Integration tests for cross-cutting features.** §28 recovery, §28.7 FITS atomic-write, §29.1 storage configure, §39 calibration matching, §63 PHD2 lifecycle, §65 stretch palette — each gets at least one integration test exercising the happy path + one for each documented failure mode.
4. **UI flows hit by integration tests.** First-run, wizard completion, sequence start, frame capture-to-library, settings change-and-save.

**v0.1.0+ may add hard coverage thresholds** once the codebase stabilizes and the inherited NINA code has been pruned. Until then, the soft target keeps friction low during the port.

### 14.8 §61 search registry entries

- `testing.run_pre_pr_gate` — keywords: `run tests, pre-pr check, test before commit, gate, verify code, check before pr`

---

## 15. Build + verification gate (run after every phase)

Run `scripts/pre-pr-check.sh` (per §14.4) — same script the AI runs before every PR, exercises the full gate.

```bash
# All-in-one, exits non-zero on any failure:
scripts/pre-pr-check.sh

# Or, equivalently, run the pieces manually:

# Server
dotnet restore OpenAstroAra.sln
dotnet build OpenAstroAra.sln -c Debug --nologo
dotnet test  OpenAstroAra.Test/OpenAstroAra.Test.csproj -c Debug --nologo --logger "console;verbosity=minimal"

# Client (Phase 11 onward)
cd client/openastroara_client
flutter analyze
flutter test
cd -

# Cross-platform publish smoke (Phase 10 onward)
dotnet publish OpenAstroAra.Server -c Release -r linux-arm64 --self-contained -o /tmp/publish-test
```

Gate is green when:
1. `dotnet build` succeeds with zero errors. Warnings logged in `design/PORT_DECISIONS.md`.
2. `dotnet test` green for every previously-passing test (unit + integration per §14.1). Tests dependent on deleted WPF UI types from §4.2 are deleted (not skipped).
3. `flutter analyze` returns no errors (warnings OK, logged).
4. `flutter test` + `flutter test integration_test/` pass (per §14.2).
5. `dotnet publish -r linux-arm64` succeeds.
6. From Phase 10: published server responds to `/api/v1/server/info`.
7. Settings-registry check passes if any PR in the phase touched profile schema or settings UI (per §14.1 + COMMIT-PR-RULES.md).
8. For PRs touching user-visible Flutter UI: manual screenshots attached to the PR (per §14.6).
7. From Phase 11: `flutter run -d macos` reaches the server-discovery screen without exceptions.

If the gate fails and you cannot fix it within ~5 attempts, revert the last commit, write up the failure in `design/PORT_DECISIONS.md`, try a different approach. **Do not push a broken commit.**

---

## 16. Stuck-state policy

- **Compile error you can't immediately solve:** comment out the smallest region with `// PORT_BLOCKED: <reason>`, make the file compile with `throw new NotImplementedException("PORT_BLOCKED: <reason>")`, log to `design/PORT_TODO.md`. Move on.
- **API design ambiguity:** pick a REST-conventional shape (nouns for resources, HTTP status codes per semantics), document in `design/API_CONTRACT.md`. Do not paralyze.
- **Flutter package missing for a need (e.g., FITS parsing):** vendor a minimal implementation in `client/openastroara_client/lib/<feature>/` rather than depending on an unmaintained package.
- **NINA logic depends on a WPF type internally (Dispatcher, RoutedEventArgs, etc.):** replace with `SynchronizationContext` or plain async/await. Patch in place.
- **Tempted to ask the user:** pick the option this document or §0 rule 1 prescribes. Write the decision down. Continue.

---

## 17. Fork hygiene — naming, identifiers, MPL preservation

### 17.1 Names and identifiers

| Identifier | Value |
|---|---|
| Brand short | **ARA** (all caps — hero, About large text, marketing) |
| Brand long / display name | **OpenAstro Ara** (window title, README hero, About publisher line) |
| Publisher / org | **Open Astro** |
| Solution file | `OpenAstroAra.sln` |
| Project namespace prefix | `OpenAstroAra.*` |
| Server executable | `OpenAstroAra.Server` |
| Client app name | `OpenAstro Ara` |
| iOS/macOS bundle ID | `org.openastro.ara` |
| Android app ID | `org.openastro.ara` |
| GitHub repo (monorepo) | `github.com/open-astro/openastro-ara` |
| Server log path | Linux: `/var/log/openastroara/`; macOS dev: `~/Library/Logs/OpenAstroAra/`; Windows dev: `%LOCALAPPDATA%\OpenAstroAra\Logs\` |
| Server config path | `/etc/openastroara/` (Linux); equivalents on dev OSes |
| Client local storage | platform-default app data dir, scoped to bundle ID |
| Version | Server: `0.0.1-ara.1` (assembly: `0.0.1.0`); Client: `0.0.1+1` (Flutter format) |
| Phase tags | `phase-N-complete` |
| Release tag form | `v0.0.1-ara.N` |
| App icon / branding assets | placeholder during AI port; `TODO(branding): replace before public release` markers wherever icons are referenced |

### 17.2 MPL 2.0 preservation

**Sacred files (do not modify, do not delete):**
- `LICENSE.txt`, `COPYING`, `AUTHORS` (append-only), `3rd-party-licenses.txt` (update as deps change)

**Add at repo root:**
- `NOTICE.md`:
  > OpenAstro Ara (ARA) is a derivative work of [N.I.N.A. (Nighttime Imaging 'N' Astronomy)](https://github.com/isbeorn/nina) by Stefan Berg and contributors, used under the Mozilla Public License 2.0. Original copyrights are preserved in every source file. ARA is not affiliated with or endorsed by the N.I.N.A. project.
- `README.md` rewrite (first paragraph mentions the lineage; links to `design/PORT_PLAYBOOK.md` for contributors curious about the port's planning)
- `DEPLOY.md` (RPi server install)

**Add in `design/`** (working/design docs, not shipped):
- `design/API_CONTRACT.md` (API design log)
- (Plus the existing `design/PORT_PLAYBOOK.md`, `design/GAPS-ARA.md`, `design/COMMIT-PR-RULES.md`, and the four tracking files created in §1.)

### 17.3 Per-file headers

C# (modified or new files in ported projects):

```csharp
#region "copyright"
/*
    Copyright © 2016 - 2025 Stefan Berg <isbeorn@hotmail.com> and the N.I.N.A. contributors
    Copyright © 2026 - present Open Astro contributors

    This file is part of the open-source OpenAstro Ara project.

    This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
    If a copy of the MPL was not distributed with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/
#endregion "copyright"
```

Dart (client — new code, no NINA lineage):

```dart
// Copyright (c) 2026 Open Astro contributors.
// Licensed under the Mozilla Public License, v. 2.0.
// https://mozilla.org/MPL/2.0/
```

Rules:
- File the AI does not modify → header unchanged.
- Existing NINA file the AI modifies → append Open Astro line below Stefan's existing line.
- New file in a port-touched C# project → both copyright lines, MPL header.
- New file in `OpenAstroAra.Server` or `client/` → Open Astro line only.

---

## 18. Feature decisions (baked-in)

### 18.A — Updater: **DROP**
No in-app updater. README points users to GitHub Releases. Server announces its version in `/api/v1/server/info`; client displays "Server version X — see GitHub for updates."

### 18.B — Plugin system: **DEFERRED to v0.1.0**
Phase 0.5 deletes `NINA.Plugin` entirely. No plugin loader, no browser UI in the client, no SDK published. Plugin design happens post-v0.0.1 once architecture is stable.

### 18.C — Telemetry: **LOCAL LOGS ONLY, NO NETWORK**
Server: Serilog file sink, daily rotation, 14-day retention.
Client: in-app log viewer fed by server's WebSocket log stream. Optional "Save logs to file" button.
**No network calls** for analytics, crash reporting, telemetry. Strip any pre-existing Sentry/AppInsights references.
Crash handling: server logs unhandled exceptions and continues where safe. Client shows a "Disconnected — server may have crashed; check Pi" toast.

### 18.D — Community / branding links
- README, About, Help → `github.com/open-astro/openastro-ara` and `github.com/open-astro/openastro-ara/discussions`
- No Patreon/donate/Discord links until those channels exist; `TODO(community): add links when channels exist`

### 18.E — Localization: **ENGLISH ONLY**
Delete `crowdin.yml`. Delete non-English `Locale.*.resx`. No language picker. Hard-code `CultureInfo.InvariantCulture` where formatting was locale-influenced. Client is English-only; localization is a v0.2.0 problem.

### 18.F — Code signing: **SHIP UNSIGNED**
- Server: no signing (Linux daemons don't typically sign).
- Client: unsigned macOS/Windows desktop. README documents per-OS bypass:
  - macOS: right-click → Open, or `xattr -d com.apple.quarantine "/Applications/OpenAstro Ara.app"`
  - Windows: SmartScreen → More info → Run anyway
  - Linux: `chmod +x` the AppImage and run
- iOS/Android out of scope for v0.0.1 (per §18.G + §41 mobile-deferred-to-v0.1.0 decision)
- `TODO(signing): revisit when project has funding` in release workflow

### 18.G — Distribution formats: **DESKTOP ONLY in v0.0.1; mobile deferred to v0.1.0**
- **Server**: `.deb` for `arm64` via apt.openastro.net per §34 (primary); `.tar.gz` of self-contained publish (`linux-arm64`, `linux-x64`) as a fallback tarball for manual installs.
- **Client (v0.0.1 — desktop only)**:
  - macOS: `.dmg` via `create-dmg`, unsigned (per §18.F)
  - Windows: `.zip` of release build (later `.msix`), unsigned
  - Linux desktop: AppImage (Flatpak optional)
- **Client mobile (iOS / Android): deferred to v0.1.0.** Mobile distribution requires:
  - Apple Developer Program account ($99/yr) for any iOS distribution including TestFlight
  - Google Play Console account ($25 one-time) for Play Store distribution
  - Per-platform review processes (App Store ~2–7 days, Play Store ~hours-to-days)
  - Ongoing signing-cert + privacy-manifest + capabilities maintenance per OS update
  - Per-platform manual QA each release (Flutter web-target isn't a substitute — different code paths trigger different bugs)
  - This effort is real but disproportionate to v0.0.1's "validate the architecture" goals. Pushing it to v0.1.0 lets the desktop client + server stabilize first and lets the project decide whether to fund the accounts based on early-adopter signal.
- The §41 mobile companion *spec* still informs v0.0.1 API design (so v0.1.0 mobile doesn't require server changes) — but no mobile builds ship in v0.0.1.
- All built by GitHub Actions on tag push (desktop platforms only in v0.0.1).

### 18.H — Branding assets
Placeholders during port. Every icon/splash/logo reference carries `TODO(branding): replace with ARA asset before public release`. User supplies real assets.

### 18.J — Imaging scope: **DSO + COMETS ONLY**
ARA targets deep-sky objects and comets — the long-exposure (30 s – 900 s) capture workflow where Alpaca's image-grab API is the right primitive. **Planetary and lunar lucky-imaging are out of scope, permanently, not deferred.** The architectural reason: ASCOM Alpaca has no video API (the `IVideo` interface is deprecated and unsupported by Alpaca), so high-frame-rate (5–30 fps) capture isn't possible through the protocol ARA has committed to (§52). NINA has the same limitation; this isn't an ARA-specific gap. Users who want planetary capture use FireCapture, SharpCap, or AstroDMx with vendor-native drivers — different tool category. ARA's sky atlas (§36) still browses planets and moon for educational purposes, but capture is DSO + comets. This decision propagates: no `lunar.json` / `planetary.json` sequence templates (§38.7), no SER file format support, no ROI capture, no high-frame-rate workflows. Anything reading "v0.1.0 planetary support" in older revisions of this doc is wrong — corrected by this section.

### 18.I — Plate solving
- **ASTAP**: only solver. Cross-platform; users download per OS from astap.nl. Server config exposes ASTAP binary path + star-database path; per-OS defaults attempted on first run:
  - Linux: `which astap` → `/usr/bin/astap` or `/opt/astap/astap`
  - macOS: `/Applications/ASTAP.app/Contents/MacOS/astap`
  - Windows: `%PROGRAMFILES%\astap\astap.exe`
- **Astrometry.net**: **deferred to v0.1.0.** ASTAP covers 99% of astrophotography solving needs and is well-maintained, ARM64-native, cross-platform. Adding astrometry.net means another binary management workflow, another index-file download manager (4100 / 4200 / 5000-series catalogs, ~1-30 GB each), and another solver-tuning surface — not worth the complexity for v0.0.1. Phase 8 strips astrometry.net call sites from inherited NINA code; v0.1.0 may add it back if there's user demand.
- **PlateSolve2**: deleted entirely (Windows-only legacy).

---

## 19. Auto-approve safety rails

### 19.1 Git safety

- **Branch allowlist:** AI may commit/push to `port/ara` (integration branch, where all phase PRs land) and to per-sub-PR feature branches matching the pattern `port/ara/phase-N[/<letter>]` (e.g., `port/ara/phase-0.5/a`, `port/ara/phase-12/h`). All other branches are off-limits without explicit user instruction. The main `port/ara` branch is integration-only — direct commits to it happen only after the user merges a sub-PR (AI pulls the merge commit; never authors directly).
- **AI never merges, ever.** PR merges are a user action, no exceptions. AI opens PRs, drives the CodeRabbit poll-and-fix loop per COMMIT-PR-RULES.md, posts "Ready for human review @<user>" at quiescence, and waits.
- No `git push --force` or `--force-with-lease`. Plain `git push` only.
- No `--no-verify` on commits.
- No `git reset --hard` without first creating `backup-<timestamp>` tag.
- No deleting branches, remotes, or stashes on the remote.
- No history rewriting (`filter-branch`, `filter-repo`, interactive rebase).
- Tags: `phase-N-complete` (or `phase-N-<letter>-complete` for sub-PRs) at boundaries, `backup-<timestamp>` before destructive ops. Push via `git push --tags`.

### 19.2 Filesystem safety

- No `rm -rf` outside `bin/`, `obj/`, `client/openastroara_client/build/`, and the explicit deletion lists in §4.2.
- No modifications outside repo root.
- No installing global tools (`dotnet tool install -g`, `brew install`, `apt install`, `npm install -g`, `flutter pub global`). Local-to-repo only.
- No modifying system state (PATH, shell config, dotfiles outside repo).
- No writing to `~/`, `/etc/`, `/usr/`, `/var/`, `~/.ssh/`, `~/.aws/`, `~/.config/git/`, `~/.gitconfig`.

### 19.3 Network safety

- No authenticated network calls except `gh` (GitHub CLI) and `git push origin`.
- No POSTing telemetry, analytics, crash reports.
- Allowed: `dotnet restore` (NuGet), `flutter pub get` (pub.dev), `gh` commands, package metadata.

### 19.4 Secrets safety

- Do not commit `.pfx`, `.key`, `.pem`, `.env`, `appsettings.Secrets.json`, `secrets.dart`, or files containing `password`/`secret`/`token`. Add patterns to `.gitignore` if found. (v0.0.1 has no server auth per §67, but the rule still applies — protects against future v0.1.0 remote-access tokens, plus any third-party API keys that may show up in dependencies or example configs.)
- Do not echo or log API keys, tokens, auth headers that may exist in third-party integrations or v0.1.0+ remote-access mode.

### 19.5 Scope safety

- Do not edit `design/PORT_PLAYBOOK.md`, `design/PORT_DECISIONS.md`, `design/PORT_TODO.md`, `design/PORT_PROGRESS.md`, `design/API_CONTRACT.md` except to append entries per documented rules.
- Do not edit `.git/`, `.github/workflows/` (until Phase 14), `.claude/`.

---

## 20. Quota-resume protocol

### 20.1 `design/PORT_PROGRESS.md` format

```markdown
# OpenAstro Ara — Port Progress

## Current
- Phase: 7 — Sequence endpoints
- Started: 2026-XX-XX
- Currently working on: <file or endpoint>

## Completed
- ✅ Phase 0.5 — Fork hygiene + project demolition (tag: phase-0.5-complete)
- ✅ Phase 1 — Bump non-UI projects to .NET 10 (tag: phase-1-complete)
- ... (one line per phase)

## Next
- After current task: <next file or endpoint>
- After current phase: Phase 8 — Image endpoints
```

Updated on every commit. "Currently working on" must point at a specific file or endpoint, never "various refactoring."

### 20.2 Resume procedure

On session start (fresh or resumed):

1. `git status` and `git log --oneline -20`.
2. `cat PORT_PROGRESS.md`.
3. `cat PORT_TODO.md`.

Then resume the current task. If `git diff HEAD` shows uncommitted changes, finish them and commit. Otherwise pick up at the next file/endpoint per `design/PORT_PROGRESS.md`.

---

## 21. Localization

ARA is English-only in v0.0.1. The English `Locale.resx` is preserved for remaining `Locale.Instance[...]` references in non-UI code. All other language files were deleted in §4.3.

When porting NINA logic into ASP.NET Core endpoints, replace `Loc.Instance[...]` in API responses with hard-coded English strings — the API does not localize. Client-side localization is a v0.2.0 feature.

---

## 22. Final pass (Phase 15)

1. Sweep `design/PORT_TODO.md`: every `// TODO(port)` and `// PORT_BLOCKED` resolved or explicitly accepted in `design/PORT_DECISIONS.md`.
2. Run the gate one more time including `-c Release` and `flutter build` for every desktop platform (macOS / Windows / Linux per §18.G).
3. Smoke test end-to-end:
   - Bring up `OpenAstroAra.Server` on a Linux ARM64 host (Pi or Docker).
   - Launch the Mac client. Discover via mDNS. Connect (no token; see §67).
   - Verify equipment dashboard shows AlpacaBridge-exposed simulator devices.
   - Run a 2-target sequence with simulator camera + simulator mount; openastro-phd2 connects and dithers.
   - Disconnect the client mid-sequence; wait 5 minutes; reconnect; verify session continued and frames were captured.
   - Open every section of the app. Note regressions.
4. Update `CHANGELOG.md` (per §33.7) — rename `## [Unreleased]` → `## [0.0.1-ara.1] — <date>` with sections:
   - Headless server + cross-platform desktop client architecture
   - Alpaca-only equipment (per §52, §68)
   - Plugin support deferred to v0.1.0
   - Mobile (iOS / Android) deferred to v0.1.0 per §18.G + §41
   - Behavioral parity goals vs. upstream NINA where applicable
   - Lineage attribution
   - Known issues, install instructions
   - Create fresh `## [Unreleased]` placeholder for v0.0.2 work
5. Bump `CommonAssemblyInfo.cs` to `0.0.1.0`; informational `0.0.1-ara.1`. Bump `pubspec.yaml` to `0.0.1+1`.
6. **Open final PR from `port/ara` to `master`** with `design/PORT_DECISIONS.md` contents as description. Per COMMIT-PR-RULES.md decision (2026-05-23): no `develop` branch; final PR goes directly `port/ara → master`. This PR is a fast-forward over the already-reviewed per-phase sub-PRs that landed on `port/ara` throughout the port — CodeRabbit's review burden is minimal because each constituent commit was already reviewed at its sub-PR. **Do not merge** — user reviews + merges.

---

## 23. Quick reference — bash one-liners

```bash
# Find leftovers from deleted things
grep -rln "System\.Windows\|UseWPF\|Microsoft\.Web\.WebView2\|ASCOM\.Com\|nikoncswrapper" --include="*.cs" --include="*.csproj" .

# Bulk-rename NINA → OpenAstroAra inside a directory.
# IMPORTANT: this only renames namespace/type references in .cs files.
# Skips license headers + NOTICE files + markdown — those contain user-visible
# NINA references (attributions, lineage notes) that should be edited deliberately,
# not by find-and-replace. Always dry-run first:
DRY_RUN_PATCH=$(find $DIR \
  \( -name "*.cs" -o -name "*.csproj" \) \
  -not -path '*/LICENSE*' \
  -not -path '*/NOTICE*' \
  -not -path '*/AUTHORS*' \
  -exec grep -l 'NINA\.' {} \;)
echo "Files that would be modified:"
echo "$DRY_RUN_PATCH"
echo "---"
echo "Sample of changes (first 10 lines):"
echo "$DRY_RUN_PATCH" | head -5 | xargs -I {} sh -c 'echo "=== {} ===" && grep -n "NINA\." {} | head -2'
echo "---"
read -p "Apply renames? [y/N] " confirm
if [ "$confirm" = "y" ]; then
  echo "$DRY_RUN_PATCH" | xargs -I {} sed -i.bak 's/NINA\./OpenAstroAra\./g' {}
  echo "$DRY_RUN_PATCH" | xargs -I {} mv {}.bak {}.bak.delete
  find $DIR -name "*.bak.delete" -delete
fi

# License headers + user-visible strings (error messages, log strings, resource files)
# get edited DELIBERATELY — see §17.2 fork hygiene rules + §4.3 Stefan branding strip.
# Phase 0.5 sub-PR 0.5f covers the deliberate string sweep (per COMMIT-PR-RULES.md).

# Verify server builds for ARM64
dotnet publish OpenAstroAra.Server -c Release -r linux-arm64 --self-contained

# Tail server logs from a remote Pi
ssh pi 'sudo journalctl -u openastroara-server -f'

# Regenerate Dart client from OpenAPI spec
cd client/openastroara_client && dart run build_runner build --delete-conflicting-outputs

# Run client against a local-running server during dev
cd client/openastroara_client && OPENASTROARA_DEFAULT_HOST=localhost:5400 flutter run -d macos
```

---

## 24. What "done" looks like

- Server builds on Linux ARM64, x64, Windows, macOS via `dotnet build`. Linux ARM64 publish is the canonical artifact.
- Client builds on macOS (Apple Silicon), iOS, Android, Windows, Linux desktop via `flutter build`. Every platform produces a working binary.
- `OpenAstroAra.Server` runs as a systemd daemon on a Pi, discovered via mDNS, accepts authenticated client connections.
- `OpenAstro Ara` (Flutter client) on a Mac discovers the server via mDNS, connects (no auth per §67), displays equipment status, runs a sequence to completion, displays preview JPEGs as frames complete, supports clean disconnect/reconnect mid-sequence.
- Smoke test in §22 (step 3) passes end-to-end on a Mac + RPi setup with simulator equipment and openastro-phd2.
- No bundled native vendor SDKs, no WPF UI code, no plugin loader, no upstream-NINA branding (except attributions in NOTICE.md, AUTHORS, About, README per §17).
- All MPL license headers preserved per §17.3.
- `design/PORT_DECISIONS.md`, `design/PORT_TODO.md`, `design/PORT_PROGRESS.md`, `design/API_CONTRACT.md` reflect the full history.
- PR description summarizes the work and links the four tracking files.

Begin Phase 0.5.

---

## 25. Visual design reference — cloning NINA's UX

The Flutter client deliberately mirrors NINA's UX so existing astrophotographers feel at home. This section documents what to clone, what to substitute, and the IP boundaries.

### 25.1 IP boundary

**Free to clone (not copyrightable):**
- Layout: top bar + left panel + center tabs + right panel + bottom status bar
- UX flows: how a sequence is built, how plate solving is invoked, how the framing assistant works
- Control labels and terminology: "Exposure", "Gain", "Offset", "Filter", "Cooler Target", "Framing Assistant", "Sky Atlas", "Plate Solve", "Dither", etc.
- Color palette decisions (dark theme with status accents)
- Information density and arrangement
- Workflow patterns (equipment chooser → connect → manual control → sequence build → run)

**NOT free to copy — placeholder only:**
- Bitmap icons (`Logo_Nina.ico`, the camera/mount/focuser device icons, any custom rendered glyphs)
- Splash screen
- Specific photographs or imagery (any sample images that ship with NINA — do not include)
- The NINA wordmark or any stylized "N.I.N.A." rendering

For v0.0.1: every icon is a Flutter Material icon (`Icons.camera_alt`, `Icons.adjust`, etc.) or a simple SVG primitive drawn from scratch. Mark each with `// TODO(branding): replace with custom ARA icon before public release`.

### 25.2 Color tokens (Flutter theme)

```dart
class AraColors {
  // Backgrounds
  static const bgPrimary    = Color(0xFF1A1A1A);  // main app background
  static const bgPanel      = Color(0xFF262626);  // side panels
  static const bgPanelAlt   = Color(0xFF2E2E2E);  // alternate row / hover
  static const bgInput      = Color(0xFF333333);  // text fields, dropdowns
  static const border       = Color(0xFF404040);  // panel borders, dividers

  // Text
  static const textPrimary   = Color(0xFFE0E0E0);
  static const textSecondary = Color(0xFFA0A0A0);
  static const textDisabled  = Color(0xFF606060);

  // Accents (status indicators)
  static const accentConnected    = Color(0xFF4CAF50);  // green
  static const accentBusy         = Color(0xFFFFB300);  // amber
  static const accentError        = Color(0xFFE53935);  // red
  static const accentInfo         = Color(0xFF42A5F5);  // blue
  static const accentDisconnected = Color(0xFF606060);  // gray

  // Highlight
  static const selectionBg     = Color(0xFF1976D2);
  static const selectionFg     = Color(0xFFFFFFFF);
  static const buttonPrimary   = Color(0xFF1565C0);
  static const buttonSecondary = Color(0xFF424242);
}
```

Use Material 3 `ThemeData` with these tokens. Material's default dark theme is too light/colorful for an observatory app used in the dark.

### 25.3 Top equipment bar

A horizontal `Row` of equipment "chips" — one per device type. Each chip:
- Icon (Material icon for v0.0.1)
- Device-type label below the icon (small caps: "CAM", "FW", "FOC", "MOUNT", "ROT", "GUIDE", "DOME", "SW", "FLAT", "WX", "SAFE")
- Colored dot indicator (top-right of icon): gray (disconnected), green (connected + idle), amber (busy), red (error)
- Tap: opens chooser bottom sheet listing discovered Alpaca devices
- Long-press / right-click: disconnect, or open device-specific properties panel

Order (left-to-right, mirroring NINA's bar):
1. Camera
2. Filter Wheel
3. Focuser
4. Mount (telescope)
5. Rotator
6. Guider (PHD2)
7. Flat Panel
8. Switch
9. Weather
10. Safety Monitor
11. Dome

### 25.4 Left panel — profile + manual control

Two vertical sections:

**Profile selector** (top, fixed-height):
- Dropdown listing all profiles from `~/.config/openastroara/profiles/`
- "Active profile: <name>" label
- Gear icon → opens profile editor in a modal

**Manual control accordion** (below, scrollable):
- One expandable card per connected device
- Card contents = the device's relevant manual controls (camera: exposure/gain/offset/cooler; mount: slew controls + park/unpark; focuser: position + step; filter wheel: filter selector; rotator: angle + reverse; guider: connect/start/stop)
- Cards remain in the layout when disconnected, but content is disabled and shows "Not connected"

### 25.5 Center tabs — workspace

Five tabs along the top of the center area:

1. **Imaging** — the live capture workspace
   - Main area: the most-recent frame's preview JPEG, rendered with `Image.network(previewUrl)`. Zoom + pan via `InteractiveViewer`.
   - Overlay (toggle-able): plate-solve results (RA/Dec, rotation, pixel scale)
   - Right-side controls: exposure/gain/offset, "Take One" button, "Live View" toggle, sequence shortcut to start
   - Bottom: thumbnail strip of recent frames in the current session
2. **Framing Assistant**
   - Top: target search (DSO catalog query — Messier, NGC, IC, by name or coords)
   - Center: sky chart preview (basic for v0.0.1 — just a labeled scatter of stars from a small star catalog)
   - Right: framing parameters (FOV, rotation, mosaic panel grid)
   - "Set as Target" button → adds to sequence
3. **Sequencer**
   - Tree view of: Areas → Targets → Instructions (NINA's hierarchy)
   - Drag-and-drop reordering within a level
   - Per-instruction editor pane on the right when selected
   - Top toolbar: New / Load / Save / Validate / Run / Pause / Abort
   - Below toolbar: progress bar + currently-executing instruction label
4. **Sky Atlas** — **embedded Aladin Lite (CDS) with bundled catalogs + Tonight's Sky planetarium view**. See §36 for full spec. Two sub-modes:
   - **Catalog View** — Aladin Lite in standard equatorial mode, free pan/zoom, full HiPS survey browsing (21 surveys, see §36), universal search (Simbad online + bundled name index offline)
   - **Tonight's Sky** — same Aladin instance, zenith-centered, horizon-aware, time-controlled, solar system (Sun/Moon/planets) + comets overlaid, planetarium-style. Replaces NINA's external-planetarium integration (Cartes du Ciel, Stellarium) — ARA is the planetarium.
   - Tap an object → details panel + "Set as Target in Framing Assistant" CTA
   - Aladin logo + CDS attribution preserved at bottom-right per Aladin's GPLv3 license terms (see §17 + §36)
5. **Options**
   - Tree-based settings: Equipment (per device type), Imaging, Plate Solving, Astrometry, File Saving, Telescope, Astronomy, Sequence, Application
   - Right pane: settings for selected node
   - "Save Profile" / "Save Profile As..." buttons in the toolbar

### 25.6 Right panel — analysis

Three stacked sections, each collapsible:

1. **Histogram + image statistics** — built with `fl_chart` or a custom CustomPaint. Shows the last frame's RGB or luminance histogram, plus min/median/max/mean/std-dev/stars-detected.
2. **Plate solve panel** — last plate-solve result: solved RA/Dec, rotation, pixel scale, FOV; "Solve Last Frame" button.
3. **Log tail** — last 50 log lines from the server, color-coded by level (info/warning/error). "Pause", "Clear", "Open Full Log" buttons.

### 25.7 Bottom status bar

`Row` along the bottom of the main window:
- Left: local time + sidereal time (computed from server's reported lat/long)
- Center: current operation ("Capturing target M42 — frame 4/20, 180s") + progress bar
- Right: server connection state ("Connected: pi-observatory.local — v0.0.1-ara.1") + "Forget this server" button (clears the saved server entry; doesn't affect Pi-side state)

### 25.8 Mobile differences

iPad: same desktop layout, slightly more compact, side panels can be drawer-style if needed.

iPhone/Android: bottom-tab navigation as in §12.3. Per-tab the layout collapses to the relevant content only (no side panels). Sequence editing is read-only or limited on phone in v0.0.1 — full editing is a desktop-class workflow; phone is for monitoring.

### 25.9 Reference materials

Before writing client code, the AI should:
1. Browse NINA's documentation site for layout reference: `https://nighttime-imaging.eu/docs/`
2. Look at NINA's existing screenshots in `/Users/joey/Documents/GitHub/nina/README.md` and any docs subdirectory (these will be deleted in §4.3 — capture them via `git log master --name-only` or similar before deletion if needed)
3. Capture the layout in pseudo-mockups in `client/openastroara_client/docs/mockups/` as Markdown ASCII art if useful for self-reference

The implementation does not need to be pixel-perfect to NINA — it needs to be *recognizable* to a NINA user as the same workflow. Familiar enough that a user who knows NINA can navigate ARA on day one without reading docs.

### 25.10 Decisions that diverge from NINA

These are deliberate departures, documented up front so the AI doesn't try to clone them:

- **AvalonDock panel rearrangement** — not supported in v0.0.1. Static layout only.
- **MGEN guider tab** — gone (NINA.MGEN deleted). Guider section is PHD2-only.
- **Plugin browser tab** — gone (plugin support deferred to v0.1.0).
- **Built-in updater UI** — gone (per §18.A).
- **Patreon / donate banner** — gone (per §18.D).
- **Language picker** — gone (English-only, §18.E).
- **Web browser panel** (NINA's WebView2 panel for catalog access) — replaced by "Open in external browser" buttons.
- **Settings tree depth** — flatten where NINA goes deep for things that no longer exist (vendor-specific camera settings, MGEN settings, plugin settings, etc.).

---

## 26. Image processing on Linux — OpenCvSharp4 migration

This is the single biggest technical risk in the port and must be handled in Phase 5 (Image).

### 26.1 The problem

NINA's image pipeline uses `System.Drawing.Common` for `Bitmap`, `Graphics`, `ImageConverter`, etc. In .NET 6 and later, **`System.Drawing.Common` is Windows-only by default**. On Linux you get `PlatformNotSupportedException` at first use. The historical workaround `libgdiplus` is deprecated and crashes intermittently on ARM64.

NINA also uses `System.Windows.Media.Imaging` (`BitmapSource`, `WriteableBitmap`, `RenderTargetBitmap`, `FormatConvertedBitmap`, `CroppedBitmap`) — these are WPF types, not available on Linux at all.

Neither approach works for an ARM64 Linux daemon.

### 26.2 The solution — OpenCvSharp4

PI.N.S. proved this path: replace `System.Drawing` + `System.Windows.Media.Imaging` calls with **OpenCvSharp4** (`Mat`, `Cv2.ImEncode`, etc.). OpenCV is the standard image-processing library in astronomy (used by INDI, KStars, PixInsight scripts), ARM64 binaries ship in the NuGet package, license is BSD-3 (clean for MPL).

```xml
<PackageReference Include="OpenCvSharp4" Version="4.11.*" />
<PackageReference Include="OpenCvSharp4.runtime.linux-arm64" Version="4.11.*" Condition="'$(RuntimeIdentifier)' == 'linux-arm64'" />
<PackageReference Include="OpenCvSharp4.runtime.linux-x64"   Version="4.11.*" Condition="'$(RuntimeIdentifier)' == 'linux-x64'" />
<PackageReference Include="OpenCvSharp4.runtime.win"         Version="4.11.*" Condition="$([MSBuild]::IsOSPlatform('Windows'))" />
<PackageReference Include="OpenCvSharp4.runtime.osx"         Version="4.11.*" Condition="$([MSBuild]::IsOSPlatform('OSX'))" />
```

### 26.3 Translation cheatsheet

| WPF / System.Drawing | OpenCvSharp4 |
|---|---|
| `new Bitmap(width, height, PixelFormat.Format24bppRgb)` | `new Mat(height, width, MatType.CV_8UC3)` |
| `bitmap.Save(path, ImageFormat.Jpeg)` | `Cv2.ImWrite(path, mat)` (extension drives format) |
| `bitmap.LockBits(...)` then `Marshal.Copy` | `mat.GetArray<byte>()` / `mat.SetArray(byte[])` — direct buffer access |
| `WriteableBitmap.WritePixels(...)` | `mat.SetArray(...)` |
| `BitmapSource` parameter type | `Mat` (the OpenCV canonical type) |
| `Convert16BppTo8Bpp` (NINA helper) | `mat.ConvertTo(out8bit, MatType.CV_8U, 1.0/256.0)` |
| `FormatConvertedBitmap(source, PixelFormats.Gray8, ...)` | `Cv2.CvtColor(src, dst, ColorConversionCodes.BGR2GRAY)` (or appropriate code) |
| `CroppedBitmap(source, rect)` | `new Mat(source, new Rect(x, y, w, h))` — ROI view |
| `RenderTargetBitmap` (rendering a UI element to a bitmap) | **N/A** — no UI in server. Delete the call sites. |
| JPEG preview generation | `Cv2.ImEncode(".jpg", mat, out byte[] buf, new[] { (int)ImwriteFlags.JpegQuality, 80 })` |
| Debayering | `Cv2.CvtColor(rawMat, rgbMat, ColorConversionCodes.BayerRG2RGB)` |

### 26.4 What gets deleted vs. translated

| In NINA | Action |
|---|---|
| Anything using `RenderTargetBitmap` to capture a UI element | **Delete** — no UI |
| `ImageUtility.ConvertBitmap` (BitmapSource ↔ Bitmap conversions) | **Rewrite** — pipeline operates on `Mat` end-to-end |
| FITS reader (custom format, doesn't depend on System.Drawing) | **Keep as-is** — outputs `ushort[]` or `byte[]` buffers, fed into `Mat` |
| Stretch / histogram code (operates on raw buffers) | **Keep as-is** — just feeds Mat instead of Bitmap |
| Star detection (`HocusFocus`, `HFR`, etc.) | **Audit** — uses Accord.Math + raw arrays; should port cleanly. Some Accord types depend on System.Drawing; replace with OpenCV equivalents (`Cv2.HoughCircles`, `Cv2.FindContours`, etc.) where present. |

### 26.5 Phase 5 (Image) task order

1. Add OpenCvSharp4 references to `OpenAstroAra.Image.csproj`.
2. Run `grep -rn "System\.Drawing\|System\.Windows\.Media\.Imaging" --include="*.cs" OpenAstroAra.Image/` to enumerate all call sites.
3. Per call site, apply the translation cheatsheet. Commit one file at a time.
4. Delete `RenderTargetBitmap` call sites entirely (UI-only, server doesn't render visuals).
5. Verify image pipeline tests still pass on Linux: `dotnet test -c Debug --runtime linux-arm64` (cross-compile build, run in Docker emulation if no Pi available).
6. Server smoke test (Phase 10): capture from Alpaca simulator → JPEG preview generated and served → file size and dimensions sane.

### 26.6 What we don't borrow from PI.N.S.

PI.N.S.'s `System.Windows.Compat` library mocks `System.Windows.*` namespaces so NINA's WPF UI code still compiles on Linux. **We don't need that** — we delete the WPF UI entirely in §4.2. Trying to keep the compat shim would mean carrying a parallel WPF-look-alike API surface that nothing in ARA actually uses.

The OpenCvSharp4 *technique* we borrow. The compat layer we don't.

### 26.7 Lineage attribution update

`NOTICE.md` adds a paragraph crediting PI.N.S. for proving the Linux feasibility:

> The Linux image-processing approach (OpenCvSharp4 in place of System.Drawing.Common) was pioneered by [PI.N.S. (PI 'N' Stars)](https://github.com/nitr57/pins), a separate Linux fork of N.I.N.A. by nitr57. ARA uses the same library choice but does not depend on or fork the PI.N.S. codebase; we start fresh from NINA 3.2 master.

---

## 27. Connection policy — single-client at a time

ARA serves **one connected client at a time** to eliminate command-conflict edge cases entirely. New connection attempts go through a hand-off dance mediated by the currently-connected client.

### 27.1 Flow

```
new client → POST /api/v1/server/connect  (no auth per §67)
   │
   ├─ no current client      → 200 + session ID, new client takes over
   │
   ├─ current client online  → server sends current client a WebSocket event:
   │                              { "type": "connection.request",
   │                                "from": "ipad.local",
   │                                "request_id": "..." }
   │                          → current client shows modal:
   │                              "ipad.local wants to connect.
   │                               [Allow]  [Keep me connected]"
   │                          → current replies on WS:
   │                              { "type": "connection.response",
   │                                "request_id": "...",
   │                                "action": "allow" | "reject" }
   │                          → server replies to new client:
   │                              200 (allow — current gets a "disconnected" toast)
   │                              409 (reject — "Server in use by mac.local")
   │
   └─ current client unresponsive (no WS pong in 60s)
                              → server marks current as dead, accepts new client immediately
```

### 27.2 Timeouts

| Condition | Timeout | Action |
|---|---|---|
| Current client doesn't respond to `connection.request` | 30 s | 409 to new client: "Current client unresponsive, try again in 60s" |
| WS pong missing from current client | 60 s | Mark current as dead; next `connect` call succeeds |
| WS pong missing during normal operation | 60 s | Connection dropped, session ends, sequence keeps running on server |

### 27.3 Endpoints

- `POST /api/v1/server/connect` (no auth per §67; body identifies the connecting client by hostname for the takeover UX) → 200 + session, or 409 (in-use), or 503 (server starting / sequence-only mode)
- `POST /api/v1/server/disconnect` → graceful disconnect, releases the slot
- `GET /api/v1/server/session` → current session info (controller hostname, since when, idle time)

### 27.4 Out of scope for v0.0.1

- Multi-client read-only spectator mode (a "watch only" connection) — could be v0.1.0
- Persistent admin override / force-disconnect mechanism — could be v0.1.0 (paired with §67.4 remote-access mode where auth re-enters)

---

## 28. Sequence durability & crash recovery

Power blips, server crashes, kernel hiccups, Wi-Fi resets — none should cost a night of imaging. ARA checkpoints sequence state to SQLite and runs a structured recovery on restart.

### 28.1 Checkpointing

- Persistence engine: SQLite at `${config}/openastroara.db`.
- `sessions` table: id, profile_id, sequence_json, started_at, ended_at, recovery_needed (bool), last_completed_instruction_id, current_target_id, frame_count
- `frames` table: id, session_id, target_id, instruction_id, fits_path, captured_at, filter, exposure_seconds, etc.
- **Write points:**
  - Session start → row inserted with `recovery_needed = true`
  - After every completed sequencer instruction → `last_completed_instruction_id` updated
  - After every FITS save → row inserted in `frames` (this is the canonical "frame succeeded" signal)
  - Graceful shutdown → `ended_at` set, `recovery_needed = false`
- **In-flight frame at crash time is lost** (that exposure was interrupted). Recovery resumes from the instruction *after* the last completed one.

### 28.2 Recovery routine (runs on server startup if `recovery_needed = true`)

1. **Reconnect equipment** — re-enumerate Alpaca devices from the saved session's profile. If any fail to connect: retry 3× with 5s spacing; log per-device status; continue without missing devices (graceful degradation).
2. **Mount home** — issue `FindHome` (if mount supports it) or slew to the configured park position. Provides a known reference; resolves meridian-flip ambiguity.
3. **Cooler setup** (if camera has cooler):
   - Set target temp from profile (e.g., −10°C)
   - Ramp at configured rate (default **1°C/min** — too fast risks condensation/thermal stress)
   - Wait for stabilization: within **±0.5°C for 60 s continuous**
   - Max wait timeout: **10 minutes**. If not stabilized (warm night, cooler failing), queue warning notification and proceed anyway.
   - Skip entirely if camera reports no cooler.
4. **Altitude check on active target:**
   - Hard floor: target's `MinAltitude` setting from profile, fallback **5°** if unset
   - If below hard floor: skip target, advance to next in sequence, repeat altitude check
   - If at or above hard floor but **< 30°**: queue soft-warning notification `{ "type": "altitude.warning", "payload": { "target": "M42", "altitude": 18.4, "actions": ["continue", "skip"] } }`; default action if no user response = **continue** (unattended operation keeps running)
   - If all remaining targets below hard floor: log "session ended early — all targets below horizon", park mount, mark session `ended_at`, stop
5. **Slew to target** at saved RA/Dec
6. **Plate solve** with rotation:
   - Position tolerance: **60 arcsec** (NINA default)
   - Rotation tolerance: **1°**
   - Retries: **3** with re-slew + re-solve between attempts
   - On failure: log + queue notification, abort recovery (await user)
7. **Filter selection** (if filter wheel connected):
   - Look ahead in the sequence to the next instruction that uses a filter
   - Switch wheel to that filter
   - If no filter wheel: skip
8. **Autofocus** (if focuser connected):
   - Run autofocus on the selected filter (correct wavelength → correct focus, since temperature may have drifted during the outage)
   - Use NINA's existing autofocus routine
   - If no focuser: skip with log warning ("focus may have drifted during outage")
9. **Resume guiding** (if guider configured + connected):
   - Start PHD2, wait for calibration / settle
   - If no guider: skip
10. **Continue sequence** from the instruction *after* `last_completed_instruction_id`

### 28.3 Total timeout

If recovery hasn't successfully reached step 10 within **15 minutes**, abort:
- Log the failure point
- Park mount
- Set session `ended_at`, clear `recovery_needed`
- Queue notification: *"Recovery failed at step X — please reconnect and review"*

User can reconnect and manually resume or skip.

### 28.4 Soft 30° altitude warning during normal operation

The same warning pattern fires during normal sequence execution, not just recovery. NINA's per-target `MinAltitude` is the hard floor (sequence will not image below it). The 30° soft warning is purely advisory:

- Before starting a target, check altitude
- If < 30° (but ≥ hard floor): queue `altitude.warning` notification with [Continue]/[Skip] actions
- Default (no response): continue imaging — the user put it in the sequence, server respects that

### 28.5 Out of scope for v0.0.1

- Resume mid-instruction (e.g., picking up frame 4 of 10 when the crash happened at frame 3 of 10). For v0.0.1 we resume at instruction granularity; the in-progress instruction restarts from its first frame.
- Multi-mount / multi-camera sessions.
- Resume across multiple nights with target re-acquisition based on actual time skew (assumes user resumes the same night).
- `durability_mode` config knob (paranoid / balanced / fast) — single fixed safe mode in v0.0.1; v0.1.0 if users ask for paranoid (synchronous=FULL) or fast (no fsync, test-only).
- Active power-loss detection (UPS GPIO signal triggers proactive checkpoint + park) — v0.1.0.
- Background scrubbing of FITS files against checksums — v0.1.0.
- Multi-disk RAID-style redundancy — out of scope entirely; §44 backup stream covers the "second copy" use case.

### 28.6 Data durability — SQLite settings

§28.1's checkpoint table assumes durable writes. ARA applies these PRAGMAs on every server startup, before any session begins:

```sql
PRAGMA journal_mode = WAL;            -- Write-Ahead Logging for concurrent reads + faster writes
PRAGMA synchronous = NORMAL;          -- fsync at critical moments; zero corruption with WAL; modern standard
PRAGMA temp_store = MEMORY;           -- temp tables in RAM
PRAGMA mmap_size = 268435456;         -- 256 MB memory-mapped I/O for perf
PRAGMA wal_autocheckpoint = 1000;     -- default 1000 pages (~4 MB) before automatic checkpoint
PRAGMA busy_timeout = 5000;           -- 5 s timeout on contended writes (instead of immediate SQLITE_BUSY)
PRAGMA foreign_keys = ON;             -- enforce FK constraints
```

**Why NORMAL not FULL:** WAL + NORMAL is the modern standard (used by Firefox, Chrome, iOS, Android, etc.). Zero corruption guarantee on power loss — the WAL ensures the database file always represents either the pre-transaction state or the committed state, never a torn write. The trade-off: the **last few seconds of committed transactions** may be lost on power loss (anything not yet WAL-checkpointed back into the main DB file). For ARA this is acceptable because:

1. Frames are checkpointed individually via §28.1, so losing the last few seconds means losing at most the last 1–2 frame metadata writes
2. The corresponding FITS files persist through §28.7's atomic-write pattern and get re-discovered by the §28.8 startup scan
3. The net "data lost" is therefore typically zero — only the DB row gap, which auto-heals

ARA explicitly manually invokes `PRAGMA wal_checkpoint(PASSIVE);` at session start, session end, and every 10 minutes during active capture — bounds the WAL replay cost on next startup.

### 28.7 Data durability — FITS atomic-write pipeline

After every captured exposure, the server writes the FITS file using an **atomic-rename pattern** so that no partially-written file ever appears under its real name:

1. Write FITS bytes to `<frame>.fits.tmp`
2. `fsync()` the file descriptor — force bytes to platter/flash
3. Atomic rename `<frame>.fits.tmp` → `<frame>.fits`
4. `fsync()` the parent directory — make the rename itself durable
5. Insert row in SQLite `frames` table (§28.1) — its own WAL fsync via `synchronous=NORMAL`

Steps 1–4 take ~5–200 ms on USB SSD (typically 10–50 ms on quality SSDs; longer on USB sticks — which §29 already warns against). Since typical exposures are 60–300 s, fsync overhead is **< 1% of exposure time** in practice — effectively free.

**Same pattern applies to**:
- Preview JPEGs (`<frame>.preview.jpg`, alt-stretch variants per §65)
- Thumbnails (`<frame>.thumb.jpg`)
- Sequence files saved by the user
- Profile JSON exports
- Calibration metadata writes

**Does NOT apply to** (lossy is acceptable):
- Diagnostic logs (rotated, append-only, lossy)
- Cache directories (preview variants regenerable from FITS, surveys re-downloadable, etc.)

### 28.8 Startup scan + orphan recovery

Before §28.2's equipment-reconnect routine runs, the server performs a quick filesystem audit:

1. **Mount + writability check** — verify the configured save path (default `/media/openastroara`) is mounted and writable. If not, abort startup with a clear error logged to systemd journal + a `storage.unavailable` critical notification queued for next WILMA connect. Server does NOT proceed without writable storage.
2. **Filesystem type check** — per §28.9, refuse to start if FS is not ext4 (hard refuse, not warning).
3. **`.tmp` sweep** — `find <captures>/ -name '*.tmp' -mmin +5 -delete`. Any `.tmp` file older than 5 minutes is assumed crashed-mid-write and deleted. (Live writes finish in seconds; 5 minutes is generous slack for slow USB sticks.)
4. **Orphan FITS scan** — for every `.fits` file in `<captures>/`, check whether a corresponding `frames` row exists. If not (orphan), re-insert by parsing the FITS header:
   - Required header fields: `DATE-OBS`, `EXPTIME`, `OBJECT` (or fall back to "Unknown Target"), `FILTER` (or "—"), `IMAGETYP`
   - Optional metadata: `GAIN`, `OFFSET`, `XBINNING`, `CCD-TEMP`, `HFR`, `STARS`, etc. (recovered if present)
   - The recovered row joins the frame's existing session (looked up by parent directory) or creates a synthetic `recovered-<timestamp>` session if the session is also missing.
   - Emit WS event `frame.recovered_orphan` per recovered frame so WILMA reflects it in the library + Stats.
5. **Orphan preview/thumb regeneration** — for any recovered FITS that lacks a preview or thumbnail, generate them async in the background (low priority worker, doesn't block recovery).

Auto-recovery is **silent + notification-only** (per the bake decision): the user sees a single summary notification *"Recovered N frame(s) from previous session — added to library"* and detailed entries in the §46 notification feed. No banner; no quarantine; no per-frame prompt. The orphan-scan is bounded to the captures dir (won't recursively scan the whole drive); typical execution time on a Pi 4 with ~10k frames is under 2 seconds.

### 28.9 Filesystem + mount requirements

**ext4 is mandatory.** Two layers of enforcement:

1. **At configure time** — the §29.1.4 helper script validates `blkid` reports ext4 before mounting; non-ext4 drives are rejected with `code: not_ext4`, surfaced to WILMA as the in-app reformat flow (§29.1.3).
2. **At startup** — ARA refuses to start if the configured save path's filesystem is not ext4 (defense in depth against fstab edits, manual remounts of a different drive, or drives swapped while server was off).

Rationale: NTFS / exFAT / FAT32 have weaker durability semantics (no journaling, weaker fsync guarantees, no proper Unix permissions), and Btrfs / ZFS while excellent are out of scope for v0.0.1 (untested + larger DEPLOY.md surface area).

Detection: server queries `stat -f -c %T <save-path>` at startup; rejects anything other than `ext2/ext3` (which is the ext4 family name returned by stat). exFAT returns `msdos`, NTFS returns `fuseblk` or `ntfs`, etc.

If the FS is wrong: server logs the error, queues a critical notification for next WILMA connect ("Storage drive is formatted as exFAT — please reformat as ext4 per DEPLOY.md"), and exits with non-zero status (systemd restart loop will keep retrying every `RestartSec=3` per §63's pattern, but the storage error persists until the user fixes it).

**Recommended mount options** (DEPLOY.md fstab guidance):

```
UUID=<drive-uuid>  /media/openastroara  ext4  defaults,data=ordered,noatime,errors=remount-ro  0  2
```

- `data=ordered` — ext4 default; journals metadata before data
- `noatime` — skip access-time updates (reduces writes; access time isn't useful here)
- `errors=remount-ro` — if FS corruption detected mid-session, remount read-only instead of allowing further damage. Server detects this and emits `storage.error` critical notification.

### 28.10 UPS recommendation (DEPLOY.md, advisory)

A USB-attached UPS keeps the Pi alive long enough on power loss to finish the in-flight FITS atomic-write, checkpoint WAL, pause the sequence, park the mount per §35's safety policy, and clean-shutdown. Without a UPS, in-flight exposures are lost (the §28.7 + §28.8 design ensures no corruption — but the active exposure is gone).

DEPLOY.md adds a "Recommended hardware" section listing options (Geekworm X728, PiJuice, generic 12 V UPS HATs) with one-line summaries. ARA works fine without a UPS — this is a "for night-long unattended runs, strongly consider" recommendation, not a requirement.

Future (v0.1.0): UPS GPIO signal pin can trigger a proactive checkpoint + park sequence ahead of battery exhaustion. Out of scope for v0.0.1 because every UPS exposes its signal differently and ARA would need device-specific shims.

### 28.11 Scenario matrix — what's lost vs preserved

| Crash point | FITS state | DB state | Net effect after restart |
|---|---|---|---|
| Power loss during exposure | nothing written | nothing | Exposure lost (already documented §28.1). §28.2 recovery resumes from previous instruction. |
| During FITS body write | `.tmp` partial | nothing | Startup §28.8 deletes `.tmp`; user sees nothing missing. |
| Between fsync + rename | `.tmp` complete, no `.fits` | nothing | Startup §28.8 deletes `.tmp` (conservative; the rename hadn't committed). |
| Between rename + dir fsync | `.fits` may or may not be visible after fs replay | nothing | If visible → §28.8 orphan-scan re-inserts; if invisible → as-if frame never happened. |
| Between dir fsync + DB row | `.fits` visible | nothing | Orphan scan picks it up; user gets `frame.recovered_orphan` event. |
| During DB row write | `.fits` durable | WAL guarantees no corruption; row may be missing | Orphan scan picks it up. |
| After DB row inserted (steady state) | `.fits` durable | row durable | Full success. |
| Mid-WAL-checkpoint | both durable | WAL replay completes on next open | No data loss. |
| USB drive yanked mid-write | `.fits` partial in OS page cache, never reaches drive | DB write fails | Server logs `storage.unavailable`; on next mount + restart, §28.8 cleans up `.tmp` and any orphans. |

**Net property of v0.0.1's durability design:** no partial FITS files ever appear under their real name; no orphan FITS file ever becomes invisible to the library; no SQLite corruption is possible on power loss; the maximum data loss from a power event is "the single exposure that was actively integrating when power died."

### 28.12 "Paused sequence" semantics

§28 + §35 (safety policies) + §42 (fault recovery) + §44 (backup stream) all reference "pause." This is what stays running vs holds vs aborts when a sequence pauses (whether via user action, safety event, or fault):

| Component | Behavior during pause |
|---|---|
| **Cooler** (if active) | **Keeps running** at the current target temp — preserves thermal stability so resume doesn't need a re-stabilization wait |
| **Guider** (PHD2) | **Keeps running** (`pause` RPC if user-paused; full `guiding` state if safety/fault paused) — keeps the mount settled so resume is immediate |
| **Mount tracking** | **Keeps running** — target stays centered for free; no re-acquisition needed on resume |
| **Filter wheel** | **Holds** at current position |
| **Focuser** | **Holds** at current position |
| **Rotator** | **Holds** at current position |
| **Dome** | **Holds** at current position (no auto-park during pause) |
| **In-flight exposure** | **Aborts** — the integrating frame is lost; partial bytes discarded per §28.7 atomic-write pattern (no `.tmp` ever appears as a real `.fits`) |
| **WS broadcasts** | **Continue** — state changes, telemetry, frame events for already-completed frames all still flow |
| **Diagnostics (§51)** | **Continue analyzing** completed frames but emit no auto-actions while paused (paused state is itself the user signal) |
| **Notifications (§46)** | **Continue** — pause-cause notifications + any new safety / equipment events still raise |
| **Backup stream (§44)** | **Resumes during pause** — paused = idle = backup runs at full configured bandwidth (no capture I/O competition) |
| **Stats aggregator (§50)** | **Continues** — pause time is logged separately (counts as "downtime" in session-efficiency analytics) |
| **REST API** | **Fully responsive** — user can edit safety policies, view library, change settings, etc. while paused |
| **Storage monitoring** | **Continues** — disk-full / storage-slow events still raise |
| **Equipment polling** | **Continues** at the §66 cadence — equipment-state changes still surface, just no exposures triggered |

**Resume semantics:**
- User taps **[Resume]** (or safety/fault condition clears) → server checks all "keeps running" components are still healthy → if healthy, advance sequencer to next instruction
- If guider lost calibration during pause (rare — mount slipped, star drift) → resume triggers guider re-cal automatically, ~30s
- If cooler drifted from target (warm ambient, cooler failed) → resume waits up to 60s for re-stabilization, then proceeds with warning notification

**Pause vs Abort vs Stop:**
- **Pause** — described above; everything stays connected and ready; resume restores the running state
- **Abort** — sequence ends; mount stays where it is; cooler stays on; user controls what happens next
- **Emergency Stop** (§35.3) — sequence ends; mount parks per §35 safety policy; cooler may warm or stay depending on `cooler_warmup_on_session_end` (§28.13); equipment disconnects per safety policy

### 28.13 Cooler warmup at session end

When a sequence ends (graceful completion, user abort, emergency stop) or the user explicitly disconnects the camera, ARA's behavior depends on the per-profile `cooler_warmup_on_session_end` setting:

| Value | Behavior |
|---|---|
| `off` *(v0.0.1 default)* | Disconnect immediately. Camera reports warm-up complete when its internal thermal mass equalizes — ARA doesn't wait. Recommended for modern ZWO / QHY CMOS cameras, which tolerate rapid temperature change. |
| `ramp` | Ramp the cooler power back toward 0% at the configured rate (default 1 °C/min, configurable via `cooler_warmup_rate_c_per_min`); when temp reaches ambient ± 1°C, disconnect. Adds ~30s–2min depending on the cold delta. Recommended for cooled CCDs and humid environments — prevents thermal-shock condensation on the sensor window. |
| `immediate` | Explicit "warm now" command (sets cooler power to 0% directly), then disconnect. Functionally similar to `off` but issues the explicit Alpaca `CoolerOn = false` before disconnect for drivers that need it. |

**Where set:** wizard Screen 5 (Camera, §37.3) defaults to `off`; user can change in Settings → Equipment → Camera. Searchable via §61 keywords.

**Sequence integration:** when `ramp` is selected, the sequencer adds a post-sequence cooldown phase. WILMA shows a banner: *"Cooling down (12°C → 22°C ambient at 1°C/min)... ~10 min remaining."* User can [Skip warmup + disconnect] to abort the ramp.

**Profile schema:**

```json
{
  "camera": {
    ...
    "cooler_warmup_on_session_end": "off",
    "cooler_warmup_rate_c_per_min": 1.0
  }
}
```

**§61 search registry:**
- `camera.cooler_warmup_on_session_end` — keywords: `warmup, ramp down, session end, cooler shutdown, thermal shock, condensation, cooldown`
- `camera.cooler_warmup_rate_c_per_min` — keywords: `warmup rate, ramp rate, cooldown speed`

**v0.1.0 paths:**
- **Detect-camera-type default** — auto-pick `off` for CMOS / `ramp` for CCD via Alpaca `CameraType` property. Some cameras misreport, so override stays available.
- **Humidity-aware ramp** — if observing-conditions reports humidity > 80%, force `ramp` regardless of setting.

### 28.14 SQLite schema migrations across releases — EF Core + mandatory pre-migration backup

When a new ARA Core version ships with schema changes (new columns on `frames`, new tables for v0.1.0 Live Stacking, etc.), existing users' databases must migrate forward without data loss. ARA uses **EF Core migrations** as the migration framework, with a **mandatory pre-migration backup** policy that always runs before any schema change touches the database.

**Why EF Core (not hand-rolled SQL or recreate-on-mismatch):**

- Tooling generates migrations from model changes (`dotnet ef migrations add <Name>`) — code-review surface is a versioned C# file with explicit `Up()` / `Down()` methods
- Migration history table (`__EFMigrationsHistory`) is the schema-version source of truth
- Idempotent application (`Database.Migrate()` no-ops if all migrations are applied) — safe to run on every startup
- Battle-tested in production at scale; community knowledge for diagnosing failures
- Cost: ~10 MB runtime dependency (`Microsoft.EntityFrameworkCore.Sqlite` + provider) — acceptable on the Pi's 4 GB headroom

NINA's data layer is replaced wholesale during Phase 4 (server scaffold) — the OpenAstroAra.Data project is greenfield, so introducing EF Core has no migration cost from inherited code.

**Mandatory pre-migration backup — the user requirement:**

Before applying any pending migration on startup, ARA always backs up the current database. This is non-optional, not configurable, and not skippable. The backup is the safety net for: migration bugs, partially-applied transactions due to power loss mid-migration, schema regressions discovered after a version is released, and the user wanting to roll back to the prior server version.

**Backup pipeline (runs in §28.2 startup recovery routine, before EF Core's `Database.Migrate()`):**

1. **Detect pending migrations.** Server calls `db.Database.GetPendingMigrationsAsync()`. If empty, skip the entire backup + migration block; proceed to normal startup.
2. **WAL checkpoint.** Run `PRAGMA wal_checkpoint(TRUNCATE)` to flush pending writes into the main DB file. Ensures the backup captures a consistent state.
3. **Backup the DB.** Use SQLite's online backup API (`SqliteConnection.BackupDatabase()`) — safe with WAL, doesn't require closing the DB. Backup destination is on the USB drive (not SD card) at:

   ```
   /media/openastroara/.araback/migrations/data_pre-v<from>-to-v<to>_<iso8601>.db
   ```

   Example: `data_pre-v0.0.1-to-v0.0.2_20260601T142733Z.db`. Naming embeds both the source version (running ARA's reported version) and the target version (the assembly version we're about to migrate into) so rollback is unambiguous.

4. **Backup integrity check.** Run `PRAGMA integrity_check` on the backup file. If it returns anything other than `ok`, abort the migration entirely:
   - Server logs `MIGRATION_BACKUP_INTEGRITY_FAIL` (critical)
   - Server refuses to start; systemd will retry per `RestartSec=3` but the failure persists
   - WILMA on next connect sees `pending_restart` flag style state: a critical notification "Database migration aborted — backup integrity check failed. Contact support; do not retry without expert help."
   - This guards against migrating into a corrupted state if the source DB has subtle damage

5. **Retention policy.** Keep the last 5 migration backups; older backups are auto-deleted at this step. (Users wanting longer retention can copy backups out manually via WILMA's backup UI per §43.)

6. **Apply EF Core migrations.** Server calls `await db.Database.MigrateAsync()`. EF Core wraps each migration in its own transaction; if any migration throws, the partial transaction rolls back, the DB remains at the prior schema version, and an exception bubbles up to step 7.

7. **Failure handling — automatic restore.** If `MigrateAsync` throws:
   - Server logs `MIGRATION_FAILED` (critical) with the full exception + migration name
   - Server copies the backup from step 3 back over the live DB path (preserving WAL by copying `data.db` only — the WAL was already checkpointed in step 2, so the backup is self-contained)
   - Server re-checks `__EFMigrationsHistory` to confirm we're back at the source schema version
   - Server emits a startup-failure WS event (queued for next WILMA connect) with: source version, target version, failed migration name, exception summary
   - Server starts up on the **prior** schema version (old code paths still work because we haven't loaded the new binary's data-layer assumptions yet — caveat: this only works if the new binary's code is backward-compatible-readable on the old schema; see "Failure on mixed-version mismatch" below)
   - WILMA shows critical notification with [Roll back server to v<previous>] action — which kicks the user to a manual `apt install openastroara-server=<previous version>` flow via DEPLOY.md

8. **Success path.** On clean migration, server logs `MIGRATION_APPLIED` per migration with timing, emits `server.migration_complete` WS event (`{from_version, to_version, migrations_applied: [...], backup_path, elapsed_ms}`), and proceeds with normal startup.

**Migration UX from the user's perspective:**

For most upgrades (small schema changes — adding a nullable column, creating a new table), the entire backup + migrate flow completes in 1–5 seconds. The user sees nothing different from a normal restart.

For larger migrations (e.g., a v0.1.0 backfill that touches every row in `frames`), startup time can stretch to 30–60+ seconds. To prevent the user thinking the server crashed:

- Server emits `server.migrating_database` WS events with progress (per-migration name + estimated row count + completed count) — but this is best-effort since most clients are disconnected during startup
- WILMA's connect screen detects "server unreachable for > 10 seconds after expected boot" and shows: "Server is taking longer than usual to start — it may be applying a database upgrade. Wait up to 5 minutes before troubleshooting."
- After connect, `pending_restart`-style banner shows briefly: "Database migrated from v0.0.1 to v0.0.2 — backup saved to .araback/migrations/" then auto-dismisses after 10 s

**Forward-compatibility policy (downgrade attempts):**

Server refuses to start if the DB's `__EFMigrationsHistory` contains migrations the running binary doesn't know about (i.e., user installed v0.0.3, ran it once, then downgraded to v0.0.2). Behavior:

- Server logs `DB_SCHEMA_AHEAD_OF_BINARY: db_max_migration=<name>, binary_max_migration=<name>` (critical)
- Server exits with non-zero status; systemd restart loop keeps trying but failure persists
- WILMA on next connect sees this via a `/healthz` (per Tier 2 health-check gap) or `/api/v1/server/state` 503 with `code: "schema_ahead_of_binary"`
- User's path forward: re-install the newer server version (data is intact) OR restore a pre-migration backup from `.araback/migrations/` via `/api/v1/server/restore-from-backup {path}` (deferred to v0.1.0 — for v0.0.1, this is a DEPLOY.md manual SSH instruction)

ARA does **not** generate down-migrations in v0.0.1. EF Core supports `Down()` methods but maintaining them doubles the testing surface and the realistic recovery path is "restore backup, downgrade binary" — not "run a down-migration that the dev team only partially tested." If a user needs to roll back schema, they restore the pre-migration backup. v0.1.0 may reconsider for specific high-risk migration types.

**Bundled migrations structure:**

```
src/OpenAstroAra.Data/
├── OpenAstroAra.Data.csproj  (refs Microsoft.EntityFrameworkCore.Sqlite)
├── AraDbContext.cs           (DbContext with DbSet<Frame>, DbSet<Session>, ...)
├── Entities/
│   ├── Frame.cs
│   ├── Session.cs
│   └── ...
└── Migrations/               (auto-generated, hand-reviewed)
    ├── 20260101_InitialCreate.cs        (Phase 4 baseline)
    ├── 20260101_InitialCreate.Designer.cs
    ├── 20260601_AddSessionNotes.cs      (example v0.0.2)
    └── AraDbContextModelSnapshot.cs
```

Migration files are bundled into the assembly; no external `.sql` files to ship. The .deb's `/opt/openastroara/` install path stays clean.

**Workflow for adding a migration (developer / AI during the port):**

1. Modify entity class or DbContext (e.g., add property `public string? Notes { get; set; }` to `Session`)
2. Run `dotnet ef migrations add AddSessionNotes --project src/OpenAstroAra.Data` — generates the `Up()`/`Down()` migration file
3. Hand-review the generated SQL via `dotnet ef migrations script --idempotent`
4. Run `dotnet ef database update` against a test DB to verify
5. Add an integration test in §14.1 covering: start with prior-version DB fixture → run server → assert migration applied + data preserved
6. Commit (migration file + entity change + test in same commit per the settings-registry-style discipline)

The pre-PR gate (§14.4) runs the integration tests, which exercise the migration flow against bundled prior-version DB fixtures (`OpenAstroAra.Test/fixtures/migrations/v0.0.1.db`, etc.).

**Cross-platform consideration (dev machines):**

On macOS/Windows dev machines where there's no USB drive, backups go to `~/Library/Application Support/OpenAstroAra/Backups/migrations/` (macOS) or `%LOCALAPPDATA%\OpenAstroAra\Backups\migrations\` (Windows). Same retention policy (last 5). The integration test suite (§14.1) runs migrations against ephemeral SQLite files in `/tmp` per test — no real backups created.

**API surface added:**

| Endpoint | Method | Purpose |
|---|---|---|
| `/api/v1/server/migrations/history` | GET | Returns ordered list of applied migrations + timestamps from `__EFMigrationsHistory` + paths to corresponding backups still on disk |
| `/api/v1/server/migrations/backups` | GET | Lists existing pre-migration backups in `/media/openastroara/.araback/migrations/` with size + timestamp + source/target versions |
| `/api/v1/server/restore-from-backup` | POST | v0.1.0 — pointer-only endpoint in v0.0.1 returning 501 "use manual restore procedure in DEPLOY.md" |

**WebSocket events added:**

| Event | Payload | When |
|---|---|---|
| `server.migrating_database` | `{migration_name, rows_processed, rows_total, elapsed_ms}` | Per-migration progress during long migrations (best-effort; most clients disconnected during startup) |
| `server.migration_complete` | `{from_version, to_version, migrations_applied: [...], backup_path, elapsed_ms}` | Once after all pending migrations applied successfully |
| `server.migration_failed` | `{from_version, attempted_to_version, failed_migration, exception_summary, backup_path, restored: bool}` | If migration throws; queued for next WILMA connect |

**§61 search registry entries** (added in 12h Settings sub-PR):

- `server.migrations.history` — keywords: `database version, schema version, migration history, db upgrade, what migrations`
- `server.migrations.backups` — keywords: `database backup, migration backup, pre-upgrade backup, restore database, roll back schema`

**§14.1 integration test cases (added):**

- `migration_applies_on_startup` — fixture DB at v0.0.1 schema → start v0.0.2 server → assert schema upgraded + backup present + data intact
- `migration_failure_restores_backup` — inject deliberately-failing migration → assert backup restored + server exits cleanly + WS event queued
- `forward_compat_refuses_unknown_migrations` — fixture DB at v0.0.3 schema → start v0.0.2 server → assert refuses to start with `schema_ahead_of_binary` error
- `pre_migration_backup_integrity_check_fails_aborts` — corrupt the source DB → start server → assert migration aborts + no schema change + critical log line
- `backup_retention_keeps_last_5` — apply 7 sequential migrations with backups → assert 5 most-recent remain + 2 oldest deleted

**v0.1.0 follow-ups:**

- WILMA UI for browsing backups + one-click restore (currently DEPLOY.md manual SSH path)
- Server-side downgrade flow: when user explicitly requests rollback, server stops + binary swaps + DB restores from pre-migration backup atomically
- Optional encrypted backups (`PRAGMA key`) for the v0.1.0 remote-access mode

---

## 29. Storage / disk-space policy

Two distinct storage domains: **Pi side** (FITS frames + session state + profiles + sequences + calibration library + logs, server-managed, **on a mandatory USB drive**) and **WILMA side** (bundled catalogs + downloaded sky imagery surveys + cached tiles + draft sequences, client-managed).

**Pi-side storage is on a USB drive — REQUIRED, not optional.** The Pi's SD card holds only the OS, the `openastroara-server` binary, the systemd unit, and `/etc/openastroara/`. All ARA persistent data (frames, DB, profiles, sequences, logs) lives on an external USB drive that the user provides and configures during first-run setup. Reasons:

- SD cards have limited write endurance (typically 1,000-10,000 P/E cycles on consumer cards). A typical astrophotography night writes 50-100+ GB of FITS data; on an SD card that's months-to-a-year of life before failure.
- SD card failure during a session = lost imaging data plus a bricked Pi.
- USB SSDs (or even quality USB 3.0 sticks) handle sustained writes orders of magnitude better.
- DEPLOY.md recommends USB 3.0 SSDs or quality USB 3.0 sticks from reputable brands. Strongly discourages "free promotional" USB sticks of unknown provenance.

The server refuses to enter "ready" state without a configured USB drive (§29.1.1).

### 29.0 WILMA-side storage (Flutter client)

Goes in platform-default app data directory:
- macOS: `~/Library/Application Support/OpenAstroAra/`
- iOS: app sandbox documents
- Android: app sandbox files dir
- Windows: `%APPDATA%\OpenAstroAra\`
- Linux: `~/.local/share/openastroara/`

Contents:
| Sub-directory | Contents | Size budget |
|---|---|---|
| `catalogs/` | Bundled HYG, Tycho-2, NGC/IC, Caldwell, Sharpless, MPC comets, constellation art, nebula vectors | ~200 MB (bundled, immutable) |
| `hips/<survey>/` | Downloaded HiPS tile sets per §36 (DSS2, Mellinger, eROSITA, etc.) | Variable — 0 GB to TB depending on what user downloads |
| `aladin-cache/` | Live-fetched HiPS tiles from CDS (auto-managed by Aladin Lite) | LRU-capped at user-configurable size (default 2 GB) |
| `sequences/` | Draft + saved sequence JSON files built in WILMA | Tiny (KB per file) |
| `profiles/` | Profile drafts (sync to Pi when connected) | Tiny |
| `frames-downloaded/` | FITS files the user pulled from the Pi for review | Variable, user-managed |
| `logs/` | Client-side logs | Rolling, capped at 100 MB |

Settings → Storage on WILMA shows total usage, per-survey breakdown, "Clear cache" button per category.

### 29.1 Save location (Pi side)

Server stores ALL persistent data on the configured USB drive at `/media/openastroara/`. Layout:

```
/media/openastroara/                            (mandatory USB drive)
├── captures/<session-id>/<target>/<filter>/    FITS frames
│   └── <frame>.fits + .thumb.jpg + .preview.jpg
├── calibration/                                Calibration library (§39.9)
│   ├── darks/<camera-id>/<gain>_<temp>_<exp>/
│   ├── bias/<camera-id>/<gain>_<offset>/
│   └── flats/<camera-id>/<filter>_<rot>_<focus>/
├── db/openastroara.db                          SQLite session + frames + profiles + sequences + faults DB
├── profiles/                                   Profile JSON files (canonical)
├── sequences/                                  Sequence library (§38.2)
│   ├── library/
│   ├── imported/
│   └── active/
├── templates/                                  User-customized templates
├── logs/                                       Serilog rotating output, capped 14 days
└── .araback/                                   Auto-generated backup zips (per §43)
```

The Pi's SD card holds only: OS, `openastroara-server` binary (under `/opt/openastroara/`), systemd unit, `/etc/openastroara/storage.conf`, and a tiny placeholder `/var/lib/openastroara/` (used only briefly during first-run before USB is configured).

### 29.1.1 USB drive configuration (first-run)

After `sudo apt install openastroara-server` completes:

1. Server starts in **`needs_storage`** mode. `GET /api/v1/server/info` returns `{ "status": "needs_storage", "available_usb_drives": [{...}, {...}] }`.
2. `GET /api/v1/server/storage/candidates` enumerates connected USB block devices (via `lsblk -J --output NAME,UUID,SIZE,MOUNTPOINT,LABEL,FSTYPE`) excluding the OS root + boot partitions. Drives need not be mounted yet — the helper script handles mounting.
3. WILMA prompts user: *"Select a USB drive for ARA data:"* with size + label + filesystem type + free space (if mounted) per option.
4. User picks → WILMA calls `POST /api/v1/server/storage/configure { "uuid": "..." }`.
5. Server invokes the **configure-storage helper** (§29.1.4) via sudo:
   - Helper validates the UUID, validates ext4, creates `/media/openastroara`, appends fstab line (idempotent), mounts the drive, chowns to `openastroara:openastroara`
   - On `ERROR: not_ext4` → server responds 422 with `{ "code": "not_ext4", "fs": "exfat" }`; WILMA presents the reformat path (§29.1.3)
   - On `ERROR: uuid_not_found` → server responds 422 with `{ "code": "uuid_not_found" }`; WILMA asks user to re-plug and retry
   - On `OK` → continue
6. Server:
   - Writes UUID to `/etc/openastroara/storage.conf` (permanent pin)
   - Creates directory structure on the USB drive
   - Initializes the SQLite DB per §28.6
   - If USB already has existing ARA data (re-using a drive from another Pi): asks WILMA `"Found existing ARA data on this drive (12 profiles, 47 sessions). Use this data?"` → either continue with existing data or initialize fresh
   - Transitions to **`ready`** mode

The configured UUID is pinned. If the user replaces the USB drive, they go back through the configuration flow. The user experience is **plug drive in → pick in WILMA → done** — no SSH, no manual fstab editing, no terminal.

### 29.1.3 ext4 reformat flow (in-app)

When the user picks a non-ext4 drive in §29.1.1, WILMA presents a destructive-action confirmation:

```
┌────────────────────────────────────────────────────────────┐
│  ⚠ Drive must be reformatted as ext4                        │
│  ──────────────────────────────────────────────────────────  │
│  Drive:     "WD My Passport"  (931 GB)                       │
│  Current FS: exFAT                                            │
│                                                               │
│  ARA requires ext4 for the durability guarantees its capture │
│  pipeline depends on (§28). To use this drive with ARA, it    │
│  must be reformatted. **This will permanently erase           │
│  everything on the drive.**                                   │
│                                                               │
│  To confirm, type the drive's label below:                    │
│  ┌───────────────────────────────┐                            │
│  │                                │                            │
│  └───────────────────────────────┘                            │
│  (case-sensitive)                                             │
│                                                               │
│  [Reformat as ext4 + use this drive]    [Pick another drive] │
└────────────────────────────────────────────────────────────┘
```

The [Reformat] button is disabled until the typed label matches exactly. On click, WILMA calls `POST /api/v1/server/storage/reformat { "uuid": "...", "label_confirmation": "..." }`. Server validates the label echo, then invokes the helper with `--format`:

- Helper double-checks the label against `blkid` output (defense in depth — the server already validated, but the helper validates independently)
- Helper unmounts the drive if mounted, runs `mkfs.ext4 -L <label>` (preserves the original label), then proceeds with the §29.1.4 mount + chown flow
- On success: returns `OK`; server transitions to **`ready`** and continues with §29.1.1 step 6

If the label confirmation fails server-side OR helper-side: server returns 422 with `{ "code": "label_mismatch" }`; WILMA re-prompts. Reformat never proceeds without two layers of label validation.

**Why label-echo confirmation:** stronger than a [Yes I'm sure] checkbox. Forces the user to look at the drive (or `lsblk` output) and copy the label deliberately. Prevents the "I clicked the wrong drive in the picker" disaster.

### 29.1.4 configure-storage.sh helper (sudoers-invoked)

Lives at `/opt/openastroara/scripts/configure-storage.sh`, installed by the .deb postinst (§34.3). Sudoers drop-in grants the `openastroara` user passwordless invocation.

**Interface:**

```
sudo /opt/openastroara/scripts/configure-storage.sh <uuid>
sudo /opt/openastroara/scripts/configure-storage.sh --format <uuid> <expected-label>
```

**Behavior (no `--format`):**

1. Validate `<uuid>` exists in `blkid` output. If not → echo `ERROR: uuid_not_found` + exit 2.
2. Look up the device's filesystem type via `blkid`. If not `ext4` → echo `ERROR: not_ext4 <fs-type>` + exit 3.
3. Ensure `/media/openastroara` exists (`mkdir -p`).
4. Check `/etc/fstab` for an existing line with this UUID. If absent, append:
   ```
   UUID=<uuid>  /media/openastroara  ext4  defaults,data=ordered,noatime,errors=remount-ro,nofail,x-systemd.device-timeout=10  0  2
   ```
5. `systemctl daemon-reload`.
6. `mount /media/openastroara` (idempotent — no-op if already mounted at the right UUID).
7. `chown -R openastroara:openastroara /media/openastroara`.
8. Echo `OK /media/openastroara` + exit 0.

**Behavior (`--format`):**

1. Validate `<uuid>` exists in `blkid` output. If not → exit 2.
2. Look up the device's current label via `blkid`. If `<expected-label>` doesn't match exactly → echo `ERROR: label_mismatch <actual-label>` + exit 4.
3. If the device is currently mounted, attempt `umount` (fail if busy → echo `ERROR: device_busy` + exit 5).
4. Run `mkfs.ext4 -F -L <expected-label> <device-path>` (preserves the label so subsequent ops see the same human-readable name).
5. Continue from step 3 of the no-`--format` path (create mount point, append fstab line if missing — note that mkfs.ext4 changes the UUID, so the fstab line should use the new UUID returned by `blkid` post-format, not the one passed in).
6. Echo `OK /media/openastroara <new-uuid>` + exit 0.

**Sudoers drop-in** (`/etc/sudoers.d/openastroara`, added by postinst per §34.3):

```
openastroara ALL=(root) NOPASSWD: /opt/openastroara/update.sh
openastroara ALL=(root) NOPASSWD: /opt/openastroara/scripts/configure-storage.sh
```

Scope is narrow: only these two scripts, only as root, only nopasswd. The `openastroara` user has no shell (system user per §34.3), isn't reachable interactively, and the scripts validate their own inputs. No direct `/usr/bin/mount` or `/sbin/mkfs.ext4` permissions are granted — all storage operations route through the helpers so their validation logic can't be bypassed.

**Helper script exit codes:**

| Code | Meaning |
|---|---|
| 0 | Success |
| 2 | UUID not found in `blkid` |
| 3 | Filesystem is not ext4 |
| 4 | Label mismatch on `--format` |
| 5 | Device busy (unmount failed) |
| 6 | mkfs.ext4 failed |
| 7 | mount failed |
| 8 | chown failed |
| 9 | fstab write failed (immutable / read-only `/etc`?) |

Each code maps to a structured `code` field in the server's 422 response so WILMA can surface a specific user-facing message.

### 29.1.2 USB unplug detection

Server watches the USB mount point (filesystem watcher on the parent dir). On unmount/disconnect:

- Active sequence pauses immediately at next safe point (no new exposures)
- Active capture: in-flight frame's bytes go to a tmpfs buffer, flushed once the drive returns OR discarded after 60s
- WebSocket event: `{ "type": "storage.unmounted", "severity": "critical" }`
- WILMA shows full-screen alert: *"USB drive disconnected. Sequence paused. Reconnect the drive to resume."*
- On remount (same UUID): server detects, resumes sequence; users sees "Storage reconnected" toast

If a different UUID mounts: server ignores it (not the configured drive).

### 29.2 Storage info endpoint

`GET /api/v1/server/storage` returns:

```json
{
  "save_path": "/media/usb1/captures",
  "is_usb_mount": true,
  "total_bytes": 1099511627776,
  "available_bytes": 442381533184,
  "frame_size_estimate_bytes": 52175232,
  "frames_remaining_estimate": 8479,
  "camera": {
    "name": "ZWO ASI2600MM Pro",
    "dimensions": "6248×4176",
    "bin": "1×1",
    "bit_depth": 16
  }
}
```

If no camera connected: `frame_size_estimate_bytes` defaults to 40 MB (a typical mid-sized CMOS estimate), with `camera: null`.

### 29.3 Frame-size estimation formula

```
bytes_per_pixel = 2 if camera.MaxADU > 255 else 1
frame_size_bytes = ceil(width / binX) * ceil(height / binY) * bytes_per_pixel + 16384  // FITS header overhead
```

Uses **uncompressed worst-case** for safety — real frames may compress to 30-60% via FITS RICE or XISF compression, but estimating high prevents surprises mid-night.

### 29.4 Sequence-start validation

When client sends "start sequence," server first checks:

- If `available_bytes < 2 GB`: include a warning in the start response
- If `is_usb_mount = false` (save path is on SD card or internal): include a USB-recommendation warning

Client displays the result as a confirmation modal before actual sequence kickoff:

> Save location: `/media/usb1/captures` — 412 GB free.
> ZWO ASI2600MM Pro at 1×1 bin produces ~52 MB per frame; room for ~8,479 frames.
>
> [Start sequence]  [Cancel]

Or if low / on SD card:

> ⚠ Save location is the Pi's SD card with 8.2 GB free (~110 frames).
> Recommended: configure a USB drive in Settings → Storage.
>
> [Start anyway]  [Cancel]

### 29.5 Mid-sequence disk full

- Capture write fails (`IOException: No space left on device`)
- Sequencer pauses at that instruction; state is checkpointed
- Queued notification: *"Capture failed — disk full at frame N. Free space or change save location, then resume."*
- No automatic deletion or rotation in v0.0.1. User intervenes.
- v0.1.0 may add rotation policies (delete oldest unflagged, archive to cloud, etc.)

### 29.6 Settings → Storage panel (client)

- Current USB drive: label, total / free space, throughput meter, "Healthy" indicator
- **Configured UUID** display (so user knows which drive is "the one")
- "Switch storage drive" → re-runs the §29.1.1 configuration flow (warns about migrating data)
- "Eject safely" → unmounts the drive cleanly so user can swap (sequence must not be running)
- Link to DEPLOY.md USB hardware recommendations + backup instructions
- Auto-prune policy editor (per §29.5): never / monthly / weekly with rating-based rules

### 29.7 DEPLOY.md content (USB drive setup)

**The default path is automatic.** Plug a USB drive in, then in WILMA: Settings → Storage → Configure → pick the drive. If the drive isn't ext4, WILMA offers in-app reformat (§29.1.3 with label-echo confirmation). If the drive is already ext4, the §29.1.4 helper mounts + chowns it and ARA starts using it. No terminal needed.

The manual fallback below is for advanced users with custom partitioning needs, immutable-distro setups where `/etc/fstab` isn't writable, or troubleshooting:

```bash
# 1. Plug in your USB drive (USB 3.0 SSD recommended; quality USB 3.0 stick acceptable).
#    DO NOT use a cheap promotional USB stick — they fail.

# 2. Find the drive's UUID:
lsblk -f
# Look for your drive (e.g., /dev/sda1) and note its UUID.

# 3. Format the drive as ext4 — REQUIRED. ARA refuses to start on non-ext4
#    filesystems per §28.9 (NTFS / exFAT / FAT32 lack the durability semantics
#    ARA's atomic-write pipeline depends on). This WIPES the drive — back up first.
sudo mkfs.ext4 -L openastroara /dev/sda1

# 4. Create the mount point + persistent mount with durability options per §28.9:
sudo mkdir -p /media/openastroara
echo 'UUID=<your-uuid> /media/openastroara ext4 defaults,data=ordered,noatime,errors=remount-ro,nofail,x-systemd.device-timeout=10 0 2' \
  | sudo tee -a /etc/fstab
sudo systemctl daemon-reload
sudo mount -a

# 5. Set ownership so the openastroara user can write:
sudo chown -R openastroara:openastroara /media/openastroara

# 6. Tell ARA to use this drive — open WILMA, Settings → Storage → Configure,
#    pick the drive. ARA detects it's already mounted + ext4 + writable and
#    skips the helper's mount/chown steps.
```

### 29.8 §61 search registry entries

- `storage.configure` — keywords: `usb drive, storage setup, mount, configure drive, ara data location, pick drive, where data goes`
- `storage.reformat` — keywords: `reformat drive, format usb, ext4, wipe drive, erase storage, convert exfat`
- `storage.switch_drive` — keywords: `change drive, swap drive, new usb, migrate data, switch storage`
- `storage.eject` — keywords: `eject, unmount, safely remove, disconnect drive, take drive out`
- `storage.troubleshoot` — keywords: `drive disappeared, mount failed, permission denied, can't write to drive, storage error, not ext4`
- `storage.logs.location` — keywords: `log files, server logs, where are logs, /var/log, journal`
- `storage.logs.retention` — keywords: `log rotation, log retention, log cleanup, disk usage logs, clear old logs`

### 29.9 Log rotation + retention (Pi side)

Logs live at `/var/log/openastroara/` (on the Pi's SD card, not the USB drive — keeps log writes off the data-write path and survives USB unmount). Serilog file sink writes one file per startup with daily rolling; `logrotate` handles compression + retention.

**Disk budget:** worst case ~3 GB (30 daily files × 100 MB pre-compress); realistic ~200–500 MB after gzip (~10:1 on text logs). Fits comfortably on a 16 GB SD card alongside the OS + binaries.

**.deb installs `/etc/logrotate.d/openastroara`:**

```
/var/log/openastroara/*.log {
    daily
    rotate 30
    size 100M
    compress
    delaycompress
    missingok
    notifempty
    copytruncate
    create 0640 openastroara openastroara
    sharedscripts
}
```

Key choices explained:
- `daily` — rotation cadence aligned with imaging session boundaries
- `rotate 30` — 30 days of history (enough to capture seasonal hardware quirks + bug-report context per §54)
- `size 100M` — safety cap; daily rotation usually triggers first, but a runaway-error scenario can't blow past 100 MB before rotation
- `compress` + `delaycompress` — gzip yesterday's file but keep today's uncompressed for live `tail -f` debugging
- `copytruncate` — Serilog keeps its file descriptor open; we copy + truncate instead of rename + reopen (would lose live writes during the gap)
- `create 0640 openastroara openastroara` — recreated file has correct ownership + tight read perms (bug-report zip per §54 reads via the openastroara group)
- `sharedscripts` — defensive; doesn't matter for the single-glob case but harmless

logrotate runs daily via `/etc/cron.daily/logrotate` (standard Debian — no extra unit needed).

**Pressure handling (storage approaches full):**

When `/var/log/openastroara/` free space drops below 500 MB:
- Server downgrades log level from Debug → Info (drops verbose-mode traffic)
- Emits `storage.log_pressure` WS event (severity: warning) with current free + threshold
- WILMA surfaces a non-modal notification in the §46 feed: "Server log volume low — consider checking SD card capacity"

When free space drops below 100 MB:
- Server downgrades log level further to Warning (drops Info except critical lines)
- Force-runs logrotate immediately (`logrotate -f /etc/logrotate.d/openastroara`)
- Emits `storage.log_pressure` (severity: critical)
- If post-rotation free space is still < 100 MB, server logs `LOG_VOLUME_CRITICAL_DROPPING_INFO_LOGS` once + stops writing Info entirely

At no point does log pressure block sequence operation — capture, dither, plate-solve, etc. all continue. The diagnostic value of logs is secondary to keeping the session running. The §66 backpressure model and the §28 atomic-write pipeline are unaffected; only Serilog's verbosity is throttled.

**Bug-report zip behavior (§54 cross-ref):**

The §54 bug-report zip's "logs" tier already pulls from `/var/log/openastroara/*.log*` (the `*` covers both rotated `.log.1`, `.log.2.gz`, etc. and the live `.log`). No change needed — logrotate's output is naturally bug-report-ready. Zip size estimate updated in §54 to "10–500 MB depending on retention window" (was "10–100 MB").

**Journal (systemd) coexists:**

systemd-journal captures stdout/stderr from the service unit; ARA Core writes structured logs to Serilog files for searchability + journal text logs as a backup. Journal rotation is system-managed (default `SystemMaxUse=4G` on Trixie); DEPLOY.md adds an optional `/etc/systemd/journald.conf.d/openastroara.conf` recommendation:

```
[Journal]
SystemMaxUse=1G
SystemMaxFileSize=50M
```

— but not required; default journal behavior is fine for typical Pis.

**Log location summary (cross-platform):**

| Platform | Path | Set by |
|---|---|---|
| Pi (production) | `/var/log/openastroara/ara-YYYYMMDD.log` | .deb postinst + logrotate |
| Linux dev | `/var/log/openastroara/` (if running as `openastroara` user) OR `~/.local/share/openastroara/logs/` (user dev) | env var `OPENASTROARA_LOG_DIR` |
| macOS dev | `~/Library/Logs/OpenAstroAra/ara-YYYYMMDD.log` | platform default; logrotate N/A; manual cleanup |
| Windows dev | `%LOCALAPPDATA%\OpenAstroAra\Logs\ara-YYYYMMDD.log` | platform default; logrotate N/A; manual cleanup |

On dev platforms (macOS/Windows), Serilog's own `retainedFileCountLimit=30` + `fileSizeLimitBytes=100MB` enforces the same caps in-process (no logrotate available). Production Pi relies on logrotate exclusively.

**WILMA UI surface:**

Settings → Storage adds a "Logs" sub-panel showing:
- Current log volume size (`du -sh /var/log/openastroara/`)
- Retention window (read-only display: "30 days, daily rotation, 100 MB max per day")
- [Open in file manager] (on Linux/macOS desktop)
- [Force rotate now] (calls `POST /api/v1/server/logs/rotate` — runs `logrotate -f`; returns 200 + new disk usage)
- [Download current log] (streams `/var/log/openastroara/ara-<today>.log`)

No user-facing knobs for rotation cadence / size cap in v0.0.1 — Linux logrotate defaults are battle-tested; per-user tuning has no real demand. v0.1.0 could expose them if observatory operators ask.

**§14.5 integration test cases (added):**

- `logrotate_rotates_at_size_cap` — write 100 MB of synthetic log lines, run `logrotate -f`, assert two files exist + new one is empty
- `log_pressure_event_fires_below_threshold` — fill log volume to 400 MB free, assert `storage.log_pressure` WS event emitted
- `log_pressure_does_not_block_capture` — under 100 MB free, start a 30 s exposure, assert frame completes + FITS file written

---

## 30. First-run + launch flow (client)

### 30.1 Launch sequence

1. **Splash screen** (1-2 seconds) — ARA logo placeholder. mDNS server discovery runs in background simultaneously.
2. **Server connect** — only shown when:
   - mDNS finds 0 servers (manual IP/port entry), OR
   - mDNS finds 2+ servers (user picks)
   - If exactly 1 server is found, skip this screen and connect automatically (no auth per §67)
3. **Profile box** — always shown, layout below
4. **Main app** — the NINA-style shell from §25

### 30.2 Profile box

```
┌──────────────────────────────────────────┐
│  OpenAstro Ara                           │
│  Connected to pi-observatory.local       │
│                                          │
│  Active profile:                         │
│    [▼ My Backyard Rig            ]       │   ← shown when ≥1 profile exists
│                                          │
│           [   Image   ]                  │   ← primary action, shown when ≥1 profile exists
│                                          │
│  ─────────  or  ─────────                │
│                                          │
│  [ + Add a Profile ]  [↗ Import Profile ]│   ← ALWAYS visible
└──────────────────────────────────────────┘
```

- **Existing profiles**: dropdown + [Image] visible; [Add] / [Import] also visible below
- **No profiles**: dropdown + [Image] hidden; [Add] / [Import] are the only actions
- Add / Import never gated behind picking an existing profile — users can experiment freely

### 30.3 Subsequent launches

Splash → (auto-connect to saved server, no auth per §67) → Profile box pre-selects last-used profile → click [Image] → main app. **Three taps from cold-launch to imaging.**

### 30.4 Add a Profile

[+ Add a Profile] launches the **mandatory profile setup wizard** (§37). The wizard is the only profile-creation path in ARA — there is no quick "minimal modal" alternative. The wizard supports [Skip — use defaults] on every screen and [Save & Exit Wizard] at any point, so users in a hurry can land in the main app within a few clicks while still going through the wizard's structure. On final save → `POST /api/v1/profiles` → returns new profile, becomes selected in the dropdown.

**Rationale:** every user-facing setting needs to flow through one canonical setup path so that recommended downloads (§36.12), equipment signatures (§30.7.1), safety policies (§35), and site location all get captured consistently. A bypass modal would make ARA's §0.5 pillar 3 ("discoverable + safe by default") impossible to enforce.

### 30.5 Import a Profile

Modal with a file picker:

- Accepted formats: `.profile.xml` (NINA's existing profile format) and `.profile.json` (future ARA-native format)
- Client uploads to `POST /api/v1/profiles/import` (multipart)
- Server validates schema, returns the parsed profile or a validation error
- Imported profile becomes selected
- If the import is a NINA profile that references equipment ARA can't replicate (e.g., a vendor-specific COM driver), profile imports successfully but those equipment slots are blanked with a one-time notification: *"Imported — your camera setting referenced ASCOM.QHYCCD.Camera which ARA doesn't support; please reselect via Alpaca."*

### 30.6 Server connection management

- Saved servers list (hostname + last-seen IP/port + version) in WILMA's local state
- No auth tokens to manage per §67
- Settings → Server panel: shows current server + connection state, "Forget this server" button
- Forget = removes the saved server entry; next launch shows the discovery flow for that server again (Pi-side state is unaffected)
- v0.1.0 introduces remote-access mode (§67.4) — that's when tokens come back, scoped to remote endpoints only

### 30.7 Equipment-change check on profile load

Fires between profile selection (§30.2) and main app load. **Only shown on subsequent launches of a profile that has been used before** — first-time profile load skips the prompt because there's nothing to invalidate yet.

**Why this exists:** several ARA subsystems calibrate against the user's specific physical rig — Smart Focus calibration table (§59), backlash values (§59.7), dither auto-magnitude based on pixel scale (§62), §58 first-flip-confirm, filter focus offsets, PHD2 calibration. If the user changes gear between sessions and ARA doesn't know, those calibrations are silently stale. NINA has no equivalent check; users discover stale values the hard way.

**The prompt (default path — Alpaca detected no changes, no user concerns):**

```
┌──────────────────────────────────────────────────────┐
│  Welcome back to "C14 on CEM120"                      │
│  Last session: 2 days ago                             │
│  ──────────────────────────────────────────────────  │
│  Has any gear changed since then?                     │
│                                                       │
│  [ ✓ Nothing changed — continue ]   ← default action  │
│  [ Yes — let me check what changed ]                  │
└──────────────────────────────────────────────────────┘
```

Two taps in the 95% case.

**Expanded prompt (Alpaca detected a change OR user clicked "let me check"):**

```
┌──────────────────────────────────────────────────────┐
│  Welcome back to "C14 on CEM120"                      │
│  Last session: 2 days ago                             │
│  ──────────────────────────────────────────────────  │
│  ⚠ ARA detected one possible change automatically:    │
│  ☑ Focuser (was: Moonlite NiteCrawler — now:          │
│              ZWO EAF #DEF456) — pre-checked           │
│                                                       │
│  Anything else?                                       │
│  ☐ Camera (last: ZWO ASI2600MM Pro #ABC123)           │
│  ☐ Telescope / focal length / reducer                 │
│  ☐ Filter wheel / filters                             │
│  ☐ Mount (last: iOptron CEM120)                       │
│  ☐ Pier / tripod / dovetail / saddle                  │
│  ☐ Guide scope / guide camera / PHD2 setup            │
│  ☐ Something else                                     │
│                                                       │
│  [ Continue ]    [ Cancel — back to profile picker ]  │
└──────────────────────────────────────────────────────┘
```

### 30.7.1 Alpaca auto-detection

Server tracks per-profile equipment signatures via Alpaca `DriverInfo` + `Name` + `DriverVersion`. On profile load, signatures are compared to those stored in the profile. Mismatches:
- Pre-check the relevant checkbox in the prompt
- Show before/after detail line (e.g., *"was: Moonlite NiteCrawler — now: ZWO EAF #DEF456"*)
- User confirms or unchecks (in case it's a driver version bump rather than a real swap)

Alpaca catches obvious cases. The manual checkboxes catch what Alpaca can't see (pier/tripod/dovetail changes, OTA swap on the same camera+focuser, PHD2 config changes, filter swap in the wheel).

### 30.7.2 Invalidation matrix

When user confirms a gear change (checkbox ticked + [Continue]), the affected subsystems are marked stale and re-calibrate on next use:

| Gear change | Invalidates | Behavior |
|---|---|---|
| **Camera** | Pixel scale → dither auto-magnitude (§62); cooler ramp targets sanity-check | Dither magnitude recomputed lazily on next exposure; cooler config flagged "verify in Settings → Camera" |
| **Telescope / focal length / reducer** | Pixel scale (affects §62 dither + §59 AF feature sizing + framing FOV); §58 first-flip-confirm reset | All of the above; user reminded to verify focal length in Settings → Telescope |
| **Focuser** | Smart Focus calibration table (§59); backlash values (§59.7); focus-temp slope | Smart Focus recalibrates on next AF trigger (~5 min one-time cost) |
| **Filter wheel / filters** | Per-filter focus offsets (§59); filter-specific dither cadence (v0.1.0); flat library matching (§39) | Per-filter AF runs on first use of each filter; flat-matching warns if filter names changed |
| **Mount** | §58 first-flip-confirm reset; meridian flip pause windows verified; polar-align result flagged stale | First flip on new mount fires the §58.8 60-second confirm prompt again |
| **Pier / tripod / dovetail / saddle** | §58 first-flip-confirm reset; `pause_after_min` flagged for verification | First flip fires confirm; wizard's mount screen surfaces a banner *"Verify your `pause_after_min` value still fits your physical rig"* |
| **Guide scope / guide camera / PHD2** | PHD2 calibration; dither settle params; guider RMS baseline (§50) | PHD2 recalibrates on next guiding start (full cal, not auto-restore) |
| **Something else** | Generic flag | Server adds a banner to main app: *"You indicated some gear changed but didn't specify. Calibrations may be stale. Verify before unattended use."* |

Each invalidation logged in the session DB so user can see exactly what got reset and why.

### 30.7.3 Result on the user's screen after [Continue]

If anything was invalidated, the main app shell shows a non-blocking banner across the top:

```
ℹ Equipment-change check: Focuser changed. Smart Focus will recalibrate
  on next autofocus trigger (~5 min one-time cost).        [Dismiss]
```

Auto-dismisses when the relevant calibration completes naturally. User can also force calibrations now from each subsystem's Settings panel.

The same banner shell also hosts the **sky-data-missing banner** described in §36.13 (recommended Data Manager downloads not yet completed for this profile's rig). Multiple banner variants can stack; each shows independently with its own [Dismiss] and CTA.

### 30.7.4 Profile schema additions

```json
{
  "equipment_signatures": {
    "camera":   { "driver_info": "...", "name": "...", "version": "...", "last_seen_at": "..." },
    "focuser":  { ... },
    "mount":    { ... },
    "filter_wheel": { ... },
    "guider":   { ... }
  },
  "last_session_at": "2026-05-19T03:14:15Z",
  "calibration_state": {
    "smart_focus":      { "valid": true,  "last_calibrated_at": "..." },
    "backlash":         { "valid": true,  "last_refined_at": "..." },
    "dither_magnitude": { "valid": true,  "computed_for_pixel_scale": 1.43 },
    "flip_first_confirmed": true,
    "phd2_cal":         { "valid": true,  "last_at": "..." }
  }
}
```

Invalidation = set the `valid` flag to false. The relevant subsystem checks this flag before its next use and re-calibrates if needed.

### 30.7.5 API + WebSocket

| Method | Path | Purpose |
|---|---|---|
| `POST` | `/api/v1/profiles/{id}/equipment-change-check` | User submits which gear changed; server invalidates calibrations |
| `GET` | `/api/v1/profiles/{id}/calibration-state` | Lists current validity of every calibration (for the banner UI) |

```json
{ "type": "calibration.invalidated", "payload": { "subsystem": "smart_focus", "reason": "user_reported_focuser_change", "will_recalibrate_on": "next_af_trigger" } }
```

### 30.7.6 §61 search registry

- `equipment.change_check_on_load` — keywords: `equipment changed, gear swap, recalibrate, what changed`
- `equipment.signatures` — keywords: `equipment fingerprint, driver info, camera model, mount model`

User can search "equipment changed" and jump to the equipment-change-check screen even after dismissing it.

### 30.8 Multi-server support (observatory with 2+ Pis)

ARA's typical deployment is one Pi per rig. Observatory operators sometimes run two scopes on two Pis simultaneously, controlled from a single WILMA app. v0.0.1 supports this via **one-server-at-a-time** with explicit server switching — distinct from §27's per-server single-client policy, which governs how many WILMAs can talk to one Pi. This section governs how one WILMA tracks multiple Pis.

**v0.0.1 model: known servers list + one active connection at a time.**

WILMA maintains a local list of known servers (per §30.6) with per-server metadata: nickname, last-seen address, last-seen version, last-connected timestamp, last-known online state. WILMA is connected to exactly one server at any moment; switching disconnects the current connection cleanly before establishing the new one.

**Why not concurrent connections in v0.0.1:**

Multi-server-concurrent introduces per-server state forking throughout WILMA (per-server notification feeds, per-server diagnostics state, per-server WS reconnect logic, per-server pending_restart banners, cross-rig dashboards, "which Pi is this safety modal coming from" disambiguation). Each addition is small; together they touch nearly every screen. The engineering payoff is real but disproportionate to the v0.0.1 user count. Single-active-server is shippable in Phase 12a (existing connection flow scales trivially); concurrent multi-server becomes a clean v0.1.0 effort once the v0.0.1 baseline is proven.

**Discovery model:**

- mDNS discovery (§32.4) finds *all* ARA servers on the LAN, not just the previously-connected ones
- Discovery flow runs continuously in the background (low-frequency) so the Servers menu shows up-to-date online/offline indicators for known servers + any newly-discovered ones
- A discovered-but-unsaved server appears in the menu as "Available: <name>" with [Save + Connect]

**Servers menu UI (always available in WILMA app shell, top-left next to server name):**

```
┌─ Servers ───────────────────────────────────────┐
│                                                  │
│ Active                                           │
│ ✓ joey-north (192.168.1.42:5555)  v0.0.1-ara.6  │
│                                                  │
│ Known                                            │
│ ○ joey-south (offline, last seen 2h ago)        │
│ ○ joey-grab-and-go (offline, last seen 6d ago)  │
│                                                  │
│ Available on this LAN                            │
│ ⊙ ara-pi-test.local (10.0.0.55:5555) v0.0.1-ara.6│
│                                                  │
│ ─────────────────────────────────────────────    │
│ [+ Add server manually]                          │
│ [⚙ Manage servers]                               │
└──────────────────────────────────────────────────┘
```

Status glyph conventions (per §53 color+symbol):
- `✓` (green) — connected
- `○` (gray) — known but currently offline
- `⊙` (blue) — discovered on LAN, not yet saved
- `⚠` (yellow) — known + online but version mismatch with WILMA

**Server switching flow:**

When user clicks a non-active server:

1. **Check current server state.** If a sequence is active on the currently-connected server, show modal:
   ```
   ⚠ A sequence is running on joey-north.

   Switching servers will keep joey-north's sequence
   running (the Pi is autonomous), but you won't see
   notifications from it while connected to joey-south.

   [Cancel]  [Switch + monitor joey-north via notifications]  [Switch now]
   ```
   - **[Cancel]** — no switch
   - **[Switch + monitor]** — switches, but enables a "background watcher" (see below)
   - **[Switch now]** — switches without watcher

2. **If no active sequence,** silent switch: tear down WS, close REST connections, persist last-known state for the leaving server, connect to the new server, run §32.5 hydration (`/api/v1/server/state` snapshot + ws_resume_token replay if available).

3. **Background watcher mode** (when [Switch + monitor]):
   - WILMA opens a *minimal* WS connection to the leaving server (low-traffic — only `notification.critical` and `notification.urgent` events; no frames, no telemetry, no stats)
   - Cross-server notifications surface in a separate "Other rigs" section of the §46 notification feed with the source server prefixed (`[joey-north] Sequence completed: NGC 7000 — 47 frames`)
   - Background watcher auto-closes if WILMA's process is killed or if the user switches WILMA's primary connection a second time (only one background watcher at a time in v0.0.1)
   - Critical/urgent notifications from the watched server can pop a modal in the active context — the user is reminded which Pi it's from
   - This is the minimum-viable cross-rig awareness for v0.0.1; full concurrent multi-server arrives in v0.1.0

**Settings → Servers panel (`[⚙ Manage servers]`):**

Full list with per-server metadata + actions:
- Rename (per-server nickname)
- Forget (removes from saved list; Pi-side state untouched per §30.6)
- Set as default-on-launch (WILMA auto-connects to this server on next launch)
- View last 7 days of connection history (helpful for debugging "did my Pi go offline last night?")

**Per-server WILMA state (what gets keyed by server UUID):**

- `last_seen_server_version` (per §33.7 changelog modal)
- Background-stream subscriptions (notifications + watcher mode)
- User preferences that are server-specific (Settings → Display ordering of equipment tabs, etc.)
- WS resume tokens (per §60.4)
- Bug-report draft state per-server (per §54)

WILMA preferences that are *user-global* (theme, font size, reduce-motion, ⌘K shortcut, suppress-changelog-modal, suppress-tooltips, etc.) live in a separate scope and apply across all servers.

**Conflict cases:**

| Scenario | Behavior |
|---|---|
| User has joey-north + joey-south saved; both come up after rebooting WILMA | Auto-connect to default-on-launch if set; otherwise show server picker (no auto-connect). Don't surprise users by auto-connecting to a random Pi. |
| Two saved servers have the same nickname (user typo) | Disambiguated by stable server UUID; menu shows hostname + IP as discriminator |
| Saved server's hostname/IP changes (DHCP lease moved it) | mDNS rediscovery finds it by service name + UUID, updates the saved address; user sees one momentary "reconnecting…" toast, no manual action |
| Saved server reports a different UUID than expected (Pi was reflashed) | Modal: "joey-south reports a new server identity. This usually means the Pi was reflashed. [Use as new] / [Forget old] / [Cancel]". Prevents silent association with a stranger's Pi at a star party |

**Notification scoping:**

- Active server's notifications behave per §46 (in-app feed, severity-driven UX)
- Background-watcher server's notifications go to the "Other rigs" section of the same feed, prefixed with the source server's nickname
- Quiet hours (§46.5) apply globally regardless of source server
- Emergency alarms (§35.5) from a watched server still fire the alarm modal — safety wins over context-switching cost

**v0.0.1 limitations (documented in DEPLOY.md):**

- One active server + one optional background watcher; no >2-server concurrent monitoring
- No cross-rig stats rollups (§50 Stats are per-server only)
- No cross-rig sequence orchestration ("alternate N frames on north, M frames on south")
- No cross-rig single-emergency-stop button (each server has its own §35.3)

**v0.1.0+ concurrent multi-server roadmap** (added to §55):

- Concurrent WebSocket connections, one per server
- Tabbed top-level UI (one tab per server, plus a "Rigs overview" tab)
- Cross-rig stats dashboard with per-rig + aggregated views
- Aggregated notification feed with optional per-rig filtering
- Cross-rig single-emergency-stop affecting all connected rigs simultaneously
- Optional cross-rig sequence orchestration (mosaic split across rigs, alternating targets, etc.)

### 30.8.1 API + WebSocket additions

| Endpoint | Method | Purpose |
|---|---|---|
| `/api/v1/server/info` | GET | Lightweight identity probe — returns `{server_uuid, nickname, version, mDNS_name, started_at}`. Called by WILMA's background discovery loop to confirm a saved server's identity. Cheap (no DB hit); separate from `/healthz`. |

WebSocket events from background-watcher mode use the same shapes as §46 notification events; WILMA's client-side routing distinguishes them by the connection they arrived on, not by event structure.

### 30.8.2 §61 search registry

- `app.servers.switch` — keywords: `switch server, change pi, multi-pi, multi-rig, observatory, two scopes, multiple servers`
- `app.servers.manage` — keywords: `manage servers, forget server, rename pi, server list, default server, auto-connect`
- `app.servers.watcher` — keywords: `background watcher, monitor other rig, notifications from other pi, cross-rig notifications`

### 30.8.3 §14 test cases

**§14.1 server integration tests:**
- `server_info_endpoint_returns_uuid_and_nickname`

**§14.2 widget tests:**
- `servers_menu_shows_active_known_and_discovered_servers`
- `switching_server_during_active_sequence_prompts_for_watcher_mode`
- `switching_server_with_no_active_sequence_silently_switches`
- `background_watcher_receives_critical_notifications_only`
- `reflashed_pi_uuid_mismatch_blocks_silent_reconnect`
- `default_server_auto_connects_on_launch`

### 30.8.4 Cross-references

- §27 — single-client policy (per-server; complementary to §30.8 single-active-server-per-WILMA)
- §30.6 — saved servers list (§30.8 extends with switching UX + discovery + watcher mode)
- §32.4 + §32.5 — mDNS discovery + state hydration on connect
- §35.5 — emergency alarms from watched servers still fire
- §46 — notification feed (background-watcher notifications are a section within the same feed)
- §55 — v0.1.0+ concurrent multi-server roadmap entry
- §67 — security model (no auth between WILMA and any Pi in v0.0.1; switching adds no new attack surface)

---

## 31. Time + location sync (WILMA waterfall)

ARA Core needs accurate UTC time and lat/long/altitude for: sidereal time, alt/az transforms, dawn/dusk schedules, plate-solve search, sequence triggers. A Pi without internet doesn't keep time (no RTC by default). WILMA helps via a waterfall of sync sources.

### 31.1 Flow

After profile selection on WILMA:

```
GET /api/v1/server/time-sync  → server reports sync state
     │
     ├─ synced & fresh (< 1h ago, trust ≥ medium) → proceed
     │
     └─ unsynced or stale → walk the waterfall:

         1. WILMA has internet?           push device clock + GPS/location
                                          POST /api/v1/server/time-sync
              (modern phones/laptops on Wi-Fi or cellular are NTP-synced)
              │ no
              ↓
         2. Server detects USB GPS on Pi? server self-syncs from /dev/ttyUSB* or /dev/ttyACM*
              (gpsd or direct NMEA read; no user action required)
              │ no
              ↓
         3. WILMA is mobile (iOS/Android)? push device clock + GPS (Flutter geolocator)
              │ no
              ↓
         4. Prompt: "Plug a USB GPS into the Pi" + [Retry]
              │ user skips
              ↓
         5. Manual entry modal: UTC date/time + lat/long/altitude
```

### 31.2 Trust levels

| Source | Trust |
|---|---|
| Pi NTP (if internet reaches the Pi directly) | high |
| USB GPS via gpsd / NMEA | high |
| WILMA on Wi-Fi or cellular with internet (NTP-synced device clock) | medium |
| Mobile GPS (no internet, device geo-fix only) | medium |
| Manual entry | low |

Trust is stored alongside the sync. Soft warnings fire when:
- Trust = `low` and the sequence about to run uses schedule-based instructions (`Wait until dusk`, etc.)
- Drift > 30s detected during a session

### 31.3 Endpoints

```
GET /api/v1/server/time-sync
Response:
{
  "synced": true,
  "source": "ntp|gps-internal|gps-external|client|manual|none",
  "trust": "high|medium|low|none",
  "current_time_utc": "2026-05-19T03:14:15.123Z",
  "system_time_offset_seconds": 0.0,
  "location": { "lat": 30.27, "lng": -97.74, "alt": 165.0 },
  "internet_available_on_pi": false,
  "internal_gps_available": false
}

POST /api/v1/server/time-sync
{
  "source": "client|gps-mobile|manual",
  "time_utc": "2026-05-19T03:14:15.123Z",
  "location": { "lat": 30.27, "lng": -97.74, "alt": 165.0 },
  "trust": "medium"
}
Response: {
  "before": { "time_utc": "...", "offset_seconds": -42.5 },
  "after":  { "time_utc": "...", "offset_seconds": 0.0 },
  "location_updated": true
}
```

### 31.4 Implementation notes

- Server sets the system clock via a CAP_SYS_TIME-granted helper. DEPLOY.md adds `sudo setcap cap_sys_time+ep /opt/openastroara/OpenAstroAra.Server` post-install.
- USB GPS detection: server scans `/dev/ttyUSB*` and `/dev/ttyACM*` on startup for NMEA `$GPRMC` / `$GPGGA` sentences. If detected, server self-syncs without WILMA involvement.
- WILMA mobile GPS: `geolocator` Flutter plugin, permission-prompts on first use. Cached for the session.
- All astronomy computations use **UTC internally**; client displays in user's local TZ.
- Time-sync state is cached per-session; doesn't re-prompt unless the Pi reboots or > 12 hours pass.

### 31.4.1 Background NTP daemon — ARA does nothing

ARA Core does NOT install, configure, or rely on a local NTP daemon (chrony, ntpd, systemd-timesyncd). The §31 waterfall is sufficient on its own: WILMA pushes time at session start; USB GPS feeds during the session; user can manually re-prime if needed.

systemd-timesyncd ships enabled by default on Trixie and will keep the clock within a second of pool.ntp.org *when the Pi has internet* — ARA neither depends on it nor disables it. If the user has installed chrony or another daemon for their own reasons (observatory automation, multi-Pi sync, gpsd shared-memory ref-clock), ARA's waterfall happily coexists; WILMA push just becomes one more opportunistic time source on top.

**What the user must know** (DEPLOY.md addition):

> ARA's server gets its clock from (in priority order):
> 1. USB GPS dongle plugged into the Pi (via NMEA `$GPRMC` parsing — always preferred when present)
> 2. WILMA pushing time on every connect (handles ~95% of typical sessions)
> 3. Whatever Debian's default NTP setup (systemd-timesyncd) maintains in the background when the Pi has internet
>
> For long unattended sessions (mosaic projects spanning days; remote observatory), a USB GPS dongle costs ~$20 and removes all clock-drift concerns. Without one, expect FITS DATE-OBS timestamps to drift by ~1 s/hour if WILMA disconnects mid-session and the Pi has no internet.

**Why not bundle chrony or override timesyncd:**
- 95% of users image at home with internet → systemd-timesyncd default is fine
- Remote-observatory users typically already have GPS or PTP setups they prefer
- Bundling chrony would replace systemd-timesyncd (user trust + diagnostic familiarity cost)
- gpsd shared-memory ref-clock setup is a power-user workflow — wiki + DEPLOY.md document the path without ARA forcing the setup
- ARA stays out of the OS-level time-sync stack; respects what the user (or distribution) chose

**v0.1.0 reconsideration:** if remote-observatory users (the new v0.1.0 §67.4 remote-access mode) report systematic clock-drift issues, revisit. Possible v0.1.0 path: ARA optionally installs + configures chrony with gpsd ref-clock when the user opts into "observatory mode" during the wizard.

### 31.5 DST + timezone policy (explicit)

The Pi side is timezone-free; the client side handles DST automatically. Detailed policy:

| Surface | Timezone behavior |
|---|---|
| Server internal timestamps + SQLite writes + log timestamps | **UTC** always |
| FITS header `DATE-OBS` | UTC (ISO 8601 with `Z` suffix per FITS standard) |
| Filename template `$$DATE$$` / `$$DATETIME$$` | **UTC** (so file ordering is monotonic across DST transitions; users analyzing data across nights see consistent UTC timestamps) |
| Filename template `$$DATEMINUS12$$` | UTC minus 12h, then date — already TZ-correct for night-observation use (groups frames captured on either side of midnight UTC into the same observing-night folder for any longitude) |
| Astronomical twilight calculation | UTC + site lat/lng; **timezone-independent by construction** — dawn / dusk / astronomical-twilight times are computed from solar geometry, not from clock TZ |
| USB GPS time-sync | UTC (`$GPRMC` / `$GPGGA` sentences carry UTC by spec) |
| Manual time entry on WILMA | Client UI accepts local time, converts to UTC before sending |
| Client display (timestamps, schedules, "next exposure in") | **Local TZ** via Flutter `intl` package — handles DST transitions automatically |
| Pi system clock during DST transition | Not affected (Pi runs in UTC; OS-level DST is irrelevant) |

**Edge cases:**
- DST "spring forward" or "fall back" during an active sequence: server unaffected (UTC); client briefly displays the same local-time hour twice (fall back) or skips an hour (spring forward) — purely cosmetic, no sequence impact.
- User travels mid-session (laptop crosses TZ): WILMA picks up the new local TZ on relaunch; running session continues unaffected since all server math is UTC.
- Pi without RTC + no NTP + no GPS: per §31's existing waterfall, server enters `no_time_sync` mode and refuses time-dependent operations (twilight-based sequences, etc.) until a time source arrives.

No additional configuration is exposed to the user — DST + TZ are handled automatically.

---

## 32. Network resilience (WILMA ↔ Pi)

WILMA loses Wi-Fi mid-session. WebSocket dies. The sequence keeps running on the Pi regardless — disconnect ≠ pause. WILMA's job is to detect, communicate, and recover.

### 32.1 Disconnect detection

- WebSocket close event OR no pong within 60s (per §27.2)
- Client first attempts **silent reconnect for 5 seconds** (handles transient drops)
- After 5s of failed silent attempts, show the disconnect modal

### 32.2 Disconnect modal

```
┌──────────────────────────────────────────┐
│  ⚠ Disconnected from Ara Core            │
│                                          │
│  Your device lost connection. Check that │
│  you're still on the Ara Core Wi-Fi      │
│  network.                                │
│                                          │
│  [ Verify Network ]    [ Try Again ]     │
└──────────────────────────────────────────┘
```

### 32.3 [Verify Network] flow

1. Spinner: *"Searching for Ara Core..."*
2. Client runs mDNS scan for `_openastroara._tcp.local` + direct probe of last-known server IP/hostname (parallel)
3. Outcomes:
   - **Found + reachable** → *"Found Ara Core. Reconnecting..."* → re-open WebSocket → fetch server state snapshot → rehydrate UI → dismiss modal
   - **Not found** → *"Ara Core not found on this network. Make sure you're connected to the 'Ara Core' Wi-Fi network, then try again."* with [Verify Again]
   - **Found but unreachable** (mDNS resolves; ping/TCP fails) → *"Ara Core is on the network but not responding — it may have crashed or rebooted. Wait a moment and try again."*

### 32.4 During disconnect (no modal yet, 5s silent retry)

- Status bar shows a yellow indicator: *"Reconnecting..."*
- All mutating actions disabled
- Read-only views show last-known state (cached from prior WebSocket events)

### 32.5 Reconnect behavior

- Server is source of truth — client fetches `GET /api/v1/server/state` snapshot on reconnect and rehydrates UI
- Client-side in-flight mutations that didn't receive an HTTP response are **dropped** (v0.0.1) rather than retried — avoids double-execution risk
- Sequence keeps running on the Pi throughout — disconnect doesn't pause

### 32.6 Pi Wi-Fi mode (operational, not in scope for ARA Core .deb)

The Pi runs in one of two Wi-Fi modes, **configured outside ARA Core** per the [OpenAstro wiki](https://wiki.openastro.org):

- **AP mode (default for portable field use)** — Pi runs `hostapd`, creates network "Ara Core" (or whatever SSID user picks), WILMA devices connect to that
- **Client mode (indoor / observatory)** — Pi joins user's home Wi-Fi, accessible from any device on that network

Either way, the modal copy "Ara Core Wi-Fi network" works — it means whichever network the Pi is reachable on. ARA Core's .deb does **not** touch hostapd or Wi-Fi config — networking is user-managed per the wiki.

---

## 33. Version compatibility + WILMA-pushed updates (ASIAir model)

Update mechanism for ARA Core that doesn't require internet on the Pi. The WILMA app embeds the server binary as an asset and pushes it to the Pi over the local network. Field-friendly.

### 33.1 Embedded binary

- WILMA app bundles `linux-arm64` self-contained .NET 10 publish of `OpenAstroAra.Server`
- Trimmed + Native AOT to ~30-50 MB
- Stored at `assets/server/openastroara-server-linux-arm64.tar.gz`
- App build pipeline embeds this from CI before each release

### 33.2 Version check on every connection

Client embeds its own version (`OpenAstroAra.Client.AppInfo.version`). After WebSocket handshake:

```
GET /api/v1/server/info → { server_version: "0.0.1", api_version: "v1", protocol_minor: 3 }
```

Compare:

| Client vs Server | Action |
|---|---|
| Equal | Proceed normally |
| **Client newer** (semver) | Modal: *"Ara Core (v0.0.1) needs to update to match your app (v0.0.2). Update now?"* → [Update Ara Core] / [Cancel] |
| **Client older** | Modal: *"Your app (v0.0.1) is older than Ara Core (v0.0.2). Update via App Store / GitHub Releases — features may misbehave until you do."* → [Continue Anyway] / [Cancel] |
| API major mismatch | Hard block: *"This app cannot talk to Ara Core v1.0.0. Update your app to continue."* |

### 33.3 Update push flow

```
[Update Ara Core] clicked
     ↓
WILMA streams bundled tarball to POST /api/v1/server/update
   Headers: X-Update-Version, X-Update-Sha256
   Body: gzipped tarball, Content-Type: application/octet-stream
   (no auth per §67 — trusted LAN; SHA-256 verification is the integrity gate)
     ↓
Server:
  1. Validate version + sha256
  2. Save tarball to /opt/openastroara/staging/
  3. Verify checksum
  4. Extract to /opt/openastroara/staging/extracted/
  5. Pre-flight: run "new-binary --version" — must succeed in 5s
  6. Invoke /opt/openastroara/update.sh (privileged helper)
  7. Reply 202 Accepted, begin shutdown
     ↓
update.sh (run as root via NOPASSWD sudoers — see DEPLOY.md):
  - dpkg-divert old binary so APT respects local override
  - Atomic: mv current → previous; mv staging/extracted → current
  - systemctl restart openastroara-server
     ↓
New binary boots → smoke test (responds to /api/v1/server/info within 30s)
     ↓
   succeeds                          fails to start
     ↓                                      ↓
Client reconnects, versions match.    systemd watchdog triggers rollback:
Modal closes.                           mv previous → current; restart
                                       Client sees old version still; modal:
                                       "Update failed, rolled back."
```

### 33.4 Trust & integrity (v0.0.1)

- No auth on the endpoint per §67 (trusted-LAN posture); same as every other ARA endpoint
- **SHA-256 checksum match before swap** — the integrity gate. An attacker would need to upload a binary whose SHA-256 matches the one they declared, which requires already possessing a legitimate signed binary; mere LAN access isn't enough
- WILMA's UX requires the user to click [Update Ara Core] — opportunistic API access can't trigger an update silently
- **v0.1.0 addition**: Ed25519 signature verification with Open Astro's pinned public key (so the user can't push a tampered binary to their own Pi by accident or malice; provides strong integrity even on hostile networks once remote-access mode ships)

### 33.5 Coexistence with APT updates (per §34)

`update.sh` runs `dpkg-divert --add /opt/openastroara/OpenAstroAra.Server` so APT knows the binary is locally-overridden. On subsequent `apt upgrade`, the new APT version stages as a `.dpkg-new` file but does not replace the WILMA-pushed binary. User can manually clear the divert (`dpkg-divert --remove`) to return to APT-managed state.

### 33.6 v0.1.0 scope (noted, not implemented yet)

Same push-from-WILMA mechanism extended to:
- **AlpacaBridge** — bundled binary, `/opt/alpaca-bridge/`, restart via systemd
- **openastro-phd2** — same pattern, `/opt/openastro-phd2/`
- Endpoints: `POST /api/v1/server/components/{name}/update`
- Server detects component versions via the component's own status API (AlpacaBridge `/version`, PHD2 `get_app_state` JSON-RPC)

### 33.7 "What's new" — in-app changelog viewer

After any server version change (WILMA push per §33.3, apt upgrade per §34.7, or fresh install), WILMA surfaces a one-time modal showing release notes for the new version. Single discoverability surface for: new features the user might want to enable, breaking changes that affect existing workflows, safety-relevant fixes that change autonomous behavior, and the "where did X go?" questions that follow renames.

**Bundled CHANGELOG.md (Keep-a-Changelog format):**

ARA Core's repository root holds `CHANGELOG.md`. The .deb installs it at `/opt/openastroara/CHANGELOG.md` (read-only, owned by `root:openastroara`). Format follows [keepachangelog.com 1.1.0](https://keepachangelog.com/) — section headers per version, type subsections (Added / Changed / Deprecated / Removed / Fixed / Security).

Example structure:

```markdown
# Changelog

## [Unreleased]

## [0.0.1-ara.6] — 2026-06-01

### Added
- Smart Focus collimation detection (§59.4)
- Health check endpoints `/healthz` and `/readyz` (§60.8)

### Fixed
- PHD2 reconnect after USB hub sleep (§63.3)
- Mosaic RA-wrap math for panels crossing 0h (§47.3)

### Security
- AlpacaBridge version handshake now enforced (§68.1)

## [0.0.1-ara.5] — 2026-05-25

### Added
- Initial release of OpenAstroAra
...
```

Entry-writing discipline: every PR that ships a user-visible change adds a `### <Type>` line under `## [Unreleased]`. The release tag-cut process (Phase 15 + future releases) renames `## [Unreleased]` → `## [<version>] — <date>` and creates a fresh `## [Unreleased]` placeholder.

**Server-side endpoint** (already declared in §34.7):

`GET /api/v1/server/release-notes?version=<X.Y.Z>` — parses `/opt/openastroara/CHANGELOG.md`, extracts the section matching `<X.Y.Z>`, returns Markdown:

```json
{
  "version": "0.0.1-ara.6",
  "date": "2026-06-01",
  "markdown": "### Added\n- Smart Focus collimation detection (§59.4)\n...",
  "is_current": true
}
```

Optional query: `?since=<X.Y.Z>` returns all entries between `<since>` (exclusive) and current (inclusive), useful when the user skipped versions:

```json
{
  "current_version": "0.0.1-ara.6",
  "since_version": "0.0.1-ara.3",
  "versions": [
    { "version": "0.0.1-ara.6", "date": "...", "markdown": "..." },
    { "version": "0.0.1-ara.5", "date": "...", "markdown": "..." },
    { "version": "0.0.1-ara.4", "date": "...", "markdown": "..." }
  ]
}
```

404 with `code: "unknown_version"` if the version isn't in CHANGELOG.md (typically means manual install of an unofficial build).

**WILMA-side state + trigger:**

WILMA persists `last_seen_server_version` in `flutter_secure_storage`, keyed per-server (server UUID from `/api/v1/server/state`). On every connect:

1. Fetch current server version from `/api/v1/server/state.version`
2. Read `last_seen_server_version` for this server UUID
3. If `current > last_seen` (semver compare): fetch `/api/v1/server/release-notes?since=<last_seen>`
4. Render the modal (see below)
5. On user dismiss (close, confirm, "don't show again"): write `last_seen_server_version = current`
6. If `current == last_seen`: no modal
7. If `current < last_seen` (downgrade): no modal (rare — DEPLOY.md / DB schema concerns dominate)
8. If `last_seen` is unset (fresh WILMA / first connect to this server): no modal; silently set `last_seen = current` (don't dump the full history on first connect — would be overwhelming)

**Modal layout (Flutter):**

```
┌─────────────────────────────────────────────────────────┐
│ ✨ What's new in 0.0.1-ara.6                       [✕]  │
├─────────────────────────────────────────────────────────┤
│ Released June 1, 2026 — 7 days ago                      │
│                                                         │
│ ── Added ──────────────────────────────────────────     │
│ • Smart Focus collimation detection (§59.4)            │
│   → Settings → Equipment → Focuser → Collimation       │
│                                                         │
│ • Health check endpoints /healthz and /readyz (§60.8)  │
│                                                         │
│ ── Fixed ──────────────────────────────────────────     │
│ • PHD2 reconnect after USB hub sleep                    │
│                                                         │
│ ── Security ───────────────────────────────────────     │
│ • AlpacaBridge version handshake now enforced          │
│   → Equipment screen blocks if bridge < 1.2.0          │
│                                                         │
│                                                         │
│ [View full changelog]               [Don't show again]  │
│                                              [Close]    │
└─────────────────────────────────────────────────────────┘
```

Interactions:
- **[Close]** dismisses + writes `last_seen_server_version`. Modal won't re-appear next connect.
- **[Don't show again]** writes a permanent `suppress_changelog_modal = true` in user preferences. Future updates skip the modal entirely; the changelog remains accessible via Help → Release notes.
- **[View full changelog]** opens a scrollable side panel showing the entire CHANGELOG.md (all versions, not just the new ones).
- **§-references** in changelog markdown render as clickable deep-links into the appropriate Settings panel via §61's deep-link mechanism — clicking "§59.4" jumps to the Smart Focus collimation setting.

**Skipped-versions handling:**

If the user was on 0.0.1-ara.3 and now sees 0.0.1-ara.6 (missed .4 and .5), modal aggregates all three versions, sorted newest-first, each as a collapsible section:

```
✨ What's new — you missed 3 updates (.4 .5 .6)
[▼ 0.0.1-ara.6 — June 1] (expanded)
  ... entries ...
[▶ 0.0.1-ara.5 — May 25] (collapsed; click to expand)
[▶ 0.0.1-ara.4 — May 18] (collapsed)

[View full changelog] [Don't show again] [Close]
```

Caps at 20 versions; if user skipped more (extreme case), modal shows "Many updates skipped — see [View full changelog] for full history" and aggregates the most recent 20.

**Help → Release notes (always-available surface):**

WILMA's Help menu adds "Release notes" → opens the side panel with the full changelog. Always available regardless of update history. Selectable version dropdown + search box. Supports the [Don't show again]-pressed user who later wants to look something up.

**§61 search registry entries** (added in 12h Settings sub-PR):

- `app.changelog.view` — keywords: `changelog, release notes, what's new, version history, updates, what changed`
- `app.changelog.suppress` — keywords: `disable changelog popup, don't show what's new, stop release notes modal, hide version popup`

**§14.2 widget test cases (added):**

- `changelog_modal_shown_when_version_changes` — set last_seen=X, mock current=Y > X, assert modal renders with Y's content
- `changelog_modal_skipped_on_first_connect` — last_seen=null, current=Y, assert no modal but last_seen updated to Y
- `changelog_modal_aggregates_skipped_versions` — last_seen=A, current=D, mock release-notes?since=A returning B+C+D, assert all 3 sections rendered
- `dont_show_again_suppresses_future_modals` — set suppress flag, change version, assert no modal but Help → Release notes still works

**Release process integration:**

- The Phase 15 PR (final port → master) adds the initial CHANGELOG.md with `## [0.0.1-ara.1] — <date>` entry
- COMMIT-PR-RULES.md's per-phase PR rhythm (future bake) adds "update CHANGELOG.md [Unreleased] section" to the pre-PR gate (§14.4) for any PR with user-visible changes — same enforcement style as the settings-registry gate
- Release tags (manual, user-driven): rename `[Unreleased]` → `[<version>] — <date>`, create fresh `[Unreleased]`, push tag, GitHub Actions builds the .deb with the updated CHANGELOG.md
- Cross-ref with COMMIT-PR-RULES "Things still to decide" — release cadence post-v0.0.1 entry (future-scope section) inherits the changelog discipline

**Cross-references:**

- §33.3 — WILMA-push update flow triggers the post-update changelog modal
- §34.7 — apt-upgrade-deferred restart's [View release notes] button calls the same endpoint
- §30 — first-run flow's "fresh WILMA / first connect" path silently sets last_seen without modal
- §61 — deep-links from changelog §-refs use the search registry
- §0.5 — discoverability pillar reinforced (users discover new features they didn't know to look for)

---

## 34. Distribution + install (apt.openastro.net)

### 34.1 Primary install path

```bash
# 1. User flashes Trixie (Debian 13) on RPi 4/5, Orange Pi, or RockChip SBC — see OpenAstro wiki
# 2. User configures Wi-Fi or Ethernet — see wiki
# 3. Add the OpenAstro APT repo (one-time):
curl -fsSL https://apt.openastro.net/gpg.key | sudo gpg --dearmor -o /usr/share/keyrings/openastro.gpg
echo "deb [signed-by=/usr/share/keyrings/openastro.gpg] https://apt.openastro.net stable main" \
  | sudo tee /etc/apt/sources.list.d/openastro.list
sudo apt update

# 4. Install (single command, pulls everything via Recommends):
sudo apt install openastroara-server
```

### 34.2 Package details

- Name: `openastroara-server` (lowercase, hyphens per Debian convention)
- Arch: **arm64** (works on RPi 4/5, Orange Pi 5, RockChip SBCs — anywhere Debian-family + ARM64 runs)
- Depends: `libc6`, `libgcc-s1`, `libstdc++6`, runtime essentials
- Recommends: `alpaca-bridge`, `openastro-phd2` (pulled in by default; opt-out with `--no-install-recommends`)
- Suggests: `gpsd` (for USB GPS time sync per §31)

### 34.3 Post-install hooks (handled by .deb's postinst script)

- Creates `openastroara` user + group (system user, no shell)
- **Adds `openastroara` to standard device groups:** `usermod -aG dialout,video,plugdev openastroara`
  - `dialout` — USB serial devices (GPS receivers via gpsd per §31, FTDI dew heaters, simple USB switches)
  - `video` — V4L2 cameras + some vendor cameras that use kernel video interfaces
  - `plugdev` — USB hotplug events (cleaner device enumeration)
- Drops `/etc/systemd/system/openastroara-server.service` (hardened unit per §13)
- Drops `/usr/lib/tmpfiles.d/openastroara.conf` for `/var/run/openastroara/` (per §34.7 sequence lock)
- Sets `CAP_SYS_TIME` on the binary: `setcap cap_sys_time+ep /opt/openastroara/OpenAstroAra.Server`
- Installs `/opt/openastroara/scripts/configure-storage.sh` (mode 0750, owned by root:openastroara) — per §29.1.4
- Installs sudoers drop-in `/etc/sudoers.d/openastroara` (mode 0440, validated with `visudo -cf`):
  ```
  openastroara ALL=(root) NOPASSWD: /opt/openastroara/update.sh
  openastroara ALL=(root) NOPASSWD: /opt/openastroara/scripts/configure-storage.sh
  ```
- Creates data + log + config dirs at proper permissions
- Installs `/etc/logrotate.d/openastroara` per §29.9
- Enables + starts the service: `systemctl enable --now openastroara-server.service`

(No token generation per §67 — v0.0.1 has no auth. v0.1.0 remote-access mode adds a token-generation step at enable time.)

ARA Core's .deb does **only** these things. It does **not** touch:
- Wi-Fi or hostapd (per §32.6 — wiki handles this)
- OS install (wiki)
- Equipment driver configuration (camera-specific udev rules ship with AlpacaBridge's .deb per §68 + the vendor SDK packages it depends on; ARA Core stays vendor-agnostic)

**Device permission philosophy (per fourth-pass Tier 1 #6):** ARA Core ships NO vendor-specific udev rules in v0.0.1. The split of responsibilities:
- **AlpacaBridge** ships udev rules for the cameras it directly supports (or pulls them in via vendor-SDK .debs it depends on)
- **ARA Core** ensures its `openastroara` user is in the standard device groups so it inherits access to whatever AlpacaBridge or the kernel exposes
- **The user** can `sudo usermod -aG <group> openastroara` for non-standard devices; DEPLOY.md troubleshooting documents this

If a camera doesn't enumerate, the troubleshooting flow in DEPLOY.md (§34.6) walks the user through:
1. `apt list --installed alpaca-bridge` — confirm AlpacaBridge is installed
2. `ls -la /dev/bus/usb/*/* | grep <vendor>` — confirm device is visible
3. `groups openastroara` — confirm openastroara is in the expected groups
4. `journalctl -u openastroara-server -n 50` — check for permission errors
5. If still failing: file an issue per §54 bug report flow (includes group membership + udev rule snapshot in the zip)

### 34.4 Two update paths coexist

| Path | Internet required on Pi | Use case |
|---|---|---|
| **APT (primary)** | Yes | Home / observatory with internet — `sudo apt upgrade` |
| **WILMA push (§33)** | No | Field / offline — binary streamed from app |

Both coexist via `dpkg-divert` (§33.5). User can flip back to APT-managed state by clearing the divert.

### 34.5 Repo layout

```
apt.openastro.net/
├── dists/
│   └── stable/
│       ├── Release  Release.gpg  InRelease
│       └── main/
│           └── binary-arm64/
│               ├── Packages
│               └── Packages.gz
└── pool/
    └── main/
        ├── o/openastroara-server/
        │   └── openastroara-server_0.0.1-ara.1_arm64.deb
        ├── a/alpaca-bridge/
        │   └── alpaca-bridge_X.Y.Z_arm64.deb
        └── o/openastro-phd2/
            └── openastro-phd2_X.Y.Z_arm64.deb
```

GitHub Actions builds the .deb and publishes to the apt repo via `reprepro` (or `aptly`) on every `v0.0.1-*` tag push.

### 34.6 DEPLOY.md becomes lean

DEPLOY.md content:
1. Link to OpenAstro wiki for OS install + Wi-Fi/networking
2. The 4 commands from §34.1
3. How to connect WILMA (already covered in §30) — no token needed per §67
4. USB drive setup for FITS storage (default: automatic via WILMA per §29.7; manual fallback documented for advanced users)
5. USB GPS plug-in (auto-detected per §31, no config needed)
6. Security posture summary (one paragraph linking to §67): trusted-LAN, no auth, matches Alpaca + ASIAir
7. Recommended hardware sidebar (optional UPS per §28.10, USB SSD per §29)

That's it. No tarball install, no manual systemd setup, no manual user creation — all handled by the .deb.

### 34.7 `apt upgrade` mid-session safety — lock file + deferred-restart flag

**The risk:** unlike WILMA-pushed updates (§33), which are sequence-aware (server refuses restart while integrating), an `apt upgrade openastroara-server` runs the .deb postinst directly and restarts the daemon via `deb-systemd-invoke`. If a sequence is mid-exposure (e.g., 600 s integration), the restart kills the exposure with no warning. Real-world trigger: user runs unattended-upgrades, cron job, or a manual `apt upgrade` between two frames.

**The mechanism: server-managed lock file + postinst defer + WILMA-surfaced banner.**

**Server-side (writes the lock):**

- On sequence START (or transition to `paused`): server writes `/var/run/openastroara/sequence.lock` (mode 0644, owned by `openastroara:openastroara`). Content is a single line: `<sequence_id> <start_iso8601> <last_heartbeat_iso8601>`. Updated every 30 s while sequence is active.
- On sequence STOP / ABORT / COMPLETE: server removes the lock atomically.
- On server graceful shutdown (SIGTERM): server removes the lock as part of cleanup.
- On server crash: lock remains stale; server's startup routine (§28.2) detects + removes any lock whose `last_heartbeat` is > 5 min old before starting recovery.

`/var/run/openastroara/` is a tmpfs created at boot via `/usr/lib/tmpfiles.d/openastroara.conf` (shipped by .deb postinst):

```
d /var/run/openastroara 0755 openastroara openastroara - -
```

**postinst-side (reads the lock, defers restart):**

The .deb postinst's restart block runs:

```bash
LOCKFILE=/var/run/openastroara/sequence.lock
FLAGFILE=/var/lib/openastroara/.needs-restart

if [ -e "$LOCKFILE" ]; then
  # Verify lock isn't stale (heartbeat within 5 min)
  last_hb=$(awk '{print $3}' "$LOCKFILE")
  if [ -n "$last_hb" ] && [ "$(date -d "$last_hb" +%s 2>/dev/null)" -gt "$(date -d '5 minutes ago' +%s)" ]; then
    echo "openastroara: sequence active — deferring restart of openastroara-server" | systemd-cat -t openastroara-postinst -p warning
    touch "$FLAGFILE"
    chown openastroara:openastroara "$FLAGFILE"
    exit 0    # Successful postinst; new binary on disk, restart deferred
  fi
fi

# Lock absent or stale — safe to restart
deb-systemd-invoke restart openastroara-server.service
```

Net effect of a deferred-restart:
- New binary is in place on disk (the .deb has been unpacked)
- Old binary is still running (systemd hasn't been triggered to restart)
- `.needs-restart` flag is set
- Sequence continues uninterrupted on the old binary
- The new binary will take effect on the next restart (manual via WILMA, automatic at next reboot, or systemd-prompted)

**Server-side (surfaces the flag):**

On every WS handshake + on every `GET /api/v1/server/state` (§60.4), server checks for `/var/lib/openastroara/.needs-restart` and includes:

```json
{
  ...
  "pending_restart": {
    "reason": "package_update",
    "since_iso8601": "2026-05-23T19:14:02Z",
    "current_version": "0.0.1-ara.5",
    "pending_version": "0.0.1-ara.6",
    "release_notes_url": "/api/v1/server/release-notes?version=0.0.1-ara.6"
  }
}
```

`pending_version` is read from the .deb's status file (`dpkg-query -W -f='${Version}' openastroara-server`); `current_version` is the running binary's `--version` string.

**WILMA-side (banner + restart UX):**

Reuses the §30.7.3 banner shell (same component, different content). Shown on connect when `pending_restart` is non-null:

```
┌──────────────────────────────────────────────────────────────────┐
│ ℹ Server update pending — 0.0.1-ara.5 → 0.0.1-ara.6              │
│   A new server version was installed (apt upgrade) but the      │
│   restart was deferred because a sequence was active.            │
│   [Restart now]  [Restart when sequence ends]  [View release notes] │
└──────────────────────────────────────────────────────────────────┘
```

Buttons:
- **Restart now** — calls `POST /api/v1/server/restart {reason: "user_acknowledged_pending_update"}`. Server emits WS `server.restart_imminent` with 5 s warning, then exits cleanly. systemd's `Restart=on-failure` brings the new binary up. (If a sequence is still active, this button shows a confirmation dialog: "A sequence is currently running. Restart will pause + abort the in-flight exposure. Continue?")
- **Restart when sequence ends** — server sets `auto_restart_on_idle = true` in its in-memory state. After the next sequence ends (or if no sequence is running, immediately at the next 60 s idle tick), server self-restarts gracefully.
- **View release notes** — fetches `/api/v1/server/release-notes?version=<pending>`, shows a modal with the changelog (see §X.Y for the in-app changelog viewer — TODO, separate gap).

After successful restart, server boots on the new version, deletes `.needs-restart`, and WILMA's banner clears on next state poll.

**Edge cases:**

| Case | Behavior |
|---|---|
| Multiple apt upgrades stacked while deferred | `.needs-restart` flag is idempotent (just `touch`); `pending_version` always reflects the latest installed-but-not-running version. |
| Server crashed; lock is stale; postinst sees stale lock | Stale-detection (5 min heartbeat) prevents indefinite deferral. postinst restarts normally. |
| User runs `systemctl restart openastroara-server` manually while deferred | Restart goes through; new binary loads; `.needs-restart` is cleared on startup. |
| Sequence has been active > 24 hours (very long mosaic) | Banner shows continuously; server keeps deferring. No auto-force-restart — user agency wins. |
| WILMA never connects (headless / abandoned Pi) | New binary sits in `.dpkg-new` indefinitely; will activate on next reboot. Same outcome as if user simply never logged in. |
| postinst itself fails (e.g., disk full) | Standard dpkg failure path; user sees apt error; lock file untouched; running daemon unaffected. |

**Logging:**

- Postinst writes one journal line per deferral: `openastroara-postinst: sequence active — deferring restart of openastroara-server`
- Server logs `pending_restart_flag_set` (info level) on startup if the flag exists; logs `pending_restart_cleared` (info level) on successful restart with new version
- Bug-report zip (§54) includes `journalctl -t openastroara-postinst --since=30d` to surface deferral history

**API surface added:**

| Endpoint | Method | Purpose |
|---|---|---|
| `/api/v1/server/state` | GET | Existing — now includes `pending_restart` field |
| `/api/v1/server/restart` | POST | Immediate restart (with optional `reason` for telemetry). Returns 200 immediately; server schedules restart 5 s out |
| `/api/v1/server/restart-on-idle` | POST | Sets in-memory `auto_restart_on_idle=true`. Returns 200. Idempotent. |
| `/api/v1/server/release-notes` | GET | Query: `?version=<v>`. Returns bundled `CHANGELOG.md` section for that version (per the in-app changelog viewer gap — Tier 2). |

**WebSocket events added:**

| Event | Payload | When |
|---|---|---|
| `server.pending_restart` | `{current_version, pending_version, reason}` | Emitted once on WS connect if flag is set; emitted again when postinst sets the flag on an already-connected WILMA |
| `server.restart_imminent` | `{in_seconds, reason}` | 5 s before a planned graceful restart |

**§61 search registry entries** (added in 12h Settings sub-PR):

- `server.update.pending_restart_banner` — keywords: `pending update, restart server, deferred update, apt update, update banner`
- `server.update.restart_now` — keywords: `restart server, apply update, reboot daemon`
- `server.update.restart_on_idle` — keywords: `restart later, apply update at end, restart when sequence ends`

**§14.5 integration test cases (added):**

- `apt_upgrade_during_sequence_defers_restart` — simulate sequence active, run postinst, assert daemon PID unchanged + flag set + WS event fired
- `stale_lock_does_not_defer_restart` — lock file with old heartbeat, postinst restarts normally
- `pending_restart_clears_on_successful_restart` — flag set, user triggers restart, new binary starts, flag is gone

**v0.1.0 follow-ups:**

- WILMA UI for "Auto-restart at astronomical dawn" — for users who run long unattended sessions and want updates to apply between nights
- Telemetry on deferral patterns (per §67 local-only) to inform whether default behavior should change

---

## 35. Safety policies (user-configurable per profile)

ARA gives the user policy controls; the AI does not decide. Every safety reaction is set in the profile wizard or Settings → Safety. Sensible defaults pre-filled.

### 35.1 Configurable trigger → action matrix

| Trigger | Available actions | Threshold field |
|---|---|---|
| Cloud sensor unsafe (Alpaca SafetyMonitor or weather station) | Continue / Notify / Pause / Abort + park | — |
| Rain detected | Continue / Notify / Pause / Abort + park *(default abort)* | — |
| Wind speed | Continue / Notify / Pause / Abort + park | km/h or mph |
| Humidity | Continue / Notify / Pause / Abort + park | % |
| Dew point − ambient | Continue / Notify / Pause / Abort + park | °C delta |
| Generic SafetyMonitor `IsSafe = false` | Notify / Pause / Abort + park | — |
| Mount tracking error / lost guide star (>N seconds) | Continue / Pause / Abort + park | seconds |
| WILMA disconnected during a safety event | Wait for reconnect / Auto-abort after N min / Auto-abort immediately | minutes |
| Server unexpected restart | Auto-resume per §28 / Stop and wait for user | — |
| User unreachable (alarm unanswered after delay) | Loop alarm forever / Auto-abort after N min / Sequence continues | minutes |

Each trigger also has:
- **Audible alarm** toggle
- **Alarm delay** (seconds of silent popup before audio starts; default 5s)
- **Alarm sound** (pick from 3-4 bundled tones)
- **Vibration** (mobile only)

### 35.2 Sensible defaults (pre-fill in wizard)

| Event | Default |
|---|---|
| Cloud | Pause |
| Rain | Abort + park |
| Wind > 30 km/h | Pause |
| Humidity > 95% | Notify |
| Dew within 2°C | Notify |
| Generic IsSafe = false | Pause + alert |
| WILMA disconnected + unsafe condition | Auto-abort after 5 min |
| User unreachable | Continue alarm |
| Alarm | On, 5s delay, default tone |

User can override every single default.

### 35.3 User-triggered emergency abort

Persistent **[Emergency Stop]** button in WILMA's main app shell (top-right of status bar). Single tap = immediate abort, no confirmation (the button IS the confirmation). Fires `POST /api/v1/server/emergency-stop`:

Server shutdown sequence:
1. Camera: abort current exposure
2. Guider (PHD2): stop guiding
3. Mount: park (or home if no park position configured)
4. Filter wheel / focuser / rotator: leave in place (no auto-move; user can intervene)
5. Flat panel: turn off (if connected, and configured to do so)
6. Sequence state → `aborted`
7. WebSocket event `sequence.aborted` to all connected clients

### 35.4 SafetyMonitor poll loop (server-side)

- Server polls `IsSafe` on every connected `SafetyMonitor` device every 10 seconds (configurable)
- Subscribes to weather-station Alpaca events if available (push instead of poll)
- On transition to unsafe → triggers profile-configured action
- WebSocket event: `safety.unsafe` with details, fired before action so client can display alarm modal

### 35.5 Audible alarm asset

Bundle small audio in WILMA app: `assets/audio/safety_alarm.wav` (~30s loop, ~50 KB), plus 3-4 alternative tones. Use `audioplayers` Flutter plugin. Volume forced to max during alert. Vibration on mobile via `vibration` plugin.

### 35.6 API

Profile schema gains a `safety_policy` object:
```json
{
  "cloud":     { "action": "pause" },
  "rain":      { "action": "abort" },
  "wind":      { "action": "pause",  "threshold_kph": 30 },
  "humidity":  { "action": "notify", "threshold_pct": 95 },
  "dew_delta": { "action": "notify", "threshold_c": 2 },
  "is_safe_generic": { "action": "pause" },
  "tracking_error":  { "action": "pause", "threshold_seconds": 60 },
  "wilma_disconnect_during_unsafe": { "action": "abort", "timeout_min": 5 },
  "server_restart":  { "action": "auto_resume" },
  "user_unreachable": { "action": "alarm" },
  "alarm": { "enabled": true, "delay_sec": 5, "sound": "default", "vibrate": true }
}
```

Server reads this at session start and behaves accordingly.

---

## 36. Sky imagery + Data Manager (WILMA)

WILMA owns the sky atlas (per §2 responsibility split). ARA ships a **slim ~50 MB installer** and uses an in-app **Data Manager** for everything else, modeled on SkySafari's catalog-download pattern. The full feature surface ships in v0.0.1 — Tonight's Sky planetarium, all 21 surveys, full DE440 ephemerides, comet motion trails, star-catalog supplements — but as opt-in downloads rather than bundled assets. Users complete the mandatory wizard (§37) on first run; the wizard suggests downloads keyed to the user's focal length before any imaging begins. Aladin Lite is the differentiator vs NINA — by giving users the full atlas with on-demand depth, ARA delivers a sky-data experience NINA cannot match.

### 36.1 Installer base + Data Manager pattern

**Installer base (~50 MB, shipped with WILMA):**

| Bundle | Approximate size | Purpose |
|---|---|---|
| Aladin Lite WebView + JS bundle | ~5 MB | Sky atlas renderer |
| HYG star database (~120k Hipparcos stars) | ~10 MB | Naked-eye + binocular-class stars; minimum needed for Aladin to overlay something useful |
| DSO catalogs: NGC + IC + Caldwell + Messier + Sharpless + Abell + UGC | ~30 MB | All DSO targeting; searchable + clickable in Aladin |
| Common name resolver index (~50-100k entries) | ~5-10 MB | Offline universal search |
| MPC comet snapshot (`CometEls.txt`) | ~5 MB | Bundled at app build time; user-refreshable later |
| Constellation art (Urania's Mirror or modern art) | ~5 MB | Beautiful overlay at low zoom |
| Bundled audio (safety alarms per §35) | ~200 KB | — |
| **Total installer base** | **~50-60 MB** | App opens, Aladin runs, search works, all DSO catalogs browsable; sky imagery is blank-with-grid until downloads complete |

**Data Manager downloads (opt-in, sized per content):**

| Content | Size range | Tab |
|---|---|---|
| 21 HiPS surveys (per-survey HEALPix depth controls) | 4 GB – 290 GB each | Sky Imagery |
| Tycho-2 brightest subset (~2.5M stars, packed binary) | ~30-50 MB | Star Catalogs |
| GAIA DR3 brightest subset (~10M stars, packed binary) | ~80-100 MB | Star Catalogs |
| UCAC4 / HD / HIP / Bayer + Flamsteed extensions | ~5-25 MB each | Star Catalogs |
| Pre-baked DSO target thumbnails (~500 famous targets) | ~150 MB pack or per-target | Target Thumbnails |
| Full DE440 ephemerides (sub-milliarcsec planet positions) | ~50 MB | Solar System |
| Nebula contour vectors | ~20 MB | Sky Imagery (extras) |

**Why slim+downloader instead of fat install:** Bundling everything (~1 GB) inflates the installer and forces every user to carry data they may not need (e.g., X-ray surveys for visual-only users, deep narrowband surveys for someone shooting only broadband). The Data Manager pattern matches user expectations from SkySafari, Stellarium Mobile, and modern atlases. ARA's differentiator from NINA is **letting the user customize what their atlas knows** — not pre-deciding for them. There is no truncated/lite variant of any asset; the Data Manager always offers the full version.

**Empty-download fallback:** If the user skips all downloads in the wizard, Aladin shows a blank-with-grid view + bundled DSO catalogs + working search. Framing assistant is degraded (no imagery for FOV preview) but plate-solving still works for capture. Tonight's Sky shows horizon + currently-tracked-target + sun/moon (approximate, from a minimal analytical formula); planet positions are hidden until Full DE440 is downloaded. Pure-offline first-night use is supported; quality improves as the user downloads more.

### 36.2 Data Manager UI (unified, tabbed)

Settings → Sky Atlas Data → Data Manager. Four tabs, each fronting an on-demand downloader. Also reachable from the wizard's Sky Imagery screen (§37.6) and from the §30.7-pattern sky-data-missing banner (§36.13).

```
┌─────────────────────────────────────────────────────────────┐
│  Data Manager                       412 GB used / 1.2 TB     │
│                                                              │
│  [ Sky Imagery ▼ ] [ Star Catalogs ] [ Thumbnails ] [ Solar Sys ] │
│                                                              │
│  ...contents of selected tab...                              │
└─────────────────────────────────────────────────────────────┘
```

#### 36.2.1 Tab — Sky Imagery

All 21 HiPS surveys, grouped by wavelength:

```
┌────────────────────────────────────────────────────────────┐
│  Optical (broadband)                                        │
│  ☑ DSS2 (color)             order 8, 47 GB        [Update] │
│  ☐ DSS2 blue                not downloaded, ~30 GB [Download]│
│  ☐ DSS2 red                 not downloaded, ~30 GB [Download]│
│  ☑ Mellinger (color)        order 6, 4 GB          [Update]│
│  ☐ SDSS9                    not downloaded, ~120 GB[Download]│
│  ☐ PanSTARRS DR1 color      not downloaded, ~280 GB[Download]│
│  ☐ DECaPS DR2               not downloaded, ~150 GB[Download]│
│  ☐ DESI Legacy DR10         not downloaded, ~290 GB[Download]│
│                                                             │
│  Hα                                                         │
│  ☑ Finkbeiner Hα            order 7, 8 GB          [Update]│
│  ☐ VTSS Hα                  not downloaded, ~6 GB  [Download]│
│                                                             │
│  Infrared                                                   │
│  ☑ 2MASS (J+H+K)            order 8, 38 GB         [Update]│
│  ☐ GLIMPSE360               not downloaded, ~52 GB [Download]│
│  ☐ Spitzer                  not downloaded, ~58 GB [Download]│
│  ☐ allWISE                  not downloaded, ~64 GB [Download]│
│  ☐ IRIS                     not downloaded, ~7 GB  [Download]│
│  ☐ AKARI FIS                not downloaded, ~14 GB [Download]│
│                                                             │
│  Ultraviolet                                                │
│  ☐ GALEX GR6/7              not downloaded, ~16 GB [Download]│
│                                                             │
│  X-ray                                                      │
│  ☐ eROSITA DR1              not downloaded, ~8 GB  [Download]│
│  ☐ XMM-Newton (PN)          not downloaded, ~7 GB  [Download]│
│  ☐ Chandra                  not downloaded, ~5 GB  [Download]│
│                                                             │
│  Gamma-ray                                                  │
│  ☐ Fermi                    not downloaded, ~3 GB  [Download]│
│                                                             │
│  Extras                                                     │
│  ☐ Nebula contour vectors   not downloaded, ~20 MB [Download]│
│                                                             │
│  [Download Preset ▼]  [Clear All]                          │
└────────────────────────────────────────────────────────────┘
```

#### 36.2.2 Tab — Star Catalogs

SkySafari-style on-demand catalog downloader. Bundled HYG covers the baseline (~120k Hipparcos stars); supplementary catalogs add density and naming depth:

| Catalog | Approximate size | Purpose |
|---|---|---|
| Tycho-2 brightest subset (~2.5M stars, packed binary) | ~30-50 MB | Smooth rendering at all zooms |
| GAIA DR3 brightest subset (~10M stars, packed binary) | ~80-100 MB | High-density backdrops, dim-star detail |
| UCAC4 brightest | ~15-25 MB | Astrometric reference, common for finder workflows |
| HD designation index | ~5 MB | Henry Draper number lookups + display |
| HIP designation index | ~3 MB | Hipparcos catalog cross-reference |
| Bayer + Flamsteed extensions | ~2 MB | Greek-letter + Flamsteed-number labels in constellations |

Each catalog: integrity verified post-download via SHA-256 against the build manifest, ingestion progress visible, [Remove] to free space.

#### 36.2.3 Tab — Target Thumbnails

Pre-baked Aladin-quality previews for famous DSO targets (helpful for offline framing-preview UX):

- **Famous Targets Pack** — ~500 popular DSOs (Messier + Caldwell + select NGC), one click ~150 MB. Default-recommended in the wizard for any rig.
- **Per-target download** — search for a target, [Download Preview] for that one object. Useful for niche / obscure targets the famous pack doesn't include.

When a thumbnail exists locally, framing assistant uses it. When absent, framing assistant falls back to Aladin's live HiPS render (requires Sky Imagery downloads or online CDS access).

#### 36.2.4 Tab — Solar System

| Asset | Size | Purpose |
|---|---|---|
| **Full DE440 ephemerides** | ~50 MB | Sub-milliarcsecond Sun/Moon/planet positions. Required for accurate Tonight's Sky planetarium planet rendering, comet motion-trail precision, occultation events. Without it, planetarium falls back to a minimal analytical formula for Sun/Moon only; planets are not shown. **Default-recommended in the wizard for any rig.** |
| MPC asteroid catalog (bulk) | placeholder, v0.1.0 | Bulk asteroid layer (~1.4M numbered) — deferred to v0.1.0 per §36.8 |

### 36.3 Per-asset controls

Applies across all four tabs:

- Surveys: choose HEALPix resolution depth (e.g., order 4 = ~6 GB DSS2 color; order 8 = ~47 GB)
- Download / Pause / Resume / Cancel / Remove
- Verify integrity — SHA-256 against CDS manifest for HiPS tiles; SHA-256 against ARA's build manifest for star catalogs, thumbnails, and DE440
- Storage location (default app data dir; user can point at external drive on desktop)
- Download history + size accounting per tab + global "size used" header

### 36.4 Presets

- **"Famous Targets + Star Catalogs"** — Famous Targets Pack + Tycho-2 + GAIA brightest + UCAC4 + Full DE440 (~350 MB). **Default-recommended in the wizard for every rig** — small, fast, useful regardless of focal length.
- **"Optical only"** — DSS2 color + Mellinger + Finkbeiner Hα (~60 GB)
- **"All-wavelength essentials"** — one survey per band (~150 GB)
- **"Everything full resolution"** — ~2 TB. Confirmation gate. For real users with real storage.

### 36.5 Politeness considerations (CDS bandwidth)

- Parallel tile fetcher with per-CDS-host rate limiting (default 8 parallel connections, user-configurable)
- README + Data Manager explainer notes: CDS infrastructure is shared by astronomers worldwide. Download "Everything full res" only when you actually need it, preferably overnight.
- Implement HTTP `If-Modified-Since` so updates only fetch changed tiles
- Star catalogs, thumbnails, and DE440 are served from ARA's own release-asset hosting (GitHub Releases or similar; v0.0.1 picks a host, v0.1.0 may move to a CDN). Same politeness — parallel cap + `If-Modified-Since` — applies. These are static per-release assets, not live CDS pulls.

### 36.6 Local asset serving to Aladin Lite

Once an asset is downloaded:
- HiPS tiles stored at `<wilma data>/hips/<survey-id>/Norder<n>/Dir<m>/Npix<k>.jpg` (standard HiPS layout)
- Star catalogs stored at `<wilma data>/catalogs/<catalog-id>/...`
- Thumbnails stored at `<wilma data>/thumbnails/<target-id>.jpg`
- DE440 stored at `<wilma data>/ephemerides/de440.bsp`
- WILMA runs an embedded local HTTP server (Dart `shelf` package) on a random localhost port, serving from the data dirs via path prefixes (`/hips/...`, `/catalogs/...`, `/thumbnails/...`, `/ephemerides/...`)
- Aladin Lite's `hipsUrl` config points at the local server when WILMA is offline OR when the user prefers local
- Online + survey not downloaded → falls back to CDS

### 36.7 Tonight's Sky (planetarium mode)

The Sky Atlas tab has a sub-mode toggle: **[Catalog View]** ↔ **[Tonight's Sky]**.

**Tonight's Sky implementation:**
- Aladin Lite driven by WILMA — view centered programmatically on current zenith RA/Dec, stereographic projection
- WILMA computes (Dart, using inherited Astrometry library):
  - Current zenith RA/Dec from profile lat/long + UTC
  - Alt/az for every bundled catalog object
  - Horizon great-circle in equatorial coordinates
- WILMA pushes overlays into Aladin via JS bridge:
  - **Horizon polyline** as a catalog
  - **Cardinal direction markers** (N/E/S/W) at the horizon edge
  - **Below-horizon shading** (darken half-sky via custom overlay)
  - **Solar system bodies** — Sun, Moon (with phase glyph), 8 planets, computed from Full DE440, fed as a custom Aladin catalog updated every 60s. If Full DE440 is not downloaded (Data Manager → Solar System per §36.2.4), planets are omitted; Sun/Moon fall back to a minimal analytical formula with reduced accuracy. The §36.13 banner surfaces this state until the user downloads.
  - **Comets** — visible comets from bundled MPC snapshot, with motion trails for next N days (trail precision improves with Full DE440 downloaded)
  - **Currently-tracked target** — highlighted if user has one picked
- Time slider — scrub forward/backward. Each frame recomputes. "Now" button snaps to current real time. Auto-advance every 60s by default.
- Object filtering — catalog browser shows only objects above the horizon (or user-configurable altitude limit) at the current time. "Best transit tonight" sort option.

### 36.8 Universal search

Search bar at the top of the Sky Atlas tab:

- **Online** (WILMA has internet): query Aladin's Simbad integration. Type "wolf" → resolves to Wolf 359, Wolf-Rayet stars, candidate matches. Type "M31" / "NGC 6188" / "Andromeda Galaxy" / coordinates → resolves.
- **Offline**: fall back to bundled name resolver index (HYG common names + NGC/IC/M/HD/HIP/Tycho-2 designations + Bayer/Flamsteed + bundled comets). ~5-10 MB index, ~50-100k entries.
- **Coordinate parsing**: accept RA/Dec strings in multiple formats (HH:MM:SS / decimal degrees / mixed).
- **Comets**: searchable by designation (`C/2023 A3`) or common name (`Tsuchinshan-ATLAS`).
- **Asteroids** (v0.0.1): targeted lookup only (type "Ceres", "(1) Ceres", "433 Eros" → WILMA fetches that single object from MPC on demand). Bulk asteroid catalog deferred to v0.1.0.

### 36.9 Comet support

- Bundled `CometEls.txt` snapshot at app build time (~5 MB, ~5,000 comets)
- WILMA computes positions from Keplerian elements (a, e, i, Ω, ω, M, T) in Dart — ~500 lines, well-documented math
- Fed to Aladin as a catalog with custom marker (comet glyph + name + magnitude)
- **"Refresh comet data"** button (Data Manager → Sky Imagery → Extras) pulls latest `CometEls.txt` from MPC (requires WILMA internet, ~5 MB download, seconds)
- **Motion trail** option — shows comet's path over next 7/30/90 days as polyline overlay

### 36.10 Recommended downloads based on telescope (wizard hint)

In the wizard's Telescope screen (Screen 4 per §37.3), after the user enters focal length, the wizard later assembles a recommendation set for the Sky data downloads screen (Screen 17 per §37.6).

**Always recommended** (default-checked regardless of rig):
- Famous Targets + Star Catalogs preset per §36.4 (~350 MB)

**Recommended for the rig's focal length:**
- **Short (< 500mm)**: "Optical essentials" — DSS2 color + Mellinger + Finkbeiner Hα (~60 GB)
- **Medium (500-1500mm)**: "Optical + Infrared essentials" — adds 2MASS (~110 GB)
- **Long (> 1500mm)**: "All-wavelength essentials" — one survey per band (~150 GB) + deeper PanSTARRS or DECaPS if storage allows

Recommendations are *suggestions*, not enforced. User can accept, customize, or skip entirely. All downloads are opt-in; ARA initiates nothing without user [OK]. The Data Manager (§36.2) is always reachable from Settings for adjustments later.

### 36.10.5 Linux desktop WebView fallback (Aladin)

Flutter's desktop WebView story on Linux is patchy across packages (`webview_flutter`, `flutter_inappwebview`, `desktop_webview_window`). ARA uses `webview_flutter` (Google-official, best long-term support) for the embedded Sky Atlas WebView, but a fallback path exists for Linux desktops where the WebView fails to initialize.

**Phase 11 verification step:** before declaring Phase 11 complete, the AI runs `flutter build linux --release` and exercises the Sky Atlas tab. If the WebView loads Aladin Lite + renders the bundled DSO catalog overlay, primary path is green. If it fails (missing system library, package incompatibility, etc.), the fallback path activates.

**Fallback UX when WebView fails on Linux desktop:**

```
┌────────────────────────────────────────────────────────────┐
│  Sky Atlas — WebView unavailable on your Linux desktop      │
│  ──────────────────────────────────────────────────────────  │
│  Your Linux desktop is missing a WebView component ARA       │
│  needs to render Aladin Lite inline. You can still use the   │
│  Sky Atlas in your browser — ARA serves it from this device.  │
│                                                              │
│  [ Open Sky Atlas in your browser ]                          │
│  [ Show URL to open manually ] (for copy-paste)              │
│                                                              │
│  [ Help: install WebView dependencies ] (links to wiki)      │
└────────────────────────────────────────────────────────────┘
```

**Technical details:**
- The same Dart `shelf` HTTP server that serves downloaded HiPS tiles (§36.6) also serves a minimal HTML wrapper that loads the bundled Aladin Lite JS + WILMA's downloaded catalogs + tiles
- URL: `http://localhost:<port>/aladin/` where `<port>` is the random localhost port WILMA allocated at startup
- [Open Sky Atlas in your browser] uses Flutter's `url_launcher` package (cross-platform) — invokes the user's default browser
- [Show URL to open manually] copies the URL to clipboard + displays it on screen (for users on minimal desktops without a default browser configured)
- **Offline behavior is unchanged** — the localhost server works with no internet as long as the Aladin Lite JS bundle is bundled (which it is, per §36.1 installer base ~5 MB) and surveys are downloaded via §36.2 Data Manager. If no surveys are downloaded AND no internet, Aladin shows the same blank-with-grid + bundled DSO catalog overlays state the embedded WebView would have shown
- Bundled Aladin Lite JS + CSS lives at `/opt/openastroara/aladin/` (or the equivalent on Mac/Windows)

**Mobile + Mac + Windows + iOS + Android:** always use the embedded WebView (those platforms have robust WebView support).  The fallback path is Linux-desktop only.

**Help-wiki entry:** the OpenAstro wiki documents the Linux WebView dependency install steps (typically `sudo apt install libwebkit2gtk-4.0-dev libgtk-3-dev` or equivalent per distro). If the user installs the missing deps + restarts WILMA, the embedded WebView typically works on the next launch.

**v0.1.0 path:** the §55.2 v0.2.0 "Native Flutter sky-renderer" entry (replace Aladin WebView with pure-Flutter Skia rendering) eliminates this Linux-only issue entirely — but that's a substantial v0.2.0 design pass.

### 36.11 Aladin Lite license requirements

- Aladin Lite v3 is **GPL v3**; ARA is MPL 2.0
- These mix because the WebView is a separate process boundary — Aladin JS and Dart code communicate via `postMessage`, not statically linked. GPL FAQ explicitly permits this pattern.
- **Required by Aladin license**: keep Aladin logo + link visible bottom-right of the view. Don't strip the attribution.
- Credit in NOTICE.md: "Sky Atlas rendering powered by [Aladin Lite](https://aladin.cds.unistra.fr/) (CDS, Strasbourg) under GPL v3."

### 36.12 Wizard integration

The mandatory wizard (§37) gates initial downloads:

- **Wizard is the only profile-creation path** — users cannot use ARA without completing it at least once. See §37 preamble + §30.4 for the no-bypass policy.
- **Stage 6 (Sky data downloads, Screen 17 per §37.6)** is where downloads are offered. The screen shows:
  - What's currently downloaded (initially: ~50 MB installer base, no downloads yet)
  - Recommended set per §36.10 (default-checked, based on the focal length entered in Screen 4)
  - "Famous Targets + Star Catalogs" preset (default-checked, always recommended)
  - Full DE440 (default-checked, always recommended)
  - Total size estimate that updates as user toggles checkboxes
  - [Open Data Manager for full control] → opens §36.2 inline (returns to wizard on close)
  - [Start downloads + continue] → kicks off background downloads, proceeds to Stage 7
  - [Skip — set up later in Data Manager] → proceeds with no downloads; §36.13 silent banner appears in main app
- **Downloads execute in the background** — wizard proceeds to Stage 7 (Review + Save) while downloads run. Progress shows in the main app shell after wizard completion.
- **Offline-at-wizard case** — if WILMA has no internet at this step, the screen switches to "Internet unavailable — we'll download when you're online." Recommended set is recorded in the profile; downloads queue silently and execute when connectivity returns. The §36.13 banner surfaces if any download is still missing after first connectivity restoration.

### 36.13 Profile-creation verification (silent banner)

On profile load, the server compares the profile's recommended download set (computed per §36.10 from focal length) against the actual Data Manager state. If recommended assets are missing, a **silent banner** appears at the top of the main app shell:

```
ℹ For your 2000mm rig, we recommend downloading PanSTARRS DR1 and the
  Famous Targets Pack for better framing-preview quality. [Open Data Manager] [Dismiss]
```

Same pattern as §30.7.3's equipment-change banner. Non-blocking. Dismissible. Reappears after a configurable cool-down (default 7 days) until the user either downloads or marks "don't suggest again for this profile."

**Triggers:**
- Profile created with skipped downloads → banner appears immediately on first main-app entry
- Profile rig changes (per §30.7) → if new focal length recommends additional assets, banner re-evaluates
- New surveys added in a future ARA release → banner alerts existing profiles if the new asset matches their rig

**Storage schema additions** (added to the profile's `calibration_state` block per §30.7.4):

```json
{
  "sky_data_state": {
    "recommended_set_for_profile": ["dss2_color_o6", "mellinger_o5", "finkbeiner_ha_o6", "famous_targets_pack", "tycho2", "gaia_dr3_brightest", "de440_full"],
    "downloaded_set": ["dss2_color_o4"],
    "last_banner_dismissed_at": "...",
    "dont_suggest_again": false
  }
}
```

**API + WebSocket:**

| Method | Path | Purpose |
|---|---|---|
| `GET` | `/api/v1/data-manager/state` | Lists all downloaded assets by tab + sizes + integrity status |
| `POST` | `/api/v1/data-manager/downloads` | Queue one or more downloads (body: `{ ids: [...] }`) |
| `DELETE` | `/api/v1/data-manager/downloads/{id}` | Cancel an active download |
| `DELETE` | `/api/v1/data-manager/assets/{id}` | Remove a downloaded asset |
| `GET` | `/api/v1/profiles/{id}/sky-data-recommendations` | Returns recommended set for the profile's rig |
| `POST` | `/api/v1/profiles/{id}/sky-data-banner/dismiss` | Marks banner dismissed; optional `dont_suggest_again` body flag |

```json
{ "type": "data_manager.download.progress", "payload": { "asset_id": "panstarrs_dr1_o6", "bytes_done": 12345678, "bytes_total": 280000000000, "eta_seconds": 3640 } }
{ "type": "data_manager.download.complete", "payload": { "asset_id": "panstarrs_dr1_o6" } }
{ "type": "data_manager.download.failed", "payload": { "asset_id": "panstarrs_dr1_o6", "error": "...", "retry_after_seconds": 60 } }
```

### 36.14 §61 search registry

- `data_manager.open` — keywords: `download surveys, sky imagery, star catalogs, hips, dss2, panstarrs, gaia, tycho, ucac, thumbnails, de440, ephemerides, data manager, downloads`
- `data_manager.recommendations` — keywords: `recommended downloads, what surveys do i need, focal length recommendations`
- `data_manager.banner_dismiss` — keywords: `sky data banner, hide download suggestions, don't suggest again`

---

## 37. Profile setup wizard

The wizard is **mandatory and the only profile-creation path**. Users cannot use ARA without at least one profile, and the wizard is the only mechanism for creating one — there is no quick "Add a Profile" modal. Every [+ Add a Profile] action (from §30) and every "Run Wizard Again" action launches the full wizard flow described below. The wizard walks the user through every essential configuration with sensible defaults and per-screen [Skip — use defaults]. Each screen also has [< Back] and [Next >]. Progress bar at top: "Step X of N." User can [Save & Exit Wizard] at any point — profile saves with what's been configured, defaults for the rest. The "save partial state and exit" path still counts as wizard completion for the purpose of the no-bypass policy; defaults fill anything the user skipped.

This design enforces ARA's §0.5 pillar 3 (discoverable + safe by default): every user-facing setting flows through one canonical setup path, so recommended downloads (§36.12), equipment signatures (§30.7.1), safety policies (§35), and site location all get captured consistently for every profile.

### 37.1 Stage 1 — Profile basics

**Screen 1 — Profile name + location**

- Profile name (required)
- Site latitude / longitude / altitude (optional)
  - "Use device GPS" button (WILMA mobile) — `geolocator` Flutter plugin
  - Manual entry alternative
  - "Skip — set later" fallback
- Site name (optional, e.g., "Backyard Texas", "Bortle 4 site")
- Timezone (auto-detect from location, or pick from list)

### 37.2 Stage 2 — Equipment discovery

**Screen 2 — Connect to AlpacaBridge**

- Field: AlpacaBridge address (default: auto-discover via Alpaca's broadcast UDP on port 32227)
- "Test Connection" button — server pings AlpacaBridge, shows results
- Protocol is **ASCOM Alpaca only** — permanent architectural commitment per §52. INDI/INDIGO users use bridges (AlpacaPi, INDIGO Sky's `-A` Alpaca server). No "future native INDI" placeholder in the UI; the wizard screen explains the bridge path in a tooltip.

**Screen 3 — Discover + assign equipment**

Server enumerates Alpaca devices, groups by type. User assigns each device-type slot (or leaves "— None"):
- Camera
- Filter Wheel
- Focuser
- Mount (Telescope)
- Rotator
- Dome
- Observing Conditions (weather)
- Switch
- Safety Monitor
- Flat Panel
- Guider (PHD2 — server reaches out to PHD2's JSON-RPC, not Alpaca)

### 37.3 Stage 3 — Per-device setup (one screen per connected slot; skipped if "— None")

**Screen 4 — Telescope**

- Telescope name (free text, e.g., "ES ED102")
- Focal length (mm) — required
- Aperture (mm) — required
- Focal ratio — auto-computed but editable
- **Aladin survey recommendation** appears here based on focal length (per §36.10) — user can [Download recommended] or [Skip — configure in Sky Imagery later]

**Screen 5 — Camera**

- Cooling target temperature (°C, default −10° or "ambient minus 30°")
- Cooler ramp rate (°C/min, default 1°C/min)
- **Cooler warmup at session end** (per §28.13): [off (default)] / [ramp at 1°C/min] / [immediate]
- Default gain
- Default offset
- Default bin
- Pixel size (mm) — auto-filled from Alpaca, editable
- Image scale computed and displayed: "1.49 arcsec/pixel — wide-field DSO" (or similar)

**Screen 6 — Filter Wheel**

- For each slot detected by Alpaca:
  - Name (L / R / G / B / Hα / OIII / SII / Clear / etc.)
  - Type (broadband / narrowband / clear / luminance)
  - Wavelength (nm) — optional metadata
  - Focus offset (steps) — left blank; populated automatically by first autofocus run per §28.5

**Screen 7 — Focuser**

- Step size (microns/step) — pulled from Alpaca if reported
- Backlash compensation: in / out steps
- Temperature compensation toggle + slope (steps/°C, defaults to 0 = disabled)
- Max travel — pulled from Alpaca

**Screen 8 — Mount**

- Mount name — auto-pulled from Alpaca driver
- Slew rate (deg/sec)
- Park position: [Sync to current pointing] / [Define manually]
- Meridian flip behavior: Auto / Prompt / Never
- **Settle time after slew** — auto-pulled from Alpaca driver's `SlewSettleTime` property (per user's spec), editable

**Screen 9 — Rotator** (if connected)

- Mechanical limits (min/max angle)
- Angle step size
- Reverse direction toggle

**Screen 10 — Guider (PHD2)**

- Host:port (default `localhost:4400`)
- Dither pixels (default 5 px)
- Settle threshold (default 1.5 px for 10s)
- Calibration cadence: [Each session] / [Once, then reuse] / [Never recalibrate]

### 37.4 Stage 4 — Imaging tools

**Screen 11 — Plate solving (ASTAP)**

- ASTAP binary path — auto-detect per OS (Linux: `which astap`; macOS: `/Applications/ASTAP.app/...`; Windows: `%PROGRAMFILES%\astap\astap.exe`), editable
- Star database path — browse, recommend external/USB drive
- Search radius (deg, default 30)
- Downsample factor (default 2)
- Test button: "Solve a test image" — feeds a bundled known image, verifies ASTAP works

**Screen 12 — Autofocus**

- Exposure time (default 5s)
- Step size (microns)
- Max retries (default 3)
- "Auto-discover filter offsets" toggle (default on — first AF run per filter populates filter wheel offset)

**Screen 13 — File saving + naming**

- Save directory (browse — USB drive recommended per §29; shows free space warning if SD card)
- File format: [FITS] / [XISF]
- Compression on/off (default on)
- Filename template — default per user's spec:
  ```
  $$DATEMINUS12$$\$$IMAGETYPE$$\$$DATETIME$$_$$FILTER$$_$$SENSORTEMP$$_$$EXPOSURETIME$$s_$$FRAMENR$$
  ```
- Template variable reference shown inline

**Screen 14 — Imaging defaults**

- Default exposure (s)
- Default gain / offset
- Default frame type: [Light] / [Dark] / [Bias] / [Flat]
- Cooling target inherited from camera screen

### 37.5 Stage 5 — Safety + site

**Screen 15 — Safety policies** (per §35)

Compact wizard layout:
```
When weather goes bad:
  Clouds: [Pause ▼]
  Wind:   [Pause ▼] above [30] km/h
  Rain:   [Abort + Park ▼]

When something's wrong and I'm not here:
  WILMA offline:        [Auto-abort after  5 ] min
  Alarm unanswered:     [Continue alarm ▼]

Alarm:
  Sound: [Default ▼]  [▶ test]
  Vibrate: ☑
```

Full editor is available later in Settings → Safety.

**Screen 16 — Site preferences**

- Hard min altitude (default 5°)
- Soft warning altitude (default 30°)
- Astronomical twilight margins (default: start at end-of-evening-astro, stop at start-of-morning-astro)
- Max sequence runtime (default: no limit)

### 37.6 Stage 6 — Sky data downloads

**Screen 17 — Sky data downloads** (per §36.12)

- Shows current Data Manager state (initially: ~50 MB installer base, nothing downloaded yet)
- Recommended download set, **default-checked**:
  - **Always recommended** (regardless of rig) — Famous Targets + Star Catalogs preset (~350 MB combined: Famous Targets Pack + Tycho-2 + GAIA brightest + UCAC4 + Full DE440)
  - **Recommended for your rig** — surveys keyed to Screen 4's focal length per §36.10
- Total size estimate updates live as user toggles checkboxes
- [Open Data Manager for full control] → opens §36.2 inline (returns to wizard on close)
- [Start downloads + continue] → kicks off background downloads, proceeds to Stage 7
- [Skip — set up later in Data Manager] → proceeds with no downloads; §36.13 silent banner appears in main app

If WILMA has no internet at this step, the screen shows "Internet unavailable — we'll download when you're online." The recommended set is recorded in the profile; downloads queue silently and execute when connectivity returns.

### 37.7 Stage 7 — Done

**Screen 18 — Review + Save**

- Single-page summary of every setting (per stage)
- [Make Changes — jump to any screen]
- [Save Profile]
- After save: navigate to main app shell

### 37.8 Wizard behavior rules

- Every screen has [Skip — use defaults] and [< Back] [Next >]
- User can [Save & Exit] at any point — profile saves partial state, defaults fill the rest
- Skipped screens are flagged in the profile: "Default — please review in Settings"
- Wizard can be re-run from Settings → Profile → "Run Wizard Again" (useful when changing rigs)
- Each "Use device GPS" / "Pull from driver" / "Auto-detect" interaction is non-blocking — wizard never hangs waiting on equipment

---

## 38. Sequence file format + NINA `.json` import

ARA adopts NINA's sequence JSON schema verbatim as the canonical storage format, adds a `schemaVersion` field for forward compatibility, exposes a validated OpenAPI schema, and imports existing NINA `.json` sequence files with equipment-remap + unsupported-instruction handling.

### 38.1 Canonical schema

- Top-level field: `"schemaVersion": "openastroara-sequence-v1"`
- All NINA container types preserved verbatim: `SequenceRootContainer`, `DeepSkyObjectContainer`, `SequentialContainer`, `ParallelContainer`, `ConditionalContainer`, etc.
- All NINA instruction types preserved verbatim where they have ARA equivalents (capture, slew, switch filter, run autofocus, plate solve, center, wait, etc.)
- Trigger system (BeforeEach, AfterEach) and condition system (ConditionalRunner, LoopCondition) preserved
- Schema documented in `OpenAstroAra.Server/openapi.yaml` (or `OpenAstroAra.Server/schemas/sequence.yaml` if pulled out for size). Used for:
  - Server-side validation on `POST`/`PUT` upload
  - IntelliSense in editors that consume OpenAPI
  - Swagger UI rendering at `/api/v1/docs`

### 38.2 Storage layout

**Pi side** (`/var/lib/openastroara/sequences/`):
```
sequences/
├── library/           user's saved/pushed sequences (canonical, served via API)
├── imported/          NINA imports, source preserved per dated subfolder
│   └── from-nina-YYYY-MM-DD/<original-name>.json
├── templates/         starter templates shipped with the .deb
│   ├── lrgb-dso.json
│   ├── narrowband-shoo.json
│   └── comet.json
└── active/            checkpoint state of currently-running sequence (per §28)
    └── current.json
```

**WILMA side** (app data `/sequences/`):
```
sequences/
├── drafts/            locally-edited sequences not yet pushed to Pi
└── synced/            read-only cache of last-fetched Pi library
```

When connected, WILMA periodically refreshes `synced/` from `GET /api/v1/sequences`. Drafts live only locally until user explicitly pushes via "Save to Server" or "Run on Pi" buttons.

### 38.3 Endpoints

| Method | Path | Purpose |
|---|---|---|
| `GET` | `/api/v1/sequences` | List saved sequences on Pi (id, name, target, last-modified, last-run) |
| `GET` | `/api/v1/sequences/{id}` | Fetch one sequence (full JSON) |
| `POST` | `/api/v1/sequences` | Upload new sequence (validates, assigns id, saves to library/) |
| `PUT` | `/api/v1/sequences/{id}` | Update existing |
| `DELETE` | `/api/v1/sequences/{id}` | Remove |
| `POST` | `/api/v1/sequences/import` | Multipart upload of a NINA `.json` file → returns parsed sequence + warnings array |
| `POST` | `/api/v1/sequences/{id}/start` | Start running (kicks off the sequence executor) |
| `GET` | `/api/v1/sequences/templates` | List bundled starter templates |
| `POST` | `/api/v1/sequences/templates/{name}/instantiate` | Copy a template into the user's library, optionally fill in target via request body |

### 38.4 NINA import flow

1. WILMA: Sequencer tab → [Import from NINA] → file picker → user selects `.json`
2. WILMA uploads via multipart to `POST /api/v1/sequences/import`
3. Server processing:
   - Parse JSON
   - Detect NINA format (no `schemaVersion`, has NINA container/instruction type names)
   - Add `"schemaVersion": "openastroara-sequence-v1"`
   - **Equipment-ID remapping**: any `CameraId`, `MountId`, `FilterId`, `FocuserId`, `RotatorId`, `DomeId`, `GuiderId` etc. referencing NINA's ASCOM ProgIDs are:
     - Fuzzy-matched to user's currently-connected Alpaca devices by name where possible
     - Otherwise set to `null` and added to the `warnings` array
   - **Unsupported instruction types** (MGEN-specific, plugin instructions, vendor-specific commands not exposed via Alpaca) are wrapped in a `SkippedInstruction { reason: "...", originalPayload: {...} }` placeholder
   - Save under `imported/from-nina-YYYY-MM-DD/<original-filename>.json`
   - Return the parsed sequence + `warnings: [...]` array shape:
     ```json
     {
       "sequence": { ... },
       "warnings": [
         { "type": "equipment_unmatched", "instruction": "...", "field": "CameraId", "originalValue": "ASCOM.QHYCCD.Camera" },
         { "type": "instruction_skipped", "originalType": "MGENGuiderInstruction", "reason": "MGEN not supported in ARA" },
         { "type": "filter_unknown", "filterName": "OIII6nm" }
       ]
     }
     ```
4. WILMA: displays sequence in editor with a warnings banner — *"3 issues need attention: 2 cameras need reassignment, 1 instruction skipped (MGEN)"* — user clicks through each warning to resolve in the editor
5. Once user resolves all warnings (or accepts them), sequence is functional and can be saved to library + run

### 38.5 Validation rules (server-side on `POST` / `PUT`)

- `schemaVersion` recognized (v1 currently; future schemas added incrementally)
- All referenced equipment IDs resolve to known Alpaca devices in the active profile, OR explicitly `null` with user acknowledgment
- All referenced filters exist in active profile's filter wheel slot configuration
- At least one capturable instruction reachable from root
- Time-based conditions reference valid astronomical events (`dusk`, `dawn`, `astronomical_twilight_start`, etc.)
- No infinite loops (a `LoopContainer` must have a terminating condition that evaluates to false reachably)
- Equipment slot uses match capability — e.g., `RunAutofocus` requires a focuser slot filled

Validation failures return 422 with detailed errors per failing instruction path.

### 38.6 Template variable system

Inherits NINA's syntax:

**Filename templates** (per §37 wizard, screen 13):
- `$$TARGETNAME$$`, `$$FILTER$$`, `$$EXPOSURETIME$$`, `$$DATE$$`, `$$DATETIME$$`, `$$DATEMINUS12$$`, `$$SENSORTEMP$$`, `$$FRAMENR$$`, `$$IMAGETYPE$$`, `$$BINNING$$`, `$$GAIN$$`, `$$OFFSET$$`

**Sequence template variables** (for `templates/` files that get instantiated against a user-picked target):
- `{{target_name}}`, `{{target_ra}}`, `{{target_dec}}`, `{{target_rotation}}`
- `{{integration_minutes}}`, `{{frames_per_filter}}`
- `{{filter_set}}` (a named filter combination from the profile, e.g., "LRGB" or "SHO")
- Substituted server-side at `POST /api/v1/sequences/templates/{name}/instantiate`

### 38.6.1 Filename template — sanitization + empty-token rules

ARA applies consistent rules to every `$$TOKEN$$` substitution to keep filenames safe across Linux/macOS/Windows filesystems + downstream tools (PixInsight, Siril) + cross-share Windows mounts.

**Illegal character handling.** The following characters are illegal on at least one common target filesystem and are unconditionally replaced with `_`:

```
/  \  :  *  ?  "  <  >  |
```

Plus: leading/trailing whitespace stripped; consecutive `_` collapsed to one; ASCII control chars (0x00–0x1F) replaced; non-printable Unicode replaced. Result is safe for ext4 (server), APFS/HFS (Mac WILMA), NTFS (Windows shares), and ZFS observatory NASes.

Examples:
- Target `"NGC 7000"` → `NGC_7000` (space kept; some users prefer underscores everywhere — see §38.6.2 user preference below)
- Target `"M27 / Dumbbell"` → `M27___Dumbbell` (slash replaced)
- Target `"C:Nebula"` → `C_Nebula` (colon replaced — would break Windows share path)
- Filter `"Hα"` → `H_` (non-ASCII alpha replaced; user can rename filter in profile to `Ha` for cleaner naming)

**Empty / null token policy.** Tokens that resolve to no value get an explicit placeholder rather than disappearing — keeps the filename structure consistent across captures and avoids accidental collisions:

| Token | Empty/null placeholder |
|---|---|
| `$$SENSORTEMP$$` (camera without cooler) | `noTemp` |
| `$$FILTER$$` (OSC camera or no filter wheel) | `noFilter` |
| `$$GAIN$$` (camera doesn't report) | `noGain` |
| `$$OFFSET$$` (camera doesn't report) | `noOffset` |
| `$$BINNING$$` (driver doesn't report) | `1x1` (sensible default; matches Alpaca spec) |
| `$$TARGETNAME$$` (no target set, e.g., Live View → Save Current) | `unnamed` |
| `$$IMAGETYPE$$` (always set by sequencer; capture-without-context = `LIGHT`) | always present |
| `$$EXPOSURETIME$$` | always present (zero exposure = bias; rendered `0s`) |
| `$$DATE$$` / `$$DATETIME$$` / `$$DATEMINUS12$$` | always present (UTC per §31.5) |
| `$$FRAMENR$$` | always present (sequencer-managed) |

Placeholder strings (`noTemp`, `noFilter`, etc.) are not localized; English ASCII for forever-stable filenames.

**Path length cap.** Total path (directory + filename + extension) capped at **200 characters**. Windows file shares historically capped at 260; staying under 200 leaves margin for share-mount prefixes (`\\server\share\path` adds ~30 chars typical). When approaching the cap, ARA truncates components in this priority order (preserving file uniqueness):

1. Frame number suffix preserved at all costs (uniqueness)
2. Date/time stamps preserved (chronological ordering)
3. Filter / exposure / temp truncated last (acquisition context)
4. `$$TARGETNAME$$` truncated FIRST when truncation needed (user-supplied; usually has fluff)

Example: a 250-char path with a verbose target name collapses to ~190 chars by truncating `$$TARGETNAME$$` from `Andromeda_Galaxy_M31_NGC224_Bortle4_Backyard_2026` to `Andromeda_Galaxy_M31_NGC224_Bortle4_...`.

Truncation emits a one-time WS event `frame.filename_truncated` (severity: warning) per session so the user knows their template is borderline.

**Validation at sequence start.** The sequencer parses the active filename template before the first capture and validates:
- All referenced tokens exist in the canonical list (unknown tokens → 422 with `code: "unknown_template_token"`, body: `{"unknown_tokens": ["$$BADTOKEN$$"]}`)
- Required tokens for uniqueness present: at least one of `$$FRAMENR$$` / `$$DATETIME$$` MUST be in the template (otherwise sequential frames overwrite each other; 422 with `code: "template_lacks_uniqueness_token"`)
- Estimated worst-case length with typical token values under 200 chars (warning, not error)

Failed validation blocks sequence start; user sees the error in the Sequencer panel with a [Fix template] link to the per-profile setting.

**Case preservation.** Token values pass through with original case (no auto-lowercasing or uppercasing). Filter names like `Ha`, `OIII`, `SII` retain their case as the user defined them in the profile.

### 38.6.2 User preference: spaces in filenames

By default, ARA preserves spaces in `$$TARGETNAME$$` (sanitization only replaces illegal chars). Users who want strictly-no-spaces (Linux command-line workflows, scripts that don't quote) can opt into a profile setting:

- `filenames.replace_spaces_with_underscores` — default `false`
- When `true`: spaces in `$$TARGETNAME$$` substitutions become `_` at the same step as illegal-char replacement

§61 search registry entry: `filenames.replace_spaces` — keywords: `underscore filenames, no spaces, replace spaces in filenames, snake case files`.

### 38.6.3 §14.1 integration test cases (added)

- `template_substitution_replaces_illegal_chars_with_underscore`
- `template_substitution_collapses_consecutive_underscores`
- `template_empty_sensortemp_emits_noTemp_placeholder`
- `template_empty_filter_emits_noFilter_placeholder`
- `template_path_over_200_chars_truncates_targetname_first`
- `template_truncation_emits_filename_truncated_ws_event_once_per_session`
- `template_with_unknown_token_returns_422_at_sequence_start`
- `template_lacking_framenr_and_datetime_returns_422`
- `template_with_targetname_containing_unicode_normalizes_to_ascii_safe`

### 38.7 Bundled starter templates (v0.0.1)

Ship 3 templates with the `openastroara-server` .deb at `/opt/openastroara/templates/`. Templates cover the DSO + comet imaging workflows ARA supports (per §18.J):

| Template | Use case |
|---|---|
| `lrgb-dso.json` | LRGB on a DSO — luminance + RGB filters, dither cadence, auto-focus on temp change |
| `narrowband-shoo.json` | SHO narrowband — Hα, OIII, SII filters with longer exposures |
| `comet.json` | Comet capture — shorter sub-exposures (60–120 s typical) to limit comet-motion smearing, no per-frame guiding correction for comet motion in v0.0.1 (deferred per §55.2). User points at a comet from the §36.9 catalog. |

No lunar / planetary templates — per §18.J, ARA's scope is DSO + comets only because Alpaca lacks a video API. Lunar/planetary lucky-imaging is permanently out of scope, not deferred. Each template uses placeholder target slots. User picks target via WILMA's "Apply Template" → "Pick Target" flow, which calls `POST /api/v1/sequences/templates/{name}/instantiate` with the target details.

### 38.8 Schema evolution policy

- v0.0.1 ships `openastroara-sequence-v1` (NINA-compatible)
- Backwards-compatible additions within v1 (new optional fields) are allowed; ARA bumps a `protocol_minor` in `/api/v1/server/info` and clients respect missing-field defaults
- Breaking changes go to `openastroara-sequence-v2` — server reads both, writes current; user-managed migration only if they want new v2-only features
- Schema version is independent from API version and from app version

### 38.9 WILMA sequence editor essentials (Phase 12)

- Tree view of: Sequence → Targets → Containers → Instructions
- Drag-drop reordering within a level
- Per-instruction editor pane on the right when selected
- Validation runs live as user edits; warnings shown inline
- "Push to Pi" button (validates + uploads + tracks server id)
- "Run Now" button (push + start; shortcut)
- "Apply Template" entry point in the sequence picker
- "Import from NINA" entry point in the same picker
- Local draft auto-save every 30 seconds to WILMA app data
- Conflict resolution: if WILMA's draft diverges from Pi's saved version, prompt user [Keep Local] / [Keep Pi] / [View Diff]

---

## 39. Calibration frames + session-metadata-driven auto-flats

ARA preserves NINA's separate `Light`/`Dark`/`Bias`/`Flat` instruction types and adds a workflow unique to ARA: **automatically generate a flat (or dark) sequence that matches the exact equipment state of a past imaging session.** Filter, focus position, rotator angle (CAA), gain, offset, cooler target — all replayed from the session's recorded metadata.

### 39.1 Frame types and instructions

Sequence instruction types (all inherited from NINA verbatim per §38):

| Instruction | Purpose |
|---|---|
| `TakeManyExposures` (light frames) | Standard imaging captures during a session |
| `TakeDarkExposures` | Dark frames at specified exposure + gain + temp |
| `TakeBiasExposures` | Bias frames (shortest possible exposure, shutter closed) |
| `TakeFlatExposures` | Flat frames at user-specified exposure |
| `FlatPanelFlats` | Coordinates with flat panel: turn on, set brightness, capture flats per filter, turn off |
| `DarkLibraryInstruction` | Builds a dark library across a matrix of (exposure × gain × temp) tuples |
| `SkyFlats` | Captures sky flats during twilight using auto-exposure to target ADU |

### 39.2 Calibration philosophy: capture-only

ARA Core captures calibration frames and writes them to disk with rich FITS headers. ARA Core does **not** apply calibration during capture (no live dark subtraction, no live flat division). Calibration is the responsibility of the user's post-processing tools (PixInsight, Siril, GraXpert, AstroPixelProcessor, etc.) which read the FITS headers to match calibration frames to lights.

Rationale: applying calibration at capture-time doubles disk usage, locks the user into a calibration strategy chosen at capture-time, slows the imaging cadence, and removes the user's ability to re-calibrate with improved master frames later. NINA does not do this either; ARA matches that behavior.

### 39.3 Session metadata (the foundation for matching flats)

Every captured frame's FITS header includes the complete equipment state at capture time:

| FITS keyword | Meaning |
|---|---|
| `DATE-OBS` | UTC capture start |
| `INSTRUME` | Camera name (Alpaca-reported) |
| `FILTER` | Filter slot name |
| `FOCUSPOS` | Focuser absolute position |
| `ROTANG` | Rotator angle (degrees) — the "CAA" |
| `SET-TEMP` | Cooler target temp (°C) |
| `CCD-TEMP` | Achieved sensor temp (°C) |
| `AMBTEMP` | Ambient temp from weather (if connected) |
| `HUMIDITY` | Ambient humidity from weather (if connected) |
| `EXPTIME` | Exposure duration (s) |
| `GAIN` | Sensor gain |
| `OFFSET` | Sensor offset |
| `XBINNING` / `YBINNING` | Binning |
| `OBJECT` | Target name (from sequence) |
| `OBJCTRA` / `OBJCTDEC` | Target RA / Dec |
| `OBJCTROT` | Target rotation angle |
| `IMAGETYP` | `LIGHT` / `DARK` / `BIAS` / `FLAT` |
| `SESSIONID` | UUID linking to Pi's session database row |
| `FRAMENR` | Frame number within the session |
| `SITELAT` / `SITELON` / `SITEALT` | Observatory location |

Pi-side session database (SQLite) mirrors this data in queryable form for cross-session analytics:

```sql
CREATE TABLE sessions (
  id TEXT PRIMARY KEY,
  profile_id TEXT,
  started_at TIMESTAMP,
  ended_at TIMESTAMP,
  target_name TEXT,
  target_ra REAL,
  target_dec REAL,
  target_rotation REAL,
  rotator_angle REAL,
  total_frames INT,
  notes TEXT
);

CREATE TABLE frames (
  id TEXT PRIMARY KEY,
  session_id TEXT REFERENCES sessions(id),
  fits_path TEXT,
  captured_at TIMESTAMP,
  image_type TEXT,
  filter TEXT,
  focus_pos INT,
  rotator_angle REAL,
  set_temp REAL,
  ccd_temp REAL,
  ambient_temp REAL,
  exposure_seconds REAL,
  gain INT,
  offset INT,
  binning TEXT
);
```

### 39.4 Session library API

| Method | Path | Purpose |
|---|---|---|
| `GET` | `/api/v1/sessions` | List sessions (id, target, date, frame count, filters used) |
| `GET` | `/api/v1/sessions/{id}` | Full session metadata + summary stats |
| `GET` | `/api/v1/sessions/{id}/frames` | List frames within the session |
| `GET` | `/api/v1/sessions/{id}/calibration-suggestions` | Server analyzes session and returns a suggested calibration sequence (flats per filter, darks for the session's exposures, bias) |
| `POST` | `/api/v1/sessions/{id}/generate-flat-sequence` | Server constructs a `TakeFlatExposures` sequence matching this session's equipment state, returns the sequence JSON (user can review + push + run) |
| `POST` | `/api/v1/sessions/{id}/generate-dark-sequence` | Same, for darks |

### 39.5 "Capture matching flats from session" workflow (the killer feature)

User flow in WILMA:

1. Open the **Image Library** tab → list of past sessions, grouped by target + date
2. Pick a session → details page shows frame counts per filter, total integration, equipment used
3. Click **[Capture Matching Flats]** button
4. Server analyzes the session and returns a suggested flat sequence. **Because the server recorded the per-filter equipment state during the session, it physically commands the equipment back to those exact positions before capturing flats** — no manual reconfiguration required:

   ```
   For each filter used (L, R, G, B, Hα):
     1. Filter wheel: rotate to the same slot used during the session
     2. Focuser: move to the focus position recorded for THAT filter in
        the session (focus often differs per filter — server pulls the
        right value from the session's per-frame metadata)
     3. Rotator (CAA): slew to the same angle used during the session,
        so dust mote shadows align with the lights
     4. Cooler: set target to the session's SET-TEMP and wait for
        stabilization (subject to ambient limits — see §39.6)
     5. Camera: configure same gain, offset, binning
     6. Flat panel: turn on, auto-adjust brightness to target ADU
        (~30000 for 16-bit), capture 30 flat frames
     7. Flat panel: turn off
   Mount can stay parked or wherever — flats don't depend on sky position
   ```

   The result: a flat library that calibrates the original lights exactly, because every optical-train variable that affects vignetting and dust shadows (filter, rotation, focus distance) is identical between lights and flats.
5. WILMA displays the suggested sequence for review with any **warnings**:
   - "Session sensor temp was −10°C; current cooler achievable max is −3°C — flats will be captured at −3°C instead (post-processing tools may show minor calibration noise)"
   - "Rotator angle differs from current position by 47° — will rotate before flat capture"
   - "Filter wheel position for 'OIII' was slot 3; current slot 3 is 'SII' — please verify filter wheel hasn't been reconfigured since the session"
6. User confirms (or adjusts) → sequence pushed to Pi → runs
7. Captured flats are tagged with `SESSIONID` matching the original lights, plus a sidecar JSON noting "calibration-for-session: <id>"

### 39.6 Temp mismatch handling

The cooler-temp problem the user flagged is real: a session at −10°C in winter (ambient 5°C, delta 15°C) cannot be exactly replicated in summer (ambient 30°C, cooler max delta ~30°C → achievable target ~0°C).

Strategy:

- Server queries the camera's reported cooler capability (typically max delta below ambient, ~30-40°C for most CMOS cameras)
- Computes whether session's `SET-TEMP` is achievable at current ambient
- If not: warn user, offer:
  - **[Use closest achievable temp]** (recommended) — flats at the new temp; FITS header records both target and achieved
  - **[Wait until conditions allow]** — sequence scheduled for early morning when ambient is coolest, or deferred
  - **[Cancel — capture flats during the next imaging session instead]**
- The achieved temp is recorded in `CCD-TEMP` regardless; post-processing tools will handle the mismatch (they may warn the user, but most modern stacking pipelines handle small temp deltas gracefully)

### 39.7 Auto-flats at dusk (during a session)

Inherits NINA's `FlatPanelFlats` instruction. Typical sequence structure:

```
Container: Tonight's Session
  Instructions:
    - WaitForTimeOf(astronomical_dusk)
    - Container: Lights
        - TakeManyExposures (× target1, all filters)
    - WaitForTimeOf(astronomical_dawn)
    - Container: Flats
        - FlatPanelFlats (× each filter used tonight, 30 frames each)
```

Server-side `FlatPanelFlats` reads which filters were used in the session (live), automatically generates the flat captures for each.

### 39.8 Dark library auto-generation

Inherits NINA's `DarkLibraryInstruction`. User defines a matrix:

```json
{
  "type": "DarkLibraryInstruction",
  "exposures": [30, 60, 120, 300, 600],
  "gains": [0, 100, 200],
  "temps": [-10, -5, 0],
  "framesPerCombination": 50
}
```

Server captures `5 × 3 × 3 × 50 = 2,250` dark frames over many hours (typically a moonless or cloudy night when imaging is impossible anyway). Stores at `/var/lib/openastroara/calibration/darks/`.

### 39.9 Calibration library storage on Pi

```
/var/lib/openastroara/calibration/
├── darks/
│   └── <camera-id>/
│       └── exp_<seconds>_gain_<n>_temp_<c>/
│           └── frame_001.fits
├── bias/
│   └── <camera-id>/
│       └── gain_<n>_offset_<n>/
│           └── frame_001.fits
└── flats/
    └── <camera-id>/
        └── filter_<name>_rot_<angle>_focus_<pos>/
            └── frame_001.fits
```

Filenames + sidecar JSONs make matching by metadata easy for both ARA's session-matching workflow and external post-processing tools.

### 39.10 Calibration library browsing in WILMA

Settings → Calibration Library:

- Tab per frame type (Darks / Bias / Flats)
- Browseable table: filter / exposure / gain / temp / rotator / focus / count / date
- "Verify integrity" button (read FITS, check for corruption)
- "Match to session" — reverse direction: pick a session, see which calibration frames in the library match it
- "Export" — download a tarball of selected frames to WILMA for use in PixInsight etc.
- "Delete" — remove old/superseded frames
- Storage usage indicator + auto-prune option (e.g., "keep latest 30 days, prune older if >50 GB")

### 39.11 Comparison to ASIAir / NINA / SharpCap

| Capability | ASIAir | NINA | ARA |
|---|---|---|---|
| Light/Dark/Bias/Flat as sequence types | Yes | Yes | Yes |
| Apply calibration at capture | No | No | No (capture-only philosophy) |
| Auto-flats at dusk with flat panel | Yes | Yes (FlatPanelFlats) | Yes (inherited) |
| Dark library auto-generation | Limited | Yes (DarkLibraryInstruction) | Yes (inherited) |
| Session metadata recorded in FITS | Yes | Yes | Yes (richer — full equipment state) |
| **Generate flats matching a past session** | **Yes** | **No** | **Yes (ARA-native feature)** |
| Library browsing UI | Limited | Sequencer view | Rich (Settings → Calibration Library) |

The "matching flats from past session" is genuinely a unique-to-ARA improvement over NINA, inspired by ASIAir's workflow.

---

## 40. Captured-image library workflow

WILMA's Image Library tab is the user's window into everything the Pi has captured. Frames are organized by session (the user's mental unit) and cross-indexed by target (so multi-night, multi-year projects line up perfectly). Available on desktop with full UX, on mobile with view-only UX (per §41).

### 40.1 Frame storage on Pi (recap)

Per §29 + §39: FITS frames live at the configured save path (USB drive recommended) at `<save-path>/captures/<session-id>/<target>/<filter>/<frame>.fits`. Sidecar previews at `<frame>.preview.jpg`. Metadata indexed in Pi-side SQLite `frames` table (see §39.3).

### 40.2 Preview tiers

Server generates two JPEG previews per captured FITS:

| Preview | Resolution | Size | Purpose |
|---|---|---|---|
| `<frame>.thumb.jpg` | Max 480×360 | ~50 KB | List views, dashboard tiles, search results |
| `<frame>.preview.jpg` | Native sensor resolution, quality 90 | ~3-8 MB | Full pinch-to-zoom pixel peep on mobile + desktop |

Both generated server-side at capture time (per §28.5 / §39 — already in the capture pipeline). Stretched per the user's profile-default stretch setting (see §65 for the full stretch palette, defaults policy, manual-stretch sliders, and cache strategy). Alternative stretches are available on demand via `?stretch=` on the preview endpoint (§65.6) and via the frame viewer's stretch picker (§65.9).

### 40.3 API endpoints

| Method | Path | Purpose |
|---|---|---|
| `GET` | `/api/v1/sessions` | List sessions (id, target, date, frame count, total integration, filters used) |
| `GET` | `/api/v1/sessions/{id}` | Full session metadata |
| `GET` | `/api/v1/sessions/{id}/frames` | List frames in session, filterable by frame type / filter / rating |
| `GET` | `/api/v1/frames/{id}` | Single frame metadata |
| `GET` | `/api/v1/frames/{id}/thumb` | Tiny JPEG (for lists) |
| `GET` | `/api/v1/frames/{id}/preview` | Full-resolution JPEG (for pixel peep). Accepts `?stretch=` + manual params per §65.6 |
| `GET` | `/api/v1/frames/{id}/fits` | Original FITS file (full bytes, large) |
| `PATCH` | `/api/v1/frames/{id}` | Update rating, tags, notes |
| `DELETE` | `/api/v1/frames/{id}` | Delete frame (FITS + previews + DB row) |
| `POST` | `/api/v1/frames/bulk` | Bulk operations (multi-rate, multi-tag, multi-delete) |
| `GET` | `/api/v1/targets` | Roll-up by target: cumulative integration time, sessions count, filter breakdown |
| `GET` | `/api/v1/targets/{name}/sessions` | List all sessions that imaged a given target |
| `POST` | `/api/v1/targets/{name}/resume` | Create a new sequence template seeded from the most-recent session's plate-solve + rotator + filter usage (per §40.6) |

### 40.4 Image Library tab UI (desktop)

```
┌──────────────────────────────────────────────────────────────┐
│  Image Library                                                │
│  [By Session ▼]  [▼ All filters]  [⭐ Any rating]  [🔎 Search] │
│  ────────────────────────────────────────────────────────────  │
│  ▼ 2026-05-18 — M42 Orion Nebula (Backyard Texas)              │
│    4h 12min total · L:48 R:32 G:32 B:32 (144 frames)           │
│    [Capture Matching Flats]  [Resume Target]                   │
│                                                                │
│    ┌──┐┌──┐┌──┐┌──┐┌──┐┌──┐┌──┐┌──┐                            │
│    │  ││  ││  ││  ││  ││  ││  ││  │   ... 144 thumbnails       │
│    └──┘└──┘└──┘└──┘└──┘└──┘└──┘└──┘                            │
│                                                                │
│  ▼ 2026-05-12 — NGC 6188 Fighting Dragons (Backyard Texas)    │
│    2h 30min total · Hα:30 OIII:30 SII:30 (90 frames)          │
│    [Capture Matching Flats]  [Resume Target]                   │
│    ...                                                         │
└──────────────────────────────────────────────────────────────┘
```

- **Group toggle**: [By Session] / [By Target] / [By Date]
- **Filter pills**: filter band, frame type (Light/Dark/Bias/Flat), rating
- **Search**: target name, filter, date range, free-text in notes
- **Per-session row**: [Capture Matching Flats] (§39.5) and **[Resume Target]** (§40.6)
- **Thumbnail strip**: tap any → full frame viewer

### 40.5 Frame viewer (desktop + mobile)

```
┌──────────────────────────────────────────────────┐
│  M42_L_2026-05-18T22:14:32_120s.fits      ⭐⭐⭐⭐  │
│  ──────────────────────────────────────────────  │
│                                                  │
│       [full preview image, pinch/scroll to zoom] │
│                                                  │
│  ──────────────────────────────────────────────  │
│  Exposure: 120s  Gain: 100  Offset: 50           │
│  Filter: L       Bin: 1×1                        │
│  HFR: 1.42       Stars: 487                      │
│  Median ADU: 1284   Background: 1102             │
│  Sensor temp: −10.0°C  Focus: 14820 steps        │
│  Captured: 2026-05-18 22:14:32 UTC               │
│                                                  │
│  Notes: [...]                                    │
│  Tags: [good_seeing]                             │
│                                                  │
│  [Rate]  [Tag]  [Open in App]  [Show in Folder]  │
│  [Download FITS]  [Delete]                       │
└──────────────────────────────────────────────────┘
```

- Pinch-to-zoom + pan on desktop (trackpad gestures) and mobile (touch)
- Full-resolution JPEG preview by default; **[Download FITS]** pulls original 50MB file
- **[Open in App]** invokes OS file-association (system "open with" → PixInsight / Siril / GraXpert / etc. based on user's default FITS handler)
- **[Show in Folder]** opens the file's location in Finder/Explorer (desktop only)
- 0–5 star rating; free-text tags; optional notes
- HFR / star count / median ADU shown inline (read from the session DB, originally computed server-side at capture time)

### 40.6 "Resume Target" workflow — multi-year project alignment

Critical for users building up integration on a target across months or years. The button on the per-session row in the library:

1. User picks a target with prior history (e.g., M42 with 4 sessions over 18 months)
2. WILMA calls `POST /api/v1/targets/M42/resume`
3. Server returns a **new sequence draft** pre-configured to align exactly with the most-recent session:
   - Plate-solve target = recorded center RA/Dec from that session
   - Rotator angle = recorded ROTANG from that session
   - Filter list = filters historically used (sorted by frequency)
   - Exposure / gain / offset defaults = pulled from that session
   - Profile reference = same equipment expected (warn if profile has changed substantially)
4. User reviews + tweaks (add/remove filters, change exposure count) → [Save] / [Run]
5. When the sequence runs, the **§28 recovery flow runs in reverse**: mount slews to target, plate-solves to the *recorded* RA/Dec/rotation (not just "close enough"), refines until within tight tolerance (default 30 arcsec position, 0.5° rotation — half the recovery defaults), then begins capturing
6. New frames written with `OBJECT` matching the target name, so they roll up into the same per-target aggregate

This is what makes "come back in 3 years and add more data" work: the rotator and plate-solve solution are reproducible because we recorded them precisely.

### 40.7 Auto-rating + HFR drift detection (the "clouds, not focus" pattern)

Server analyzes HFR and star count after each frame:

**Auto-rating (per-frame, inherited from NINA logic):**
- HFR > profile threshold (default 2× session-median) → frame rated 1⭐ (auto-reject suggested)
- Star count < profile threshold → rated 1⭐
- Median ADU below floor (severely underexposed) or above ceiling (saturation) → rated 2⭐
- Otherwise → rated 3⭐ by default; user upgrades to 4⭐/5⭐ if they pixel-peep and like it

**Pattern detection (ARA-native — flagged "clouds, not focus"):**
After each autofocus completes, server tracks:
- Frame immediately post-AF: HFR
- N consecutive subsequent frames: HFR
- If pattern emerges (good HFR → degraded HFR → AF retriggers → good HFR → degraded HFR again, within a short window), pattern is `cycling_degradation`
- Queue notification: *"Autofocus completed twice in 12 minutes but HFR degrades immediately after each focus run — likely transient clouds or seeing, not a focus mechanism issue. Check sky conditions."*
- Optional: pause the sequence after N consecutive bad frames (configurable in safety policies §35; default off)
- Bad frames during the cycle are auto-rated 1⭐ for post-processing rejection

### 40.8 Bulk operations

Multi-select frames via Shift+Click (desktop) or long-press + tap (mobile):
- **Rate selection** — set 0-5 stars on all
- **Tag selection** — add/remove tags on all
- **Delete selection** — confirm + remove from disk + DB
- **Download FITS for selection** — zip + download to WILMA
- **Export** — copy to a folder picked by the user (desktop only; on mobile this is "Save to Files" or share sheet)

### 40.9 Storage management

- Per-session row shows total disk used
- Filter view: "Show only frames > 30 days old, < 3⭐ rating" → bulk-prune candidates
- Auto-prune policy (Settings → Storage on the Pi): never / weekly / monthly, with rules ("delete frames < 2⭐ older than X days, never delete frames marked 4⭐+")
- All destructive operations confirm + are logged

---

## 41. Mobile companion mode (iOS / Android)

**v0.0.1 scope status: SPEC ONLY — no mobile builds ship.** Mobile distribution is deferred to v0.1.0 per §18.G (requires funded Apple Developer + Play Console accounts + per-platform review workflow + ongoing signing maintenance). The §41 spec stays in the playbook because it informs v0.0.1 API decisions (WebSocket event shapes, single-client policy semantics, mDNS discovery, GPS-push endpoint, emergency-stop authentication-free semantics) — so the server-side API surface is correct when v0.1.0 turns on mobile builds. Flutter codebase already supports iOS/Android targets; what's missing is the distribution + signing + review pipeline, not the code.

When v0.1.0 enables mobile builds, no server changes are needed — the same WILMA codebase compiles for iOS/Android with platform-detection-driven shell selection per §41.4. The spec below describes the *intended* mobile UX; treat as v0.1.0 design with v0.0.1 API forward-compatibility.

WILMA on iOS/Android runs in **Companion Mode** — same Flutter codebase as the desktop client, but the UI is tailored for phone/tablet form factors and many "configuration" workflows are intentionally absent (replaced by a "Open ARA on your desktop to do this" prompt). The phone is for monitoring, viewing, and emergencies — not for planning tomorrow's session.

### 41.1 Mobile companion — what it CAN do

| Capability | Notes |
|---|---|
| Connect to Pi (mDNS discovery, no auth per §67) | Same flow as desktop (§30) |
| **GPS + time push to Pi** | Primary value-add when user has no USB GPS dongle (§31) |
| **Dashboard** | Current sequence, target, last frame thumbnail, time-to-next-frame, equipment connection state, sky safety status |
| **Image library browsing** | Same data as desktop (§40), responsive layout — grouped by session, scrollable thumbnail strips |
| **Frame viewer with pinch-to-zoom** | Full-resolution JPEG preview, native gestures, HFR + star count + temp displayed |
| **Live preview during active session** | Subscribes to WebSocket `frame.complete` events, latest frame appears automatically |
| **Emergency stop button** | Always visible in the persistent bottom bar; same flow as desktop (§35.3) |
| **Safety alarm response** | Receives `safety.unsafe` WebSocket events; full-screen alarm modal with audio + vibration (§35.5); [Emergency Abort] / [Override] |
| **Push notifications** | Sequence complete, safety alerts, HFR drift detection, recovery events |
| **Log tail** | Read-only live log stream |
| **Rate + tag frames** | Touch-friendly star rating + tag chips |
| **Download FITS for off-device processing** | Save to platform Files / Photos / share sheet |
| **Server connection management** | Same Settings → Server panel as desktop (§30.6); no token UX per §67 |

### 41.2 Mobile companion — what it explicitly does NOT do

| Capability | Why excluded | What user sees |
|---|---|---|
| Sequence editor | Drag-drop instruction tree is bad UX on a 6-inch screen | "Sequence editing requires the ARA desktop app — open WILMA on your Mac, PC, or Linux machine to edit sequences." Quick-share link button to open desktop on the same Wi-Fi. |
| Profile / equipment configuration wizard | 18-screen wizard cramming into phone = pain | Same redirect message |
| Sky Atlas (full Aladin Lite + Tonight's Sky) | Aladin Lite WebView with 21-survey browsing is computationally heavy on phones + 500MB+ tile bundling cost | Same redirect; users who want sky atlas on mobile run Stellarium or SkySafari standalone |
| ASTAP path / autofocus / plate-solve config | Settings | Same redirect |
| Sequence templates / instantiation | Editor-adjacent | Same redirect |

When a mobile user taps something disallowed, they get a polite modal with a "Copy link to send to your desktop" option that puts an ARA-protocol URL on the clipboard (e.g., `araapp://session/123/edit`) that the desktop app can pick up.

### 41.3 Mobile-specific UX considerations

- **Always-on bottom bar**: [Dashboard] [Library] [Logs] [Emergency Stop] — emergency button is permanently visible regardless of which tab is active
- **Push notifications**: Firebase Cloud Messaging on Android, APNs on iOS, **but only between WILMA and the Pi** — no third-party telemetry path; Pi sends webhook to client's notification endpoint. (v0.0.1 may defer push and rely on in-app foreground notifications only — depends on Apple/Google account setup effort)
- **Background mode caveat**: iOS aggressively suspends backgrounded apps; Android less so. App in background may miss WebSocket events; user opens app → fresh state pulled via REST snapshot. Push notifications wake the user for critical events even if app is suspended.
- **Tablet (iPad / Android tablet)**: companion mode renders with more density (split view: dashboard + library side-by-side); pinch-to-zoom on frames goes huge; otherwise same scope as phone. iPad Pro users get a perfectly usable casual-monitor experience.
- **Apple Watch / Wear OS**: out of scope for v0.0.1. Could be a future "notifications only" companion app.

### 41.4 Shared Flutter codebase, conditional shell

```dart
// pseudocode
final isCompanionMode = (Platform.isIOS || Platform.isAndroid);

Widget appShell() => isCompanionMode
    ? CompanionShell(routes: companionRoutes)
    : DesktopShell(routes: desktopRoutes);
```

Shared:
- API client (auto-generated from OpenAPI)
- State management (Riverpod providers)
- WebSocket connection + event handlers
- Saved-server state (no auth tokens in v0.0.1 per §67; storage layer is reserved for v0.1.0 remote-access tokens)
- Common widgets (frame viewer, dashboard tiles, status indicators)

Different:
- Top-level navigation (tabs vs nav rail)
- Some screens entirely (sequence editor absent on mobile, Aladin tab absent)
- Modal sizing (full-screen on mobile, dialog on desktop)
- Gesture handling (touch-first on mobile, mouse-first on desktop)

### 41.5 Mobile-only entry points

Two flows that exist ONLY on mobile:

- **First-launch GPS push** — if user opens mobile app and the Pi reports no recent time-sync, mobile companion auto-prompts to push device GPS+time without requiring profile-screen entry. Matches the "I just want to give the Pi a clock and go" use case.
- **Wake-from-notification → live frame view** — push notification "Frame 47 captured" → tap → opens directly to that frame in the viewer.

### 41.6 Versioning + acronym

WILMA (Windows / iOS / Linux / Mac / Android) acronym is preserved. Mobile platforms (iOS, Android) explicitly run in Companion Mode by default. Desktop platforms (Windows, macOS, Linux) run the full client.

In practice this means a single `flutter build` per platform, with platform-detection-driven shell selection at runtime.

---

## 42. Hardware fault recovery (per-equipment)

Distinct from §28 (server crash recovery). This section covers per-equipment "something went wrong while the server was running" handling: camera disconnects mid-exposure, mount loses tracking, focuser stalls, EFW jams, dew heaters fail, etc. Most of this logic is preserved from NINA; this section documents what's preserved + the few ARA-native additions (switch value-tolerance, dew detection, hot-reconnect).

### 42.1 Retry-then-action pattern (universal)

Every fault uses the same flow:

```
Fault detected
   ↓
Retry N times with exponential backoff (default: 3 attempts, 5s/15s/30s)
   ↓ all retries failed
   ↓
Execute fault's configured action per profile
   ↓
   Continue   = log, keep going (use for benign / informational faults)
   Notify     = queued WebSocket event, sequence continues
   Pause      = sequence pauses at next safe point, equipment stays connected, user resumes manually
   Abort+park = full §35.3 emergency stop sequence (camera abort, guider stop, mount park, etc.)
```

Per-fault action is configurable in profile safety policies (§35 extension), with the defaults below.

### 42.2 Fault matrix

| Fault | Detection | Default action | Notes |
|---|---|---|---|
| Camera disconnect / capture error mid-exposure | Alpaca connection error or capture timeout | Reconnect → Pause if persistent | In-flight frame lost; next frame retries |
| Camera cooling failure (set temp not reached after timeout) | `CCDTemp` vs `SetCCDTemperature` drift > 5°C for > 5 min | Notify | Don't abort — user may want to image at warmer temp |
| Camera dew heater unexpectedly OFF | Alpaca `DewHeaterPower` queried, expected vs reported | Re-command ON → Notify if still off | Camera-integrated heaters only |
| Mount loses tracking | Alpaca `Tracking = false` unexpectedly during exposure | Re-enable → Pause if rejected | Common cause of trailed stars |
| Mount slew error / refuses command | Alpaca slew returns error or doesn't complete | Retry → Abort + park | May indicate physical obstruction |
| Mount unexpected park / disconnect | Alpaca connection lost or mount auto-parks | Reconnect → Abort + park if persistent | Cable disconnect is most common |
| Focuser stalls | Commanded position not reached within timeout | Retry → recalibrate backlash → Notify | Common in cold weather (lubricant viscosity) |
| **EFW (filter wheel) jam / position not reached** | Commanded slot not reached within timeout | Retry → Notify | User must intervene physically |
| Rotator (CAA) runaway / position drift | Reported angle differs from commanded > tolerance (default 0.5°) | Re-issue → Notify | Mechanical issues |
| Guider (PHD2): loses calibration | PHD2 calibration-failed event | Recalibrate → Pause if persistent | Common after meridian flip |
| Guider (PHD2): loses guide star | PHD2 star-lost event for > 30s | Pause → wait for recovery (clouds passing) | Often transient |
| Guider (PHD2): dither timeout | Dither not settled within 60s | Continue (log warning) | Skip dither, keep imaging |
| Plate solve failure | After §28.2 retries (3 attempts) | Pause + notify | User can re-frame target or skip |
| ASTAP / Astrometry.net executable crash | Process exit code != 0 | Retry once → Notify | Re-invoke; bad images usually cause this |
| **External dew heater (Alpaca Switch) commanded ON but reporting OFF** (boolean switch) | Switch read-back mismatch | Re-command → Notify if still off | Power-port dew straps |
| **External switch value mismatch** (PWM heater, dimmable flat panel, etc. — value-based ISwitch) | Commanded value vs read-back outside tolerance (default ±5%) | Re-command → Notify | Pegasus PowerBox-style devices |
| **Dew formation suspected** | Pattern: humidity near 100% AND ambient at dew point AND HFR rising gradually (halos forming) | Notify | Advisory only — no auto-abort. User intervention required (wipe optics, enable heaters) |

### 42.3 Hot-reconnect on disconnect

When any device disconnects mid-session, ARA Core attempts automatic reconnect with backoff before pausing the sequence:

```
Disconnect detected
   ↓
Attempt 1: reconnect immediately
   ↓ fail
Attempt 2: wait 5s, reconnect
   ↓ fail
Attempt 3: wait 15s, reconnect
   ↓ fail
Attempt 4: wait 30s, reconnect
   ↓ fail
Attempt 5: wait 60s, reconnect
   ↓ fail
Pause sequence + queue notification to WILMA
```

Most disconnects are transient (USB hub hiccup, AlpacaBridge restart, WiFi blip) and recover by attempt 2 or 3.

### 42.4 Switch value tolerance (Alpaca `ISwitch`)

ARA treats Alpaca Switch devices using the full `ISwitch` interface:

- **Boolean switches** (port on/off): fault if commanded state ≠ read-back state
- **Value-based switches** (PWM, dimmable): fault if `|commanded − readBack| > tolerance × range`
  - Default tolerance: 5% of the switch's min/max range
  - Tolerance configurable per switch in profile (some devices have 10%+ inherent noise)
  - Useful for: dew heater PWM, flat panel brightness, focuser temperature setpoint on heated focusers

### 42.5 Fault logging

Every detected fault is logged to the Pi session database:

```sql
CREATE TABLE faults (
  id TEXT PRIMARY KEY,
  session_id TEXT REFERENCES sessions(id),
  detected_at TIMESTAMP,
  equipment_type TEXT,       -- "camera", "mount", "focuser", "efw", "dewheater", etc.
  equipment_id TEXT,         -- Alpaca device id
  fault_type TEXT,           -- "disconnect", "tracking_lost", "value_mismatch", etc.
  details JSON,              -- fault-specific payload
  action_taken TEXT,         -- "retry", "reconnect", "pause", "abort"
  resolved_at TIMESTAMP,     -- null if unresolved
  affected_frames JSON       -- list of frame IDs that may have been impacted
);
```

### 42.6 Fault visibility in WILMA

- **Live**: dashboard equipment chip turns yellow/red on fault detection; tap → fault details modal with retry attempt count, last error message
- **Session library**: per-session fault count badge; tap session → "Faults" tab shows timeline with each fault, action taken, frame impact
- **Image library** (§40): individual frames captured during a fault window are marked with a fault icon overlay (e.g., "captured while mount tracking was lost — likely trailed")
- **Per-fault recommendation**: WILMA shows brief advice ("Mount lost tracking — check cable, weight balance, slew limits")

### 42.7 What's preserved verbatim from NINA

- Backlash compensation algorithm + per-direction step counts
- Focuser temperature compensation curves
- Autofocus retry logic + step pattern
- PHD2 calibration retry semantics
- Plate-solve retry strategy
- Per-instruction retry counts in the sequencer

### 42.8 What's ARA-native

- Switch value-tolerance for PWM/dimmable devices
- Dew formation detection from weather + HFR pattern (§40.7 + this section)
- Hot-reconnect with explicit backoff schedule (NINA had partial; ARA formalizes)
- Per-frame fault flagging in the image library
- Unified retry-then-action pattern across all fault types (NINA varies by subsystem)

---

## 43. Backup + restore

ARA's backup model is **"the USB drive IS the backup unit"** because §29 makes USB storage mandatory and ALL persistent state (profiles, sequences, session DB, calibration library, FITS frames, logs) lives there. The Pi's SD card is disposable.

### 43.1 The portability story

Because everything lives on USB, ARA Core gets a powerful invariant for free: **pull the USB drive out, plug it into a different Pi, and ARA picks up exactly where you left off.** Same profiles, same sessions, same calibration library, same in-flight sequence state.

This makes:
- **Pi replacement** trivial — SD card died? Buy a new one, flash Trixie, `apt install openastroara-server`, plug your USB in, you're back.
- **Field-to-home migration** seamless — image at a dark site on Pi A, drive home, plug the USB into Pi B (your home observatory Pi) for processing without moving files.
- **Hardware testing** safe — try a different Pi or Orange Pi or RockChip, swap USB between them, no data risk.

### 43.2 What backups protect against

The USB-IS-the-data model is fast and portable, but it has one failure mode: **the USB drive itself dies or is lost/stolen**. Backups address only this.

Four backup layers, in priority order:

| Layer | What it protects | Scope |
|---|---|---|
| **1. Drive-to-drive clone** (recommended primary) | USB drive failure, loss, theft | Everything: FITS + DB + profiles + sequences + calibration |
| **2. Real-time backup stream to desktop WILMA** (see §44) | USB drive failure mid-session — frames already streamed are safe on the PC | FITS files trickled to desktop in real time during imaging |
| **3. Server-generated backup ZIP** (downloadable via WILMA) | Profile/sequence/setting loss; quick portability | Profiles + sequences + DB + calibration metadata. **NOT FITS files** (too large). |
| **4. Auto-snapshots on the same USB** (low-value but cheap) | "I accidentally deleted my profile" / catastrophic bug in ARA writes bad DB | Profiles + sequences + DB snapshot. Stored under `.araback/` on the same drive. |

### 43.3 Drive-to-drive clone (primary backup mechanism)

The recommended user workflow: every few weeks, clone the working USB drive to a second drive kept in a separate location.

DEPLOY.md documents the procedure:

```bash
# Identify both drives:
lsblk -f
# Working drive = /dev/sda, backup drive = /dev/sdb (example)

# Clone, drive-to-drive (full block-level copy):
sudo dd if=/dev/sda of=/dev/sdb bs=4M status=progress

# Or, file-level mirror (faster, only changed files):
sudo rsync -aAXxv --delete /media/openastroara/ /mnt/backup-drive/openastroara/
```

ARA does not automate this — the user manages their backup drive(s). v0.1.0 may add a "backup wizard" that guides the user through this with checks (drive size, free space, integrity verify) but stays out of the user's hardware.

### 43.4 Server-generated backup ZIP

For lightweight portability + WILMA-driven backup convenience:

`POST /api/v1/server/backup/create`
- Server zips: `profiles/`, `sequences/library/`, `templates/`, `db/openastroara.db` (snapshot), `calibration/` metadata sidecar JSONs only (not the FITS frames)
- Writes to `/media/openastroara/.araback/openastroara-backup-YYYY-MM-DDTHH-MM-SS.zip`
- Returns the zip's metadata + download URL

`GET /api/v1/server/backup/{filename}` → downloads the zip to WILMA.

`POST /api/v1/server/backup/restore` (multipart upload of a zip)
- Server validates zip integrity
- Pre-flight: lists what's in the zip + version compatibility
- User confirms via WILMA
- Server: stops sequence if running, backs up current state to a "pre-restore" snapshot, then unzips the backup zip over the current data, restarts to pick up new state
- WILMA reconnects

Typical zip size: 5-50 MB depending on history (vs the full USB which is GB to TB).

### 43.5 Auto-snapshots on the USB drive

Lowest-priority but cheap insurance against accidental corruption:

- Configurable in Settings → Backup:
  - "Auto-snapshot: Off / Daily at 2 AM / After every sequence"
- Server creates a backup zip (per §43.4) into `/media/openastroara/.araback/` with the daily/sequence-end timestamp
- Auto-prune: keeps last 14 snapshots, oldest auto-deleted

This protects against "my profile got corrupted by a bug" but does NOT protect against drive failure (snapshots are on the same drive).

### 43.6 Restore flow on a fresh Pi

User scenarios:

**Scenario A: Same USB drive, new Pi (most common)**
1. Flash Trixie on the new Pi's SD card, configure Wi-Fi, install openastroara-server per §34.1
2. Plug in the USB drive
3. First-run wizard detects existing ARA data on the drive, prompts: *"Found existing ARA data: 12 profiles, 47 sessions, 3.2 GB calibration library. Use this data?"* → [Use Existing Data] / [Initialize Fresh]
4. Server pins the UUID, registers existing structure, starts in `ready` mode with full history
5. WILMA reconnects automatically (no token per §67)

**Scenario B: Fresh USB drive + backup zip**
1. Same as above, but the USB is fresh
2. Plug in USB, run first-run wizard → server initializes empty structure
3. In WILMA: Settings → Backup → Restore from ZIP → upload `openastroara-backup-*.zip`
4. Server restores profiles + sequences + DB metadata
5. Note: calibration FITS files NOT in the backup zip; only the metadata. User must separately recover those from a drive clone if needed.

**Scenario C: Fresh USB drive + no backup (rebuilding from scratch)**
1. Plug in fresh USB, first-run wizard initializes
2. User goes through profile-setup wizard (§37) fresh
3. No historical data; user starts from zero

### 43.7 Backup metadata

Every backup zip has a top-level `manifest.json`:

```json
{
  "schemaVersion": "openastroara-backup-v1",
  "createdAt": "2026-05-19T03:14:15Z",
  "createdBy": "ara-pi-observatory",
  "serverVersion": "0.0.1-ara.1",
  "contents": {
    "profiles": 12,
    "sequences": 47,
    "templates": 4,
    "frames_metadata_rows": 18420,
    "calibration_metadata_files": 153
  },
  "totalSizeBytes": 24123456
}
```

Restore endpoint validates `schemaVersion` against current ARA Core's known versions before proceeding.

### 43.8 What's NOT backed up

These are deliberately excluded:

- **FITS frames** (`captures/` and `calibration/` FITS files) — too large for a portable ZIP. User backs these up via drive clone (§43.3). The DB has all metadata pointing at the frame paths, so restoring a backup with no FITS files leaves the DB "pointing at missing files" — WILMA shows these frames with a missing-file indicator and a "scan for files" button to relocate (e.g., user manually copied FITS to a different USB).
- **HiPS tile downloads on WILMA** — re-downloadable from CDS (§36)
- **WILMA bundled catalogs** — re-installable from app build (§36.1)
- **System logs older than 14 days** — pruned

### 43.9 Settings → Backup panel (WILMA)

```
┌──────────────────────────────────────────────────────┐
│  Backup                                              │
│  ──────────────────────────────────────────────────  │
│  Primary backup: clone the USB drive periodically    │
│  → [Open DEPLOY.md instructions]                     │
│                                                       │
│  Server backup ZIP:                                   │
│    Last created: 2 days ago (24 MB)                   │
│    [Create Backup Now]  [Download Last Backup]        │
│                                                       │
│  Auto-snapshot to USB:                                │
│    [ Off ▼ ]                                          │
│    Options: Off / Daily at 2 AM / After every session │
│                                                       │
│  Restore:                                             │
│    [Restore from ZIP] (select a .zip from this device)│
└──────────────────────────────────────────────────────┘
```

### 43.10 Best-practice recommendation in README

> **Back up your USB drive.** ARA stores everything on your USB drive — profiles, sequences, sessions, calibration, frames. It's portable and durable, but USB drives can still fail. We strongly recommend cloning your working drive to a second drive every few weeks, and keeping the backup drive in a separate location (or at least a separate room). The drive-to-drive clone takes a few minutes and protects against the one failure mode the "everything on USB" model has: the drive itself dying. **For users with a desktop running WILMA on the same LAN, enable real-time backup streaming (§44) to get a continuously-mirrored copy of new frames on your PC, so even an unexpected USB failure mid-session loses at most the last in-flight frame.**

---

## 44. Real-time backup stream to desktop WILMA

Layer 2 of the backup strategy from §43.2. Optional, opt-in feature that pulls each newly-captured FITS file from the Pi to a desktop WILMA in real time during imaging. Result: the PC has a continuously-updating mirror of the Pi's frames. If the USB drive dies overnight, the user wakes up to find every captured frame already on their desktop.

### 44.1 What it does

- After each FITS file is finalized on the Pi (post-capture, post-preview-generation), the Pi enqueues the file for backup streaming
- A desktop WILMA, if connected with backup-stream enabled, polls the queue and pulls each file via HTTP
- WILMA writes to a user-configured local directory (default `~/Documents/OpenAstroAra/Backups/<pi-hostname>/<session>/<filter>/`)
- Verifies SHA-256 after download; re-requests on mismatch
- Acknowledges Pi when stored locally so the Pi can mark "synced to this WILMA"

### 44.2 Why pull-based, not push

WILMA could theoretically expose an HTTP endpoint for the Pi to POST to, but:
- WILMA on macOS/Windows behind home NAT has no stable inbound port
- WILMA mobile may be on cellular with no inbound connectivity
- Browser-style firewalls block inbound by default

WILMA pulling FROM the Pi (Pi is the LAN-stable listener) just works. No port forwarding, no firewall exceptions on the desktop.

### 44.3 Restrictions

- **Desktop WILMA only** (Windows, macOS, Linux desktop). Mobile companion mode (§41) does NOT participate — phones don't have terabytes of storage and would burn cellular data.
- **Same LAN recommended** but not required (can work over VPN, just slower)
- **Single active stream target per Pi for v0.0.1** — only one WILMA at a time is the "backup target." If two desktops both enable backup stream, Pi designates whichever connects first as the active one and tells the other "another WILMA is already streaming."
- v0.1.0 may add multi-target (mirror to two PCs simultaneously)

### 44.4 Bandwidth throttling

Streaming runs in the background and must not interfere with primary operations (live preview, WebSocket status, current sequence capture). Two control mechanisms:

**Token bucket bandwidth limit** (default 50% of measured uplink):
- WILMA measures effective bandwidth on first connection (one-time HTTP throughput test)
- Bucket refill rate: configurable in Settings → Backup → "Stream bandwidth limit"
- User can override to a fixed value (e.g., "10 Mbps") or set to "Use all available bandwidth"

**Capture-aware backoff**:
- During an active exposure: streaming throttles to ~10% of normal rate (avoid USB I/O competition on the Pi)
- Between exposures (filter changes, dithering, idle): streaming runs at full configured rate
- During dawn/dusk idle: full rate
- WILMA + Pi coordinate via the existing WebSocket events (`exposure.started`, `exposure.complete`)

### 44.5 Endpoints

| Method | Path | Purpose |
|---|---|---|
| `GET` | `/api/v1/server/backup-stream/status` | Returns `{ "enabled": bool, "active_target": "wilma-hostname", "pending_count": N, "synced_count": M, "queue_size_bytes": N }` |
| `POST` | `/api/v1/server/backup-stream/claim` | WILMA claims the stream slot for this Pi (returns 200 + slot ID + active target hostname for the takeover UX, or 409 if another WILMA is active). No auth per §67. |
| `POST` | `/api/v1/server/backup-stream/release` | WILMA voluntarily releases the slot (e.g., disk full, user opted out) |
| `GET` | `/api/v1/server/backup-stream/queue?limit=N` | Returns list of pending frames: `[ { "id", "sha256", "size_bytes", "captured_at", "session_id" } ]`, ordered oldest first |
| `GET` | `/api/v1/frames/{id}/fits` | (existing per §40.3) — Pulls the FITS bytes |
| `POST` | `/api/v1/server/backup-stream/ack` | WILMA acknowledges successful storage. Body: `{ "frame_id", "sha256_verified" }`. Pi marks the frame `synced_to_target: <wilma>`. |

### 44.6 State on the Pi

Sessions DB gains per-frame columns:
```sql
ALTER TABLE frames ADD COLUMN sync_target TEXT;       -- e.g., "wilma-mac-joey"
ALTER TABLE frames ADD COLUMN synced_at TIMESTAMP;    -- null if not yet synced
ALTER TABLE frames ADD COLUMN sha256 TEXT;            -- computed at capture time
```

Queue is implicit: `SELECT * FROM frames WHERE sync_target = '<active>' AND synced_at IS NULL ORDER BY captured_at`.

### 44.7 State on WILMA

WILMA's local DB (SQLite at `<wilma-data>/backup-stream.db`):
```sql
CREATE TABLE stream_state (
  pi_hostname TEXT PRIMARY KEY,
  local_root TEXT,           -- where to write files
  last_synced_frame_id TEXT,
  total_bytes_received INT,
  enabled BOOLEAN
);

CREATE TABLE local_frames (
  frame_id TEXT PRIMARY KEY,
  local_path TEXT,
  sha256 TEXT,
  pulled_at TIMESTAMP
);
```

### 44.8 User flow

1. User opens WILMA on desktop, navigates to Settings → Backup → Stream from Pi
2. Toggle: "Stream new frames to this device": [Off / On]
3. If turned On: WILMA calls `POST /api/v1/server/backup-stream/claim`. If another WILMA is already claimed: error "Another desktop is already streaming from this Pi (<hostname>). Disconnect it first."
4. User picks a local storage path (default `~/Documents/OpenAstroAra/Backups/<pi-hostname>/`)
5. WILMA shows storage estimate: "Pi has 47 GB of frames not yet on this device. Estimated download time at current bandwidth: 2h 14m. Free space on chosen drive: 412 GB."
6. Streaming begins; status visible in:
   - Persistent footer indicator: *"Backup stream: 12 of 144 frames synced (8.4 GB)"*
   - Dashboard tile: progress bar + estimated time remaining
   - Per-frame icon in Image Library: 🟢 (synced to this PC) / ⚪ (on Pi only)

### 44.9 Failure modes

| Failure | Behavior |
|---|---|
| WILMA closed mid-stream | Pi keeps frames; queue waits; resumes when WILMA reconnects |
| Network drops | Both sides retry with backoff; queue persists on Pi |
| SHA-256 mismatch on download | WILMA re-requests the file; logs an integrity error |
| WILMA disk fills | WILMA stops pulling, releases the slot, surfaces a clear notification: *"Backup stream paused — only 1.2 GB free on backup drive. Free space and re-enable."* |
| Pi USB unmounts mid-stream | §29.1.2 handles; stream pauses; resumes on remount |
| User unplugs Pi entirely | Frames already streamed are safe on WILMA. Frames not yet streamed are gone if the USB was the only copy. |

### 44.10 What this protects against (the headline benefit)

Compared to drive-clone backups (which happen weekly) and ZIP backups (which happen on user demand), the real-time stream protects against **mid-session USB drive failure**. The worst case becomes: lose the in-flight FITS frame (the one being captured at the instant of failure). All previously-captured frames are safe on the PC.

Combined with §29's mandatory-USB design, this makes ARA's reliability model significantly stronger than NINA's (where the only protection against drive failure is the user remembering to copy files off).

### 44.11 NOT in v0.0.1 scope

Deferred to v0.1.0:
- **Multi-target streaming** (mirror to two desktops simultaneously)
- **Cloud streaming** (rclone-based push to S3, Google Drive, etc.) — same protocol model but pull from a third-party endpoint
- **Selective stream** (only stream frames matching certain filters / rated 3⭐+) — initial version streams everything
- **WAN-friendly stream** (compressed transfer, delta-encoding, etc.) — initial version uses raw FITS over plain HTTP

---

## 45. Polar alignment — iPolar-style continuous loop

### 45.1 Why not three-point polar alignment (TPPA)

NINA's TPPA plugin slews to 3 widely-separated points, plate-solves each, and computes alignment from the geometric inconsistencies. It works, but:

- **Fragile** — tiny mount adjustments cause wild reported-error swings because each plate-solve carries solver noise, and that noise propagates into the alignment vector
- **Slow over Alpaca/HTTP** — main camera at full resolution = 50+ MB FITS per point × 3 points × multiple iterations = lots of waiting on transfers
- **Bad UX** — adjust the knob, wait 30s for the next solve, see the error jumped instead of decreased, adjust again, repeat

ARA drops TPPA entirely. The user's tip-of-the-spear hardware (iPolar) shows the better path: a tight feedback loop with small images and a visual aim point.

### 45.2 iPolar's approach + ARA's adaptation

iPolar uses a **dedicated small camera on the RA axis with a ~13° FOV pointed at the pole**. It plate-solves locally, shows a simple bullseye that turns green when aligned. Continuous loop, snappy, accurate because it measures the RA axis directly.

ARA does the same workflow but **with the user's main imaging camera** (no extra hardware required), using optimizations to overcome the size/speed problem:

1. **Autofocus first** — sharp stars solve faster and more reliably
2. **One dark-frame capture for noise subtraction** — clean signal at short exposures
3. **Bin frames aggressively** (2×2, 3×3, or 4×4 depending on camera capability) — drastically smaller FITS, fast transfer, plate-solve still works because the few stars needed for PA are bright
4. **Loop at ~500 ms** like iPolar — fast feedback, smoothed errors
5. **Zooming bullseye UI** — magnifies as the user converges toward the pole

This gives ARA users iPolar-quality alignment WITHOUT requiring a dedicated PA camera. v0.1.0 will add native support for an actual iPolar / PoleMaster / dedicated PA camera (see §45.10).

### 45.3 Workflow

```
1. User taps "Polar Align" in WILMA
2. Server prompts: "Roughly point your mount at the celestial pole"
   (Polaris in N hemisphere, Sigma Octantis area in S hemisphere)
3. User confirms rough alignment
4. Server: runs autofocus on main camera (fast — uses bundled exposure
   defaults from profile)
5. Server: captures 1 dark frame at the PA exposure/bin/temp (~3 sec)
6. Server: enters continuous loop:
     a. Capture frame at PA exposure (typically 0.5-1s), apply binning
     b. Dark-subtract using the cached dark from step 5
     c. Plate solve (ASTAP, small downsampled FITS)
     d. Compute mount RA axis vs celestial pole offset
     e. Push WebSocket event with offset vector + small JPEG preview
7. WILMA renders zooming bullseye:
     - Red zone when error > 1°
     - Yellow when 10' to 1°
     - Green when < 10'
     - Arrow indicates which way to adjust altitude/azimuth knobs
     - Numerical readout: "Az: -23' Alt: +14'"
     - Live frame preview in corner (small)
8. User adjusts mount alt/az knobs while watching bullseye live
9. When error is within user's target tolerance (default 1 arcmin),
   user taps [Done] — server logs the achieved error to session DB
   and the polar-alignment workflow ends
10. If user aborts or backs out: server kills the loop, mount stays
    where it was (no slewing back to "home" or anything)
```

### 45.4 Binning per camera

Server queries the camera's `MaxBinX` / `MaxBinY` (Alpaca) to pick a sensible PA binning:

| Camera sensor size | Recommended bin | Resulting frame | Transfer time (USB 3.0) |
|---|---|---|---|
| Small (e.g., ASI120MM, 1.3 MP) | 1×1 (already small) | ~2.5 MB FITS | ~50 ms |
| Mid (e.g., ASI294MM, ~12 MP) | 3×3 | ~3 MB FITS | ~60 ms |
| Large (e.g., ASI2600MM, ~26 MP) | 4×4 | ~3 MB FITS | ~60 ms |
| Very large (e.g., QHY600M, ~62 MP) | 4×4 (cap) | ~8 MB FITS | ~150 ms |

User can override in Settings → Polar Align if their camera misbehaves at higher bin. Default works for 95% of cameras.

### 45.5 Dark frame caching

- One dark captured at the start of each PA session (~3 seconds with the chosen exposure/bin)
- Cached in-memory on the Pi for the duration of the PA session
- Discarded when PA workflow ends (don't pollute the regular calibration library)
- Optionally save to calibration library if user toggles "Save PA dark to library" — useful for users who do PA at consistent settings

### 45.6 Plate-solve tuning for PA

ASTAP can solve the binned, dark-subtracted, small-FOV PA frames much faster than full sequence frames:

- Search radius: tight (start at last-known pole RA/Dec)
- Downsample factor: 1 (already small)
- Star detection: aggressive (we don't need precise centroids; we need rough plate solution)
- Solve timeout: 5 seconds (if it can't solve in 5s with these stars + small FOV, something's wrong)

### 45.7 Latency budget per loop iteration

| Stage | Time |
|---|---|
| Exposure (binned, short) | 200-500 ms |
| Sensor download from camera over USB | 50-100 ms |
| Dark subtraction | <10 ms |
| Plate solve (ASTAP, RPi 5 CPU) | 100-300 ms |
| Error computation | <5 ms |
| WebSocket push to WILMA | ~10 ms (LAN) |
| WILMA bullseye update | instant |
| **Total per iteration** | **~400-1000 ms** |

The 500 ms aspiration is achievable on Pi 5 with most cameras; older Pi 4 + heavier sensors may land at 700-900 ms. Either way, an order of magnitude better than TPPA's "wait 30s per measurement" loop.

### 45.8 Math — error vector from plate solve

Given:
- `P_pole` = celestial pole position (NCP: RA=0, Dec=+90 for J2000; SCP: RA=0, Dec=−90)
- `P_solved` = where the mount's optical axis currently points (RA/Dec from plate solve)
- `P_mount_axis` = direction the RA axis is pointing (≠ P_solved if mount isn't perfectly polar-aligned)

To find `P_mount_axis`, server captures two frames with the mount rotated in RA by Δ (e.g., 30°). The two solved positions form a great-circle arc; the center of that arc is `P_mount_axis`. Difference between `P_mount_axis` and `P_pole` is the alignment error, decomposed into altitude + azimuth components per the user's site latitude.

Once `P_mount_axis` is known (after the initial 2-frame seed), subsequent single frames update the estimate via plate-solve + reverse-projection (no need to re-rotate the mount continuously).

This is the same math iPolar uses internally; we're just running it on the main camera at lower resolution instead of a dedicated PA cam.

### 45.9 API endpoints + WebSocket events

| Method | Path | Purpose |
|---|---|---|
| `POST` | `/api/v1/polar-align/start` | Initiate PA workflow. Body: `{ "exposure_seconds": 0.5, "bin": 4, "target_tolerance_arcmin": 1.0 }`. Defaults from profile. |
| `POST` | `/api/v1/polar-align/refocus` | Re-run autofocus (e.g., user adjusted focus knob) |
| `POST` | `/api/v1/polar-align/recapture-dark` | Capture a fresh dark (e.g., temp drifted) |
| `POST` | `/api/v1/polar-align/complete` | User marks done; server logs achieved error |
| `POST` | `/api/v1/polar-align/abort` | Cancel; mount stays in place |

WebSocket events broadcast continuously while PA loop is active:

```json
{
  "type": "polar_align.progress",
  "ts": "2026-05-19T03:14:15.234Z",
  "payload": {
    "iteration": 47,
    "azimuth_error_arcmin": -23.4,
    "altitude_error_arcmin": 14.2,
    "total_error_arcmin": 27.3,
    "direction_arrow_deg": 247,
    "zone": "yellow",       // red | yellow | green
    "preview_jpeg_url": "/api/v1/polar-align/preview/47",
    "last_solve_seconds": 0.13,
    "stars_detected": 32
  }
}
```

Failure events:

```json
{
  "type": "polar_align.error",
  "payload": { "reason": "solve_failed", "iteration": 47, "stars_detected": 4 }
}
```

### 45.10 WILMA UI — zooming bullseye

```
┌──────────────────────────────────────────┐
│  Polar Alignment                          │
│  ────────────────────────────────────────  │
│                                            │
│        ╭──────────────────╮                │
│        │   ╭──────────╮   │                │
│        │   │  ╭────╮  │   │                │
│        │   │  │  ↗ │  │   │   ← bullseye   │
│        │   │  │ ●  │  │   │     (zooms in  │
│        │   │  ╰────╯  │   │      as error  │
│        │   │   3.7'   │   │      shrinks)  │
│        │   ╰──────────╯   │                │
│        ╰──────────────────╯                │
│                                            │
│  Az: -23'   Alt: +14'   Total: 27'         │
│  ●●●○○  (red — adjust mount knobs)         │
│                                            │
│  ┌─────┐  ┌─────────┐  ┌────────────┐     │
│  │frame│  │Recapture│  │  Refocus   │     │
│  └─────┘  │  Dark   │  └────────────┘     │
│           └─────────┘                       │
│  [Done — mount is aligned]  [Abort]        │
└──────────────────────────────────────────┘
```

- Bullseye **dynamically zooms** based on current error magnitude: outer ring covers ~5° at start, shrinks to 30' when error < 1°, shrinks to 1' when error < 5'. User always sees their dot near the center with the right scale.
- Arrow inside the dot points the direction the user should move the alt/az knobs
- Color zones: red (>1°), yellow (1° → 10'), green (<10' — within tolerance)
- [Done] button enabled only when in the green zone (configurable)
- [Recapture Dark] for when ambient temp drifts mid-session
- [Refocus] if user re-bumped the focuser
- Live frame preview in the bottom-left corner (small, just confirms "we have stars")

### 45.11 Failure modes

| Failure | Handling |
|---|---|
| Plate solve fails (clouds, no stars) | Loop pauses; WILMA shows "No solve — check sky and try again" with retry counter. After 5 consecutive failures, suggest user check focus / point closer to pole |
| Mount can't rotate in RA for initial 30° seed | Cap to whatever rotation is possible; user shown warning that estimate may be less precise |
| Camera reports cooler instability mid-PA | Notify, recapture dark, continue |
| User's site latitude too far from pole (extreme polar latitudes) | Workflow allows; bullseye math still works; just narrower workable window |
| Southern hemisphere | Same workflow, just plate-solving against SCP region instead of NCP — no code difference; the math uses lat/long from profile |

### 45.12 Profile settings

Polar Alignment section of profile (default values shown):

- Exposure time: 0.5 sec
- Binning: auto (per §45.4)
- Target tolerance: 1 arcmin (controls when [Done] enables)
- Initial RA rotation for seed: 30°
- Loop cadence (target): 500 ms
- Save PA dark to library: off

Configurable in Settings → Polar Align AND in the profile wizard (could go as a brief screen, or as part of the mount config screen — TBD per wizard design).

### 45.13 Session log

After PA completes (or aborts), one row added to a `polar_alignments` table on the Pi:

```sql
CREATE TABLE polar_alignments (
  id TEXT PRIMARY KEY,
  session_id TEXT REFERENCES sessions(id),
  started_at TIMESTAMP,
  ended_at TIMESTAMP,
  final_error_arcmin REAL,
  iterations INT,
  outcome TEXT,             -- "complete" | "aborted" | "failed"
  notes TEXT
);
```

WILMA's Image Library + Dashboard can display: *"Polar alignment quality: 0.7' (last performed 2 nights ago)"* as a small status chip. Gives users awareness of when re-alignment might help.

### 45.14 v0.1.0 — dedicated PA camera support

When v0.1.0 adds support for an iPolar / PoleMaster / dedicated PA camera attached via Alpaca:

- Polar Align workflow auto-detects an Alpaca camera tagged as "PolarAlignCamera" (separate from main imaging camera)
- Uses that camera's FOV / pixel scale for the math instead of binning the main camera
- Same UI, same loop, same math — just smaller frames and faster (no need for main camera autofocus or large transfer)
- User toggles in Settings: "Use dedicated PA camera if available"

### 45.15 What we're explicitly NOT doing

- Three-point polar alignment (TPPA) — dropped per §45.1
- Drift-alignment method (the historical hour-of-RA-drift technique) — too slow + obsolete given plate-solve approach
- Pre-canned "well-known star" alignments (Polaris reticle patterns) — manual workflows, replaced by automated continuous loop

---

## 46. Notifications system

In-app notifications only — no push, no email, no webhooks in v0.0.1 (field users often have no internet). Every meaningful server event becomes a notification. Per-event opt-in/out, quiet hours, four severity levels with distinct UX treatments.

### 46.1 Delivery model

- Server emits events via existing WebSocket connection (the `/api/v1/stream` channel from §9)
- WILMA caches events locally; the **Notification Feed** is the persistent in-app view
- If WILMA is disconnected when an event fires: event is queued in Pi's SQLite `notifications` table; delivered on reconnect (oldest first)
- No third-party services (no FCM, no APNs, no SendGrid). Everything is LAN-local.

### 46.2 Severity levels and UX treatment

| Severity | Toast in WILMA | Audio | Vibration (mobile) | Feed entry | Badge | Acknowledgment |
|---|---|---|---|---|---|---|
| **info** | none (feed only) | — | — | yes | — | passive — auto-marked-read on view |
| **warning** | auto-dismiss toast (5 s) | — | — | yes | +1 | passive |
| **critical** | sticky toast until tapped | one chime | short pulse | yes | +1 | tap to acknowledge |
| **urgent** | full-screen modal | looping alarm (per §35.5) | continuous | yes | +1 | explicit user action required (e.g., [Emergency Abort] or [Acknowledge]) |

Quiet hours suppress info + warning (queue silently). Critical + urgent ALWAYS deliver regardless (safety + equipment failure can't wait).

### 46.3 Event catalog

The complete list of server events that produce notifications, with default severity. All severities are user-overridable per-event (§46.6).

**Sequence lifecycle:**
| Event kind | Default severity |
|---|---|
| `sequence.started` | info |
| `sequence.complete` | info |
| `sequence.paused` | warning |
| `sequence.resumed` | info |
| `sequence.aborted_manual` | warning |
| `sequence.aborted_safety` | critical |
| `target.switched` | info |

**Equipment:**
| Event kind | Default severity |
|---|---|
| `equipment.connected` | info |
| `equipment.disconnected` | warning |
| `equipment.reconnected` | info |
| `equipment.fault` | critical |

**Safety (also handled by §35 alarm system):**
| Event kind | Default severity |
|---|---|
| `safety.unsafe` | urgent |
| `safety.alarm` | urgent |

**Autofocus / plate solve:**
| Event kind | Default severity |
|---|---|
| `autofocus.complete` | info |
| `autofocus.failed` | warning |
| `platesolve.complete` | info |
| `platesolve.failed` | warning |

**Frames + quality:**
| Event kind | Default severity |
|---|---|
| `frame.captured` | **suppressed by default** (would be noisy — opt-in only) |
| `frame.quality_drift` | warning (per §40.7 HFR drift detection) |

**Cooler + guider:**
| Event kind | Default severity |
|---|---|
| `cooler.target_reached` | info |
| `cooler.target_failed` | warning |
| `guider.started` | info |
| `guider.lost_star` | warning |
| `guider.recovered` | info |
| `guider.dither_complete` | info |

**Meridian flip:**
| Event kind | Default severity |
|---|---|
| `meridian_flip.imminent` | info (with user-configurable advance warning, default 15 min) |
| `meridian_flip.starting` | info |
| `meridian_flip.complete` | info |
| `meridian_flip.failed` | critical |

**Polar align:**
| Event kind | Default severity |
|---|---|
| `polar_align.complete` | info |
| `polar_align.failed` | warning |

**Recovery (post-crash, per §28):**
| Event kind | Default severity |
|---|---|
| `recovery.started` | warning |
| `recovery.complete` | info |
| `recovery.failed` | critical |

**Storage (per §29 + §43):**
| Event kind | Default severity |
|---|---|
| `storage.low_space` | warning (configurable threshold; default <5% free) |
| `storage.unmounted` | urgent |
| `storage.remounted` | info |
| `backup.complete` | info |
| `backup.failed` | warning |
| `backup_stream.paused` | warning (§44 — disk full on WILMA, etc.) |

**Time / location:**
| Event kind | Default severity |
|---|---|
| `time_sync.required` | warning |
| `time_sync.drift_detected` | warning (drift > 30 s mid-session per §31) |

**Server lifecycle:**
| Event kind | Default severity |
|---|---|
| `server.starting` | info |
| `server.shutdown_imminent` | warning |
| `update.available` | info (per §33) |
| `update.applied` | info |
| `update.failed` | warning |

**Environmental:**
| Event kind | Default severity |
|---|---|
| `dew_detected` | warning (per §42) |
| `network.weak_signal` | warning (WILMA-side connection quality) |

### 46.4 WebSocket event shape

```json
{
  "type": "notification",
  "ts": "2026-05-19T03:14:15.234Z",
  "payload": {
    "id": "ntf_8K2x...",
    "event_kind": "frame.quality_drift",
    "severity": "warning",
    "title": "Image quality degrading",
    "body": "Autofocus completed twice in 12 min but HFR keeps rising — possible clouds. Check sky conditions.",
    "related": {
      "session_id": "sess_abc",
      "frame_id": "frm_xyz",
      "equipment_id": null
    },
    "actions": [
      { "label": "Pause Sequence", "endpoint": "/api/v1/sequence/pause" },
      { "label": "Dismiss", "endpoint": null }
    ]
  }
}
```

Some events come with optional action buttons (e.g., a `frame.quality_drift` notification offers [Pause Sequence] right in the toast).

### 46.5 Persistent storage on the Pi

```sql
CREATE TABLE notifications (
  id TEXT PRIMARY KEY,
  ts TIMESTAMP NOT NULL,
  event_kind TEXT NOT NULL,
  severity TEXT NOT NULL,           -- "info"|"warning"|"critical"|"urgent"
  title TEXT,
  body TEXT,
  payload JSON,                     -- full event-specific data
  related_session_id TEXT,
  related_frame_id TEXT,
  related_equipment_id TEXT,
  acknowledged BOOLEAN DEFAULT 0,
  acknowledged_at TIMESTAMP,
  acknowledged_by TEXT              -- which WILMA acknowledged
);
```

Auto-prune: keep last 30 days of info + warning; keep all critical + urgent indefinitely.

### 46.6 User preferences

Settings → Notifications panel in WILMA:

**Per-event opt-in/out** — list of all event kinds (§46.3) with:
- Toggle: notify yes/no
- Severity override: dropdown (info / warning / critical / urgent / suppressed)
- e.g., user can promote `frame.quality_drift` to critical, demote `cooler.target_reached` to suppressed

**Quiet hours:**
- Toggle: enable quiet hours
- Time range: start time → end time (server's local TZ)
- During quiet hours:
  - info: suppressed (still goes to feed; no toast/audio)
  - warning: suppressed (still goes to feed; no toast/audio)
  - critical: delivered with reduced volume audio (50%)
  - urgent: delivered at full volume

**Defaults pre-filled** by the §37 wizard's notification screen (or implicitly with sensible defaults if user skips):

```json
{
  "quiet_hours": { "enabled": false, "start": "23:00", "end": "06:00" },
  "events": {
    "frame.captured": { "enabled": false },           // opt-in
    "target.switched": { "enabled": true, "severity": "info" },
    "sequence.complete": { "enabled": true, "severity": "info" },
    "safety.unsafe": { "enabled": true, "severity": "urgent" },
    "storage.unmounted": { "enabled": true, "severity": "urgent" },
    // ... rest at default per §46.3
  }
}
```

### 46.7 Notification feed UI (WILMA)

```
┌──────────────────────────────────────────────────────┐
│  Notifications                            ⚙  Mark all read  │
│  ──────────────────────────────────────────────────  │
│  🔴  Storage disconnected             3 min ago      │
│      USB drive disconnected. Sequence paused.        │
│      [Open Storage Settings]                          │
│                                                       │
│  🟠  Image quality degrading          12 min ago     │
│      Autofocus ran twice; HFR keeps rising —          │
│      possible clouds. [Pause Sequence]                │
│                                                       │
│  🔵  Target switched: M42 → NGC 6188  47 min ago     │
│                                                       │
│  🔵  Autofocus complete on L          1h 12m ago     │
│      HFR 1.42 → 1.18                                  │
│                                                       │
│  ... (older entries below, virtualized scroll)        │
└──────────────────────────────────────────────────────┘
```

- Severity icons: 🔵 info, 🟡 warning, 🟠 critical, 🔴 urgent
- Tap action button → executes the linked endpoint, marks acknowledged
- Tap row body → opens related session/frame if applicable
- Filter pills at top: [All] [Unread] [Critical+] [Last hour] [Last 24h]
- Persistent badge count in main app shell's notifications icon

### 46.8 API endpoints

| Method | Path | Purpose |
|---|---|---|
| `GET` | `/api/v1/notifications` | List notifications (paginated, filterable by severity / acknowledged / event_kind / date range) |
| `GET` | `/api/v1/notifications/{id}` | Single notification full payload |
| `POST` | `/api/v1/notifications/{id}/acknowledge` | Mark acknowledged |
| `POST` | `/api/v1/notifications/acknowledge-all` | Bulk acknowledge by filter |
| `DELETE` | `/api/v1/notifications/{id}` | Remove (rare — typically auto-pruned) |
| `GET` | `/api/v1/notifications/preferences` | Get user's notification preferences |
| `PUT` | `/api/v1/notifications/preferences` | Update preferences |

### 46.9 v0.1.0 expansion paths

Out of scope for v0.0.1, queued in GAPS-ARA for future:

- **Push notifications** (FCM / APNs) — requires Firebase + Apple Developer accounts + privacy review
- **Email integration** — outbound SMTP from Pi (requires user to configure their mail server)
- **Discord / Slack webhooks** — POST notification payloads to user-configured webhook URLs
- **Generic webhook** — same shape, user-pasted URL
- **Notification scripting** — user-defined IFTTT-style "when X happens, do Y" rules (e.g., "when sequence.complete fires after 11pm, send IFTTT trigger to turn off observatory lights")

### 46.10 What "in-app only" means for unattended operation

The user said it best: "user may not have internet." In-app-only means:
- All notifications are deferred until WILMA reconnects
- Pi imaging continues regardless — the sequence doesn't pause just because WILMA isn't subscribing
- User wakes up, opens WILMA, sees the full feed of overnight events sorted by severity
- Critical events (USB unmount, safety abort) are still acted on by the Pi at the moment they happen (via safety policies §35); the notification just records "this happened and the policy fired"

---

## 47. Mosaic imaging (multi-panel)

Astrophotographers shoot multi-panel mosaics when a target is too large for one frame at their focal length — Andromeda at 2000mm, the Veil Nebula, Heart-and-Soul region, etc. NINA's Framing Assistant supports this; ARA preserves and modernizes the workflow with Aladin Lite integration + mosaic-aware tracking.

### 47.1 What mosaic mode does

User defines an N×M grid of overlapping panels centered on a target. Each panel becomes a sub-target with a computed RA/Dec offset. The sequencer captures all panels (light + calibration). Stitching happens later in post-processing (PixInsight, Siril, AstroPixelProcessor — ARA does not stitch).

### 47.2 Building a mosaic in WILMA's Framing Assistant

UI flow:

1. User searches for a target in Framing Assistant (Aladin Lite — §25.5)
2. Sets **Mosaic** mode: defines grid cols × rows (e.g., 3 × 2)
3. Sets overlap percentage (default **10%**; configurable 5-25%)
4. Optionally sets rotation angle (defaults to 0 if no rotator; else profile default)
5. Aladin Lite overlay renders the panel grid as colored rectangles on the sky map — user sees exactly which areas each panel covers, can drag the whole mosaic to recenter, can rotate
6. User confirms → mosaic saved as a single logical entity with N×M panels

### 47.3 Panel math (computed server-side)

Given:
- `f` = telescope focal length (mm)
- `w_sensor`, `h_sensor` = sensor dimensions (mm) — derived from camera's pixel size × pixel count
- `overlap` = fractional overlap (0.10 = 10%)
- `cols`, `rows` = grid dimensions
- `center_ra`, `center_dec`, `rotation` = mosaic anchor

Per-panel field of view: `panel_fov_x = atan(w_sensor / f)`, `panel_fov_y = atan(h_sensor / f)` (radians)

Inter-panel center offset: `step_x = panel_fov_x * (1 - overlap)`, `step_y = panel_fov_y * (1 - overlap)`

Panel `(c, r)` center (where `c ∈ [0, cols-1]`, `r ∈ [0, rows-1]`):
```
dx = (c - (cols-1)/2) * step_x
dy = (r - (rows-1)/2) * step_y

# Rotate (dx, dy) by mosaic rotation
dx', dy' = rotate(dx, dy, mosaic_rotation)

# Convert to spherical offset from center
panel_ra  = center_ra  + dx' / cos(center_dec)
panel_dec = center_dec + dy'
```

(Spherical-projection corrections for large mosaics near the poles are needed in practice; ARA uses standard tangent-plane projection per NINA's existing math.)

**RA wrap handling (0h / 24h boundary):** per-panel `center_ra` is normalized via `((computed_ra + 360) % 360) % 360` before being passed to the mount or stored. Panels straddling RA 0h work correctly — e.g., a 6-panel mosaic centered at RA 23h45m with 1° panel spacing produces panel centers spanning 23h42m, 23h44m, 23h46m, 23h48m, 23h50m, 23h52m (and possibly wrapping into 00h01m, 00h03m for wider mosaics). NINA's `Coordinates` class handles this normalization correctly per the inherited math (`OpenAstroAra.Astrometry/Coordinates.cs`). §14.5 integration test suite includes a mosaic-across-RA-wrap test case (e.g., 5×5 mosaic centered at RA 23h55m) to prevent regression.

### 47.4 Panel scheduling — interleaved

ARA's sequencer runs panels in **interleaved order** by default (not sequential):

```
Instead of: panel(0,0) all-filters all-frames → panel(0,1) all-filters → ...
ARA does:   for filter in filters:
              for frame_index in range(per_panel_count):
                for (c,r) in panels:
                  slew to panel(c,r), capture 1 frame, dither
```

Why interleaved:
- All panels see roughly the same airmass, seeing, sky brightness at each stage
- Calibrating + stitching is more consistent
- Mid-session abort = each panel has roughly proportional data, not "panel 1 done, panel 6 zero"

Cost:
- More slews between panels (each transition = slew + plate-solve, ~30 s)
- Plate-solve verification per panel transition ensures position is correct

User can switch to **sequential** mode in the mosaic's settings if they prefer (e.g., very tight schedule, prefers to "finish" panels one at a time). Setting per-mosaic, not global.

### 47.5 Per-panel sub-target handling

Each panel is a target in the existing target system (§40) with:
- `name`: `"M31 Mosaic — panel 0,0"`, `"M31 Mosaic — panel 0,1"`, etc.
- `mosaic_id`: foreign key to the parent mosaic
- `panel_col`, `panel_row`: position within grid
- `center_ra`, `center_dec`, `rotation`: computed coordinates

Plate solving, autofocus, frame loop, dithering — all run per panel exactly as for any other target. The recovery flow (§28) works per panel.

### 47.6 Schema

```sql
CREATE TABLE mosaics (
  id TEXT PRIMARY KEY,
  name TEXT NOT NULL,            -- "M31 Mosaic", "Heart and Soul"
  center_ra REAL NOT NULL,
  center_dec REAL NOT NULL,
  rotation REAL DEFAULT 0,
  grid_cols INT NOT NULL,
  grid_rows INT NOT NULL,
  overlap_pct REAL DEFAULT 0.10,
  panel_fov_x_arcmin REAL,        -- computed at creation
  panel_fov_y_arcmin REAL,
  scheduling TEXT DEFAULT 'interleaved',  -- 'interleaved' | 'sequential'
  created_at TIMESTAMP,
  notes TEXT
);

CREATE TABLE mosaic_panels (
  mosaic_id TEXT REFERENCES mosaics(id),
  panel_col INT,
  panel_row INT,
  center_ra REAL,
  center_dec REAL,
  -- target row in `targets` table is created on demand; lookup by mosaic_id + panel_col + panel_row
  PRIMARY KEY (mosaic_id, panel_col, panel_row)
);
```

### 47.7 API endpoints

| Method | Path | Purpose |
|---|---|---|
| `POST` | `/api/v1/mosaics` | Create a mosaic (body: name, center coords, grid, overlap, rotation) |
| `GET` | `/api/v1/mosaics` | List user's mosaics |
| `GET` | `/api/v1/mosaics/{id}` | Full state — grid, panels, completion % per panel |
| `GET` | `/api/v1/mosaics/{id}/panels` | Per-panel detail with frame counts per filter |
| `PATCH` | `/api/v1/mosaics/{id}` | Update overlap / rotation / scheduling (only if no frames captured yet) |
| `DELETE` | `/api/v1/mosaics/{id}` | Remove (panels remain as standalone targets if user wants) |
| `POST` | `/api/v1/mosaics/{id}/build-sequence` | Generate a sequence for the mosaic with the user's filter list + per-filter frame count |
| `POST` | `/api/v1/mosaics/{id}/resume` | Generate a sequence for **incomplete panels only** (per §47.9) |

### 47.8 Storage layout for mosaic captures

Under the session's captures dir:

```
captures/<session-id>/
└── M31-mosaic/
    ├── panel-0-0/
    │   ├── L/   (all light frames for panel 0,0 with filter L)
    │   ├── R/
    │   ├── G/
    │   └── B/
    ├── panel-0-1/
    ├── panel-1-0/
    └── panel-1-1/
```

FITS headers include `MOSAIC` (mosaic name), `PANEL` (`"0,0"`), `PANELRA`, `PANELDEC`, plus all the standard session metadata from §39.3. Post-processing tools can use `MOSAIC` + `PANEL` to identify and group frames.

### 47.9 Mosaic-aware Resume Target

The §40.6 "Resume Target" workflow extends naturally to mosaics:

1. User picks a mosaic from the Image Library
2. WILMA calls `POST /api/v1/mosaics/{id}/resume`
3. Server analyzes panel completion:
   - Per panel, per filter: count frames vs target frame count from the original sequence
   - "Complete" = at least N frames per filter (configurable per panel/filter via "per-filter target count")
   - "Incomplete" = below threshold
4. Server returns a sequence draft that contains ONLY incomplete panels' filter passes, interleaved
5. New frames write to the same `captures/<session-id>/M31-mosaic/panel-X-Y/<filter>/` structure with `MOSAIC`/`PANEL` headers — they roll up cleanly into the existing mosaic rather than creating a duplicate

This means **a mosaic project across years**: user can shoot 2 panels one night, 2 more the next clear week, 2 more 6 months later, and ARA tracks panel-completion across sessions. WILMA's mosaic detail view shows the grid colored by completion (red = 0 frames, yellow = partial, green = complete).

### 47.10 Image Library — mosaic rollup view

In §40 Image Library, mosaics appear as a top-level grouping (alongside individual targets):

```
▼ M31 Mosaic — 3×2 grid, 10% overlap
   17h 42min total · 6 panels, 5 complete, 1 partial
   [Visualize Grid]  [Resume Mosaic]  [Capture Matching Flats — All Panels]

   ┌────┬────┬────┐
   │ ✅ │ ✅ │ ✅ │   row 1 — fully complete
   ├────┼────┼────┤
   │ ✅ │ ✅ │ 🟡 │   row 0 — panel (2,0) needs 6 more L + 4 R
   └────┴────┴────┘

   [drill-in: per-panel frame lists]
```

The visualization is the actual sky layout (rotated per mosaic rotation) so user sees the spatial relationship of completed vs incomplete panels.

### 47.11 What ARA does NOT do (post-processing concerns)

- **Stitching** — combining the N×M panels into a single image. User does this in PixInsight (mosaic plugin), Siril, ICE, AstroPixelProcessor, etc. ARA's job is capture + metadata; stitching is the user's processing pipeline.
- **Star matching across panel overlap regions** — same as above
- **Color calibration across panels** — same

ARA's contribution: ensure every panel's FITS file has consistent metadata, panels are captured under similar conditions (interleaved scheduling), and the user can return for missing data years later.

### 47.12 Profile defaults (set in §37 wizard or post-hoc Settings)

- Default mosaic overlap: 10%
- Default scheduling: interleaved
- Default per-filter frame count per panel: inherit from user's standard sequence preferences
- Mosaic naming pattern: `<target> Mosaic` (e.g., "M31 Mosaic")

### 47.13 v0.1.0 expansion paths

- Adaptive panel sizing (variable focal length / FOV per panel — for super-wide mosaics combining wide-field and tighter panels)
- ARA-side stitching preview (low-res, just for sanity-check before user processes in PixInsight)
- Drift mosaicking (target drifts through FOV without slewing — for fast wide-field captures)

---

## 48. Auto-flats and dark library (sequence automation)

§39 covers the calibration philosophy + session-metadata-driven matching flats from past sessions. This section covers the *automation* layer — sequence step types that capture calibration during/after the imaging session without user intervention.

### 48.1 The "calibrate now or later" prompt (sequence start)

When a user starts a sequence, WILMA presents a one-time prompt asking whether to capture flats tonight:

```
┌──────────────────────────────────────────────────────┐
│  Capture calibration tonight?                        │
│  ──────────────────────────────────────────────────  │
│  Your sequence will use: L, R, G, B filters          │
│                                                       │
│  ○ Yes — flat panel at end of session                │
│  ○ Yes — sky flats at twilight                       │
│  ● No — capture them later                           │
│                                                       │
│  💡 ARA can recreate your exact equipment state —    │
│  focus per filter, rotator angle, sensor temp,       │
│  gain, offset — anytime from the Image Library.      │
│  Pick a past session → "Capture Matching Flats" and  │
│  the rig replays the geometry. (§39.5)               │
│                                                       │
│  ☑ Don't ask again — remember my preference          │
│                                                       │
│  [Start Sequence]                                     │
└──────────────────────────────────────────────────────┘
```

Three choices for flats:
- **Yes — flat panel at end of session** → server appends a `FlatPanelFlats` instruction to the sequence after the last imaging instruction
- **Yes — sky flats at twilight** → server appends a `SkyFlats` instruction triggered by morning astronomical twilight
- **No — capture them later** → no auto-append; user understands they can run §39.5 anytime to recreate

This prompt surfaces the §39.5 superpower at exactly the right moment — when the user is deciding whether to spend extra rig-time tonight on calibration or defer with confidence.

### 48.2 Preference persistence

User can opt out of the prompt with "Don't ask again — remember my preference":
- Profile gains a `calibration_capture_default` setting: `"ask" | "panel_at_end" | "sky_at_twilight" | "never"`
- Default = `"ask"` (the prompt is shown each sequence)
- Settings → Calibration in WILMA lets user change later

If "never" is set and user wants to capture occasionally, they manually add a `FlatPanelFlats` or `SkyFlats` instruction to the sequence editor.

### 48.3 Auto-flat step — `FlatPanelFlats` (preserved from NINA)

When the sequence reaches the `FlatPanelFlats` instruction (typically appended at end-of-night):

1. Server queries the sequence's prior instructions to detect which filters were used (live, from session DB)
2. For each filter:
   - Slew mount to the flat-friendly position (default: park position, or user-configured flat target)
   - Switch filter wheel to current filter
   - Move focuser to that filter's focus offset (so flats match imaging conditions)
   - Hold rotator at current angle (CAA)
   - Turn on flat panel, set brightness, wait for stabilization
   - **Auto-exposure**: capture short test exposure, measure median ADU, adjust panel brightness OR exposure time until target ADU is reached (default ~30000 for 16-bit cameras = ~45% full scale)
   - Capture N flats (default 30)
3. After all filters: turn off panel, park mount (optionally), log completion notification

Inherits NINA's existing implementation; ARA preserves verbatim.

### 48.4 Sky flats variant — `SkyFlats` (preserved from NINA)

When user picks sky flats:

- Sequencer waits until morning astronomical twilight starts (or evening if running backwards)
- Slews to zenith (or user-configured sky-flat target — typically east in evening, west in morning to avoid the brightening/darkening direction)
- For each filter:
  - Auto-exposure to target ADU (sky brightness changes rapidly during twilight, so exposure time must adapt frame-to-frame)
  - Capture N flats per filter; adjust exposure as sky brightens/darkens
  - Stop if sky becomes too bright (overexposure) or too dim (insufficient stars-to-skybackground)

Inherits NINA's implementation.

### 48.5 Dark library — manual user-initiated, not prompted

Darks are NOT included in the sequence-start prompt for v0.0.1 because:
- Darks don't match a specific session (they match camera + gain + temp + exposure — much more reusable)
- Building a dark library is typically a multi-hour overnight task, NOT a "tack onto tonight's imaging" thing
- Users typically build darks on cloudy/moony/full-moon nights when DSO imaging is impossible

User builds a dark library by:
1. Creating a new sequence in WILMA
2. Adding a `DarkLibraryInstruction` (NINA's existing instruction type, preserved)
3. Defining the matrix: list of (exposure, gain, temp) tuples + frames-per-combination
4. Running the sequence (typically overnight, unattended)

`DarkLibraryInstruction` semantics (preserved from NINA):
- For each (exposure, gain, temp) combination:
  - Set cooler target temp, wait for stabilization
  - Capture N frames with the camera's shutter closed (or in front of a dark cap)
  - Move to next combination
- Total runtime: hours to a full overnight, depending on matrix size

Bundled `dark-library.json` template (§38.7) gives users a starting point: typical CMOS combinations (30s × 5 gains × 3 temps × 50 frames = ~7,500 darks, ~7 hours).

### 48.6 Bias library

Bias frames are short-as-possible exposures with shutter closed — captures readout pattern. NINA's `TakeBiasExposures` instruction preserved. Typically:
- One bias library per camera + gain combination
- 100-500 frames per combo (stacks well, easy to capture in 1-2 minutes)
- Updated when user changes gain settings or replaces camera

User initiates manually; not part of the sequence-start prompt.

### 48.7 Calibration preference in profile schema

Profile JSON gains a `calibration` block (inside the existing safety/preferences area):

```json
{
  "calibration": {
    "capture_default": "ask",           // "ask" | "panel_at_end" | "sky_at_twilight" | "never"
    "flat_panel": {
      "target_adu": 30000,              // half of 16-bit full scale
      "target_adu_tolerance_pct": 5,
      "frames_per_filter": 30,
      "post_flat_park_mount": true
    },
    "sky_flat": {
      "target_adu": 25000,
      "frames_per_filter": 20,
      "sky_target_azimuth": 90,          // east in morning, west in evening
      "sky_target_altitude": 75,
      "stop_at_max_adu": 50000,         // bail if oversaturated
      "stop_at_min_adu": 5000           // bail if too dark
    },
    "dark_library": {
      "default_frames_per_combination": 50,
      "default_temp_tolerance_c": 0.5
    }
  }
}
```

### 48.8 Settings → Calibration panel (WILMA)

Mirrors the schema above with editable fields, plus:
- "What ARA will do at sequence start" preview ("Will ask each time" / "Will capture sky flats automatically" / etc.)
- Link to §39.5 "Capture Matching Flats" workflow in Image Library

### 48.9 v0.1.0 expansion paths

- **Scheduled dark library** — "build dark library every Sunday night if no sequence planned" — runs automatically when imaging is impossible
- **Smart dark management** — server identifies when dark library is stale (camera replaced, gain settings changed since last darks captured) and prompts user
- **Bias automation** — same model as flats prompt
- **Sky flat optimal target tracking** — server picks the best position by computing brightness gradient direction at the current twilight time (eastern sky brightens in dawn, western in dusk)

---

## 49. API documentation serving

ARA Core serves interactive Swagger UI documentation from its OpenAPI spec. Open access (no auth, per §67) — both the docs and the API itself, matching ASCOM Alpaca's convention.

### 49.1 Tool choice — Scalar (AOT-compatible Swagger UI replacement)

Original plan was `Swashbuckle.AspNetCore` + classic Swagger UI v5.x. Revised per §71 AOT decision: Swashbuckle relies on runtime reflection over MVC controllers, which is incompatible with `PublishAot=true`.

ARA uses **Microsoft.AspNetCore.OpenApi** (built-in, AOT-friendly, generates the spec at build time from endpoint metadata) to produce the OpenAPI document, and **Scalar.AspNetCore** to render the interactive docs UI. Scalar's rendering is visually similar to Swagger UI v5.x and equally familiar to ASCOM Alpaca consumers ([ascom-standards.org/api/](https://ascom-standards.org/api/) uses the same conceptual surface).

Source-of-truth spec is **generated** from endpoint metadata at build time (code-first per §71.3); `OpenAstroAra.Server/openapi.yaml` is committed for change tracking + Dart client generation but regenerated on every build (CI fails if regen produces a diff).

### 49.2 Endpoints

| Path | Returns |
|---|---|
| `/api/v1/docs` | Swagger UI HTML page (interactive API explorer) |
| `/api/v1/openapi.yaml` | Raw OpenAPI 3.1 spec (YAML) — for tools that consume the spec directly |
| `/api/v1/openapi.json` | Same spec, JSON format — Swagger UI fetches this |

All three are **open** — no auth required. Per §67, *all* endpoints (not just docs) are open in v0.0.1. v0.1.0 remote-access mode will gate state-mutating operations behind TLS + token.

### 49.3 Swagger UI styling

Match ASCOM's customizations for visual consistency:
- Hide the "base URL" input field (it's always the host the user is browsing)
- Hide the Swagger file description text
- Custom CSS pulled in via `Swashbuckle.AspNetCore`'s `InjectStylesheet` option
- Custom title: "OpenAstro Ara API"

```csharp
app.UseSwaggerUI(c => {
    c.SwaggerEndpoint("/api/v1/openapi.json", "OpenAstro Ara v0.0.1");
    c.RoutePrefix = "api/v1/docs";
    c.DocumentTitle = "OpenAstro Ara API";
    c.InjectStylesheet("/swagger-custom.css");
    c.DisplayRequestDuration();
    c.EnableFilter();
});
```

### 49.4 What the spec contains

Per §9 endpoint groups + later additions:

- Server (info, handshake, time-sync, storage, backup-stream, update)
- Equipment (per device type per Alpaca interface)
- Sequence (CRUD + run + import)
- Image (frames + previews + FITS + thumbnails)
- Session (history + matching-flats)
- Polar alignment
- Notifications
- Mosaic
- Calibration library
- Profiles

All request/response shapes typed via OpenAPI components. No authentication scheme declared in v0.0.1 per §67. v0.1.0 remote-access mode will add a bearer-token scheme so Swagger UI's "Authorize" button works against remote endpoints.

### 49.5 "Try It Out" from the docs page

Swagger UI's built-in "Try It Out" works on every endpoint immediately — no auth setup required in v0.0.1 per §67. Useful for:
- Debugging during development
- Power users exploring the API
- Plugin authors testing endpoints before integrating (when plugin SDK ships in v0.1.0)

### 49.6 Where Swagger UI is reachable

Same port as the API (default 5400). On a Pi at `pi-observatory.local`:
- `http://pi-observatory.local:5400/api/v1/docs` — interactive docs
- `http://pi-observatory.local:5400/api/v1/openapi.yaml` — spec

WILMA's About panel can link to `<server>/api/v1/docs` so users discover it.

### 49.7 v0.1.0 expansion

- Generated SDK packages from the OpenAPI spec for popular languages (Python, JavaScript, Go) — useful for plugin authors + community integrations
- Versioned doc browser (current v0.0.1, future v0.1.0, etc.) — Swagger UI supports multi-spec selection

---

## 50. Session analytics + Stats dashboard

ARA's analytics layer is the v0.0.1 feature designed to **leave NINA in the dust**. NINA captures rich session metadata but exposes almost none of it as insight. ARA mines that data to surface trends, correlations, equipment health, and milestones users have never had access to.

### 50.1 Why this matters as a differentiator

Astrophotography is high-effort, low-feedback. A user images for 6 hours, processes for another 6, and only then discovers their focuser drifted, their guiding got worse after midnight, or they wasted half the session on a target that was setting. ARA's analytics layer **closes that feedback loop**:

- "Your HFR creeps up 0.4 per 10°C drop — your tempcomp slope is wrong"
- "Guiding RMS doubles below 25° altitude — stop imaging Dragons before they're up that high"
- "Your camera's cooler power has crept up 18% over 6 months — sensor may be degrading"
- "You've spent 47h on Andromeda this year. Total integration target: 60h. ETA at your pace: 3 more clear weekends."
- "Tonight's frames have 0.3 better median HFR than your last M42 session — conditions are excellent"

NINA shows none of this. ARA does. It's a real differentiator because **the data is already captured** (per §39.3 session DB schema) — we just compute over it.

### 50.2 Data foundation — no new collection

All analytics derive from data already captured:

- `sessions` table (per §39.3): start/end, target, total frames, faults, recovery events
- `frames` table (per §39.3): per-frame HFR, star count, ADU stats, focus position, rotator angle, temp, filter, gain, offset, captured-at
- `polar_alignments` table (per §45.13): final error, iterations
- `faults` table (per §42.5): timeline of equipment issues
- `notifications` table (per §46.5): event log
- PHD2 guide log: imported into a `guide_samples` table during session (RMS values per sample)
- Weather station data: ambient temp, humidity, dew point at capture time (already in FITS headers per §39.3, also indexed)

The analytics layer adds **one** new computation step: a per-frame **composite quality score** (per §50.10), stored as `frames.quality_score` REAL column. Computed at capture time + on-demand recomputation if scoring algorithm updates.

### 50.3 Stats dashboard structure

New top-level tab in WILMA's main app shell, alongside Sequence/Imaging/Sky Atlas/Image Library/Logs: **Stats**.

Sub-views (left rail navigation):

```
Stats
├── Overview              — landing page, recent-night summary + headline metrics
├── Targets               — per-target rollups, progress tracking
├── Focus & Temperature   — HFR-vs-temp analysis per filter
├── Guiding               — RMS trends, correlations
├── Frame Quality         — distribution + composite score histograms
├── Equipment Health      — cooler power trend, mount accuracy, fault rates
├── Session Efficiency    — time breakdown, frame yield
├── Conditions            — frame quality correlated with weather + lunar
├── Achievements          — milestones + records (lightly gamified)
├── Calendar              — heatmap of imaging nights
└── Exports               — PDF/CSV/Astrobin output
```

### 50.4 Overview landing page

The default landing when user opens Stats. Six tiles:

```
┌────────────────┬────────────────┬────────────────┐
│  Last Night    │  This Month    │  This Year     │
│  4h 12m        │  47 hours      │  312 hours     │
│  144 frames    │  9 sessions    │  84 sessions   │
│  M42 + NGC6188 │  4 targets     │  18 targets    │
├────────────────┼────────────────┼────────────────┤
│  Best Target   │  Health Status │  Streak        │
│  Andromeda     │  All systems   │  🔥 4 nights   │
│  47h total     │  green         │  in a row      │
└────────────────┴────────────────┴────────────────┘
```

Below the tiles: 3-4 recent notifications + 1 hero chart (the year's monthly integration totals as a bar chart).

### 50.5 Targets view

Per-target rollups, sortable + searchable list:

| Target | Total Int. | Sessions | First Imaged | Last Imaged | Filters | Top HFR |
|---|---|---|---|---|---|---|
| Andromeda (M31) | 47h 12m | 14 | 2024-08-12 | 2026-05-15 | L,R,G,B,Hα | 1.18 |
| Dragons of Ara (NGC 6188) | 18h 30m | 6 | 2025-03-22 | 2026-04-30 | Hα,OIII,SII | 1.42 |

Tap a target → target detail page with:
- Per-filter integration breakdown (stacked bar)
- Cumulative integration over time (line chart)
- Per-session quality trend (HFR + star count over sessions)
- Plate-solve consistency check: did rotation/center drift across sessions? (important for multi-year stacking per §40.6)
- "Capture more data" CTA → §40.6 Resume Target flow
- "Capture matching flats" CTA → §39.5 flow

### 50.6 Focus & Temperature view

Killer chart: **HFR vs sensor temperature, scatter plot per filter, with linear regression**.

```
Filter L:  HFR
1.8 ┤              ●●
1.5 ┤        ●●● ●●●         ← regression: HFR = 1.2 + 0.05 × (T₀ - T)
1.2 ┤●●●●●●●●●                slope = 0.05 per °C
    └────────────────► sensor temp °C
   -15  -10  -5  0  +5
```

- Each dot = one captured frame
- Linear regression line + R² value
- Configured tempcomp slope from profile shown as reference (different color line)
- Insight callout: *"Measured slope is 0.05; your profile says 0.04. Consider updating to match reality."*

Across all filters: stacked or per-filter subplots. Filter-specific insights expose focus offsets that may need recalibration.

### 50.7 Guiding view

PHD2 RMS data over time:

- **RMS over recent sessions** — line chart per night, separate RA + Dec
- **RMS distribution histogram** — most sessions cluster around 0.5"; outliers reveal bad nights
- **RMS vs altitude** — scatter, reveals "guiding gets worse at low alt"
- **RMS vs wind speed** (if weather connected) — scatter, reveals wind sensitivity
- Per-session detail: RMS over the night with annotations for dithers / meridian flips / focus events

Insight examples:
- *"Your last 5 sessions averaged 0.42" RMS. The 5 before averaged 0.31". Something has changed — check belt tension, balance, cable drag."*
- *"RMS doubles when wind > 12 km/h. You may want to add a wind threshold to your safety policy (§35)."*

### 50.8 Frame Quality view

Composite quality score per frame (computed by §50.10). Three sub-views:

- **Distribution histogram** — quality score across all frames in selected date range, per filter
- **Quality over time of night** — scatter or rolling average; reveals transparency degradation patterns
- **Quality vs HFR + star count + ADU** — multi-dimensional view; lets user see what drove low scores

Pulls from this: the **"Best Frames" auto-sort filter** in the Image Library (§40) — user clicks "Show top 80%" and ARA filters frames by composite score.

### 50.9 Equipment Health view

Trends per equipment type. Cards:

- **Camera cooler power** — power draw to maintain set temp, trended over months. Rising = sensor degradation or thermal grease aging.
- **Mount tracking accuracy** — guide RMS as a proxy; trend over time. Reveals lubricant aging, belt stretch.
- **Filter wheel position errors** — frequency of EFW retries from §42. Rising = mechanical wear.
- **Focuser drift** — position drift between sessions at same temp. Reveals slop / backlash growing.
- **Disconnect frequency** — equipment disconnect events per type, per session. Reveals cable/connector issues.

Each card has a sparkline + a status indicator (green/yellow/red based on configurable thresholds) + a "View Detail" button drilling into the underlying data.

### 50.10 Composite quality score algorithm

For each captured frame:

```
quality_score = w1 * normalize(hfr_inverse)
              + w2 * normalize(star_count)
              + w3 * normalize(roundness)
              + w4 * normalize(median_adu_score)
              - w5 * normalize(eccentricity)
```

Where:
- `hfr_inverse` = 1 / HFR (lower HFR = better)
- `star_count` = stars detected
- `roundness` = average star roundness (1.0 = perfect circles)
- `median_adu_score` = closeness to user-configured ideal ADU (peaks at ideal, drops on either side)
- `eccentricity` = average star eccentricity (lower = better tracking)

Weights `w1..w5` default to `[0.4, 0.2, 0.15, 0.1, 0.15]` — configurable per profile. Normalized to 0-100 scale per filter (since narrowband filters have inherently fewer stars than broadband).

Stored as `frames.quality_score` REAL. Recomputed on capture. Algorithm version tracked so we can recompute if formula evolves.

### 50.11 Session Efficiency view

Time breakdown per session:

```
M42 session 2026-05-18 — 4h 32m total
████████████████████████████░░░░░░░░░░  Light frames (75%, 3h 24m)
███░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░  Autofocus (5%, 14m)
██░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░  Dither + settle (3%, 8m)
██░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░  Plate solve (3%, 8m)
█████░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░  Meridian flip (8%, 22m)
█░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░  Slew + filter changes (2%, 5m)
██░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░  Idle / waiting (4%, 11m)
```

Insight callout examples:
- *"Your meridian flip took 22 minutes; typical is 8-12. Plate-solve retried 3 times — check polar alignment."*
- *"Autofocus consumed 14 minutes across 4 runs. Consider lowering AF cadence (e.g., trigger only on >3°C temp change instead of every 90 min)."*

Per-session efficiency score (% light-frame time) plotted over recent sessions.

### 50.12 Conditions view

Frame quality correlated with environmental factors (when weather + lunar data available):

- **Quality vs ambient temperature**
- **Quality vs humidity**
- **Quality vs lunar illumination + lunar distance from target**
- **Quality vs altitude of target (airmass)**

These are scatter plots with smoothed trend lines. Surfaces insights like:
- *"Your imaging is consistently 15% worse when Moon > 60% illuminated and within 30° of target. Consider scheduling those nights for narrowband only."*
- *"Quality drops 8% per 10°C ambient temp drop — your dew heater may not be keeping up. Increase its power output."*

### 50.13 Achievements + milestones (lightly gamified)

Small ARA-flair feature for engagement. Tracks:

- **Streaks**: consecutive nights with imaging (encourages clear-night follow-through)
- **Records**: longest single session, most frames in one night, most targets in one session
- **Milestones**: first 10h on a target, first 100h total, first imaging across all 12 Messier seasons, etc.
- **Discovery badges**: first plate solve, first mosaic, first narrowband filter used, etc.

Achievements appear in a dedicated panel; recent achievements show as notification celebrations (info severity, never blocking).

Not heavy gamification — no points, no leaderboards. Just light "milestones along your astro journey" surfacing.

### 50.14 Calendar heatmap

GitHub-contributions-style calendar of imaging activity:

```
        Jan  Feb  Mar  Apr  May  Jun  Jul  Aug  Sep  Oct  Nov  Dec
Mon    ░░░  ▒▒░  ▓▓▓  ░▒▒  ▓▒░  ░░░  ░░░  ▓▒▒  ▓▓▓  ▒▒░  ▒▒▒  ▓▓▒
Tue    ░░░  ░▒░  ▒▓▓  ▒▒░  ▓▒▒  ░░░  ░░░  ▒▒▒  ▓▓▒  ▓▒░  ▒▓▓  ▓▓▒
...
```

Color intensity = frames captured (or hours integrated). Hover a day → session summary popup. Reveals patterns: "I always image on Thursdays" / "summer monsoon kills June for me" / "I peaked in October last year."

### 50.15 Exports

User can export analytics for sharing, archival, or external processing:

- **PDF report per target** — "Andromeda Imaging Summary" with all charts, sessions, sample frames. Useful for documenting projects or sharing with imaging groups.
- **CSV export of session data** — `sessions.csv`, `frames.csv`, `quality_scores.csv`. Power users analyze in Python/R/Excel.
- **Astrobin-format JSON** — pre-fills target + integration + filters for Astrobin posting. Saves the user from manually entering the info.
- **Equipment health PDF** — useful for warranty claims or mechanic consultations

### 50.16 API endpoints

| Method | Path | Purpose |
|---|---|---|
| `GET` | `/api/v1/stats/overview` | Tile data for §50.4 (last night, this month, this year, etc.) |
| `GET` | `/api/v1/stats/targets` | Per-target rollups |
| `GET` | `/api/v1/stats/targets/{name}` | Single-target detail (filter breakdown, cumulative integration, quality trend) |
| `GET` | `/api/v1/stats/focus-temp` | HFR-vs-temp data per filter for §50.6 |
| `GET` | `/api/v1/stats/guiding` | Guide RMS data per session + aggregates |
| `GET` | `/api/v1/stats/quality` | Quality score distributions + over-time |
| `GET` | `/api/v1/stats/equipment-health` | Per-equipment health metrics |
| `GET` | `/api/v1/stats/efficiency/{session_id}` | Time-breakdown analysis |
| `GET` | `/api/v1/stats/conditions` | Quality-vs-conditions correlations |
| `GET` | `/api/v1/stats/achievements` | List of unlocked milestones |
| `GET` | `/api/v1/stats/calendar` | Heatmap data (frames/hours per day) |
| `POST` | `/api/v1/stats/exports` | Generate an export (body: type + filters); returns download URL |

All endpoints accept date-range filters (`from`, `to`) for time-bounded analysis.

### 50.17 Charting + rendering

Flutter charting via **`fl_chart`** package — open-source, MIT, mature, covers line/bar/scatter/pie. Handles all chart types in §50.

For the calendar heatmap (§50.14): custom widget via Flutter `CustomPaint` since no off-the-shelf package matches GitHub-contributions-style well.

PDF generation: **`pdf`** package (Flutter, pure Dart). Generates client-side from the analytics data, no server round-trip needed.

### 50.18 Performance + caching

Analytics queries can be expensive on large datasets (years of frames). Two-tier caching:

- **Server-side materialized views** — daily aggregates pre-computed nightly into `stats_daily` table. Most queries hit the materialized view, not raw `frames`. Refreshed by a nightly background job.
- **Client-side response cache** — WILMA caches API responses with TTL (5 min for "Overview" tile data; 1 hour for trend charts). User pull-to-refresh forces a fresh fetch.

Heaviest charts (HFR-vs-temp with all-history) downsample on the server side (max 10k data points per chart) to keep frontend rendering snappy.

### 50.19 v0.0.1 vs v0.1.0 honest scope split

User wants the full dashboard. Here's the honest split given the engineering reality:

**Ship in v0.0.1:**
- §50.2 data foundation (already captured)
- §50.4 Overview tiles
- §50.5 Targets view + per-target detail
- §50.6 Focus & Temperature charts
- §50.7 Guiding charts
- §50.8 Frame Quality histogram + Best Frames auto-sort
- §50.10 Composite quality score (computed on capture)
- §50.14 Calendar heatmap
- §50.15 CSV export
- §50.16 API endpoints (all of them — server-side is straightforward)

**Defer to v0.0.2 / v0.1.0:**
- §50.9 Equipment Health (requires careful threshold-tuning per equipment type)
- §50.11 Session Efficiency (requires instrumenting per-instruction timing in the sequencer — preserved from NINA but needs analytics hookup)
- §50.12 Conditions correlation (requires reliable weather data; many users don't have weather stations)
- §50.13 Achievements (nice but not essential; defining "interesting" milestones takes design iteration)
- §50.15 PDF + Astrobin exports (PDF generation is real work; Astrobin format is small)

That's still a substantial v0.0.1 — leaves NINA's "no analytics" approach in the dust without overscoping.

### 50.20 Privacy

All analytics computed and stored locally on the Pi. **No telemetry leaves the user's network** (per §18.C). Calendar heatmaps, target lists, equipment health — all stay on the user's USB drive. ARA never aggregates user data across users.

If a user wants to share their analytics (e.g., PDF report to an astrophotography group), it's their explicit action via §50.15 exports.

---

## 51. Real-time acquisition diagnostics + smart corrections

§50 mines historical session data for retrospective insight. **§51 does the same analysis at the moment of capture and acts on it.** As each frame completes, ARA Core analyzes the multiple quality signals together, diagnoses WHY conditions changed, and either auto-corrects the rig or surfaces a precise, actionable notification — not "HFR went up" but **"clouds passing — pause until they clear"** or **"focuser drifted, refocusing"** or **"target at 7° altitude in the trees — advance to next target."**

This is the section designed to leave NINA, ASIAir, SharpCap, and competitors visibly behind. NINA detects HFR drift; ARA *diagnoses what caused it* and acts.

### 51.1 The signals (per-frame, already captured)

Each completed frame writes these metrics to the session DB (per §39.3 + §50.10):

| Signal | What it tracks |
|---|---|
| `hfr` | Half-Flux Radius (focus quality) |
| `star_count` | Stars detected above threshold |
| `roundness` | Average star roundness (1.0 = circular) |
| `eccentricity` | Average star eccentricity (0 = round, 1 = streak) |
| `median_adu` | Median background ADU |
| `background_gradient` | Vignette/light-pollution gradient steepness |
| `gain` / `offset` / `exposure` | Capture parameters |
| `set_temp` / `ccd_temp` | Cooler state |
| `ambient_temp` / `humidity` / `dew_point` | Weather (if connected) |
| `altitude` | Target altitude at exposure midpoint |
| `airmass` | Computed from altitude |
| `lunar_illumination` / `lunar_separation_deg` | Moon state |
| `plate_solve_result` | Solve success/failure for the frame |
| `guide_rms_total` / `guide_rms_ra` / `guide_rms_dec` | PHD2 RMS during exposure |
| `guide_star_lost_events` | Times guide star was lost during exposure |
| `composite_quality_score` | Per §50.10 |

Server keeps a **rolling buffer** of the last 10 frames' signals in memory for pattern detection.

### 51.2 The diagnostic decision tree

After each frame, server runs a rule-based diagnostic. The key insight: **multi-signal patterns disambiguate causes** that single signals can't.

| Pattern (current frame + rolling buffer) | Likely cause | Severity | Auto-action |
|---|---|---|---|
| HFR rising over 3 frames, star count stable, background stable, eccentricity stable | **Focuser drift** | warning | **Auto-refocus** on the current filter |
| Star count dropped >40% over 2 frames, HFR rising, background stable | **Clouds passing** | warning | Pause sequence; resume when star count recovers |
| Star count dropped to <5 (essentially zero), no HFR/background info reliable | **Aperture blocked** (tree, dome shutter, dew, cable across scope) | critical | Pause + notify *"Aperture may be blocked — check for trees, dew, or obstructions"* |
| Stars + HFR + roundness all stable, but median_adu rising over 5+ frames | **Light pollution increasing** (dawn approaching, Moon rising, neighbor's lights) | warning | Notify; suggest switching to narrowband if available |
| Eccentricity rising, HFR + star count + background stable | **Wind or tracking issue** | warning | Pause + check guide RMS; if guide RMS also up → pause until conditions improve |
| Guide RMS spike + guide_star_lost_events > 0 | **Clouds blocking guide star** | warning | Pause; resume when star reacquired |
| Plate solve failed N consecutive times mid-sequence | **Off-target or heavy clouds** | warning | Re-slew + blind plate solve; if still fails → notify |
| Target altitude < profile soft threshold (default 30°), trending down | **Target setting** | info | Notify "M42 at 22° and dropping — consider advancing to next target" |
| Target altitude < profile hard threshold (default 5°), trending down | **Target below horizon** | warning | Auto-advance to next target in sequence (per §28 logic) |
| HFR rising + cycling-degradation pattern (good after AF, degrades, AF retriggers, repeats) | **Transient atmospheric** (already covered §40.7) | warning | Notify "Autofocus isn't catching this — likely clouds or seeing" |
| CCD temp drifting from set_temp by >3°C | **Cooler struggling** (warm ambient, cooler failure) | warning | Notify; if persistent, suggest pausing |
| Humidity near 100% + ambient at dew point + HFR rising gradually | **Dew formation** (already covered §42) | warning | Notify "Dew suspected — check optics and heaters" |
| Frame quality score dropping monotonically over 5+ frames, no clear single-signal cause | **General degradation** (unknown) | info | Notify with current signal readout; let user investigate |

Decision-tree implementation: ~200 lines of Dart-port-of-C# in `OpenAstroAra.Sequencer/Diagnostics/AcquisitionDiagnostics.cs`. Pure-function: takes current frame metrics + rolling buffer, returns a `Diagnosis { cause, severity, suggested_action }`.

### 51.3 Auto-correction actions

When diagnosis returns an action, server may execute it autonomously, depending on user policy (§51.5):

| Action | What it does |
|---|---|
| `auto_refocus` | Triggers autofocus on the current filter; resumes capture after |
| `pause_until_recovery` | Pauses sequence; resumes when the inverse-signal recovers (star count > threshold again, etc.) |
| `skip_to_next_target` | Marks current target as "skipped due to altitude/conditions" in session log; advances to next |
| `re_slew_and_plate_solve` | Forces a fresh slew + plate solve to recover from off-target |
| `notify_only` | No autonomous action; just informs user with diagnosis |
| `safety_abort` | Hands off to §35 safety policy (only triggered by truly bad conditions, e.g., sustained dew + tracking loss + altitude below hard floor) |

### 51.4 Real-time UI in WILMA

Two surfaces:

**Health Indicator in main app shell (always visible during sequence):**

```
┌─────────────────────────────┐
│  🟢 All systems nominal     │
│  HFR 1.42 | Stars 487       │
│  Guide 0.42" | Air 1.1      │
└─────────────────────────────┘
```

Three states:
- 🟢 **Green** — all signals within nominal ranges
- 🟡 **Yellow** — one or more signals degraded, advisory only (or auto-action being taken, e.g., refocus running)
- 🔴 **Red** — significant degradation; sequence paused or action recommended

Tap → opens the Diagnostic Panel.

**Diagnostic Panel** (slide-over or modal):

```
┌──────────────────────────────────────────────────────┐
│  Acquisition Diagnostics                              │
│  ──────────────────────────────────────────────────  │
│  🟡 Clouds passing — pause until clear                │
│                                                       │
│  Last 5 frames star count: 487 → 432 → 290 → 156 → 47│
│  HFR rising: 1.42 → 1.55 → 1.78 → 2.41 → 3.20         │
│  Background ADU stable: ~1100                         │
│  → Pattern matches "transient clouds"                 │
│                                                       │
│  ARA will: pause sequence; resume when star count     │
│            recovers to >300 (currently 47)            │
│                                                       │
│  Recent live preview:                                 │
│    [thumbnail of latest frame — visibly cloudy]       │
│                                                       │
│  [Override — keep imaging]  [Stop sequence]           │
└──────────────────────────────────────────────────────┘
```

The diagnostic explains the WHY in plain language and shows the underlying numbers. Builds user trust ("how does ARA know it's clouds?") and teaches users to read the same signals themselves over time.

### 51.5 User policy — aggression dial

Profile gains a `realtime_diagnostics` block:

```json
{
  "realtime_diagnostics": {
    "mode": "notify_only",     // "aggressive" | "balanced" | "conservative" | "notify_only"
    "auto_refocus_threshold": 1.5,   // HFR multiplier triggering refocus
    "auto_skip_target_below_alt_deg": null,  // null = use profile hard threshold (§28)
    "pause_on_cloud_detection": true,
    "pause_threshold_star_count_drop_pct": 40,
    "max_consecutive_solve_failures_before_reslew": 3
  }
}
```

Modes:

- **aggressive** — ARA acts on warnings (auto-refocus, auto-pause, auto-skip) without asking; maximizes uptime
- **balanced** — acts on critical signals (pause for clouds, refocus for focus drift); notifies on warnings; user decides
- **conservative** — notifies only; doesn't auto-correct; user takes action manually
- **notify_only** *(v0.0.1 default)* — alerts but never acts; for users who want full manual control. The Diagnostic Panel + Health Indicator + per-frame FITS metadata enrichment all still run; ARA simply doesn't take autonomous corrective action

**Why notify_only is the v0.0.1 default (instead of balanced):**

1. **Thresholds are uncalibrated.** The 40% star-drop threshold, HFR 1.5× refocus trigger, etc. are educated defaults — not per-user-tuned values. v0.1.0 ships per-user threshold calibration (§51.9); until then, the risk of false-positive auto-actions outweighs the benefit. A spurious auto-pause during a clean Bortle 1 session would teach users to distrust + disable the feature entirely.
2. **Matches competitor posture.** NINA has no smart corrections at all. ZWO ASIAir auto-pauses only on hardware safety signals (cloud sensor, rain). `notify_only` aligns ARA's out-of-box behavior with what users already expect from the rest of the ecosystem.
3. **First-do-no-harm.** The diagnostic value (you see WHY a frame went bad) is preserved without surprise behavior. Power users who want auto-correction opt into `balanced` / `aggressive` in Settings → Diagnostics; that's a deliberate "I trust the smart features" choice rather than a default surprise.

When `balanced` graduates to v0.0.2 / v0.1.0 default depends on telemetry from real users (per-user calibration must work reliably first). Until then, opt-in.

Settings → Diagnostics has the mode picker; no wizard screen added in v0.0.1 (the default of `notify_only` is safe; users discover the picker via §61 search or while exploring Settings). Mode picker is registered in §51.12.

### 51.6 Server-side monitor loop

After each frame is finalized (post-FITS-write, post-preview-generation, post-quality-score-computation):

```
1. Append frame metrics to rolling buffer (drop oldest if > 10 frames)
2. Run diagnostic decision tree → returns Diagnosis object
3. If Diagnosis.severity != none:
     a. Log to faults table + notifications table
     b. If diagnostics.mode permits auto-action AND action is autonomous:
          - Execute the action (refocus / pause / skip)
          - Emit `diagnostic.action_taken` WebSocket event
     c. If action is "notify_only":
          - Emit `diagnostic.advisory` WebSocket event
4. Continue with next exposure
```

Total overhead per frame: < 50ms on Pi 5. Negligible vs the 30+ seconds of exposure.

### 51.7 API endpoints

| Method | Path | Purpose |
|---|---|---|
| `GET` | `/api/v1/diagnostics/current` | Current health status + most-recent diagnosis (for the Health Indicator) |
| `GET` | `/api/v1/diagnostics/buffer` | Rolling buffer of last N frames' metrics (for the Diagnostic Panel) |
| `GET` | `/api/v1/diagnostics/history?session_id=...` | All diagnostics fired in a session |
| `POST` | `/api/v1/diagnostics/override` | User overrides an auto-action ("no, keep imaging") |
| `PUT` | `/api/v1/diagnostics/policy` | Update user's diagnostic policy (mode + thresholds) |

WebSocket event types:

```json
{ "type": "diagnostic.advisory", "payload": { "cause": "clouds_passing", "severity": "warning", "signals": {...}, "suggested_action": "pause_until_recovery", "auto_action_taken": false } }
{ "type": "diagnostic.action_taken", "payload": { "cause": "focuser_drift", "action": "auto_refocus", "details": "Triggered AF on filter L" } }
{ "type": "diagnostic.recovered", "payload": { "cause": "clouds_passing", "duration_seconds": 423 } }
```

### 51.8 Per-frame metadata enrichment

Each frame's FITS header (per §39.3) is enriched with the diagnostic context at capture time:

| New FITS keyword | Value |
|---|---|
| `ARA-DIAG` | Active diagnosis name if non-nominal (e.g., `"clouds_passing"`); empty if green |
| `ARA-QSCORE` | Composite quality score (from §50.10) |

Post-processing tools and ARA's own §50 Frame Quality view can filter by diagnostic state — e.g., "show me all frames captured during cloudy windows" or "exclude all `clouds_passing` frames from stacking."

### 51.9 Learning over time (v0.1.0)

Rule-based diagnosis is v0.0.1. v0.1.0 adds:

- **Per-user calibration** — threshold-tuning from observed normal ranges. After a few sessions, ARA learns what "normal" star count looks like for the user's gear, sky, location; adjusts diagnostic thresholds rather than relying on global defaults.
- **ML pattern detection** — small on-device model trained on user's labeled diagnostic events ("yes, that was clouds" / "no, that was just dew"); improves diagnosis accuracy
- **Predictive alerts** — e.g., "based on the last 3 nights, you usually hit dew formation around 03:30 — your dew heaters typically don't keep up" → suggest enabling heaters proactively

All optional; user explicitly opts in to the learning system (no silent ML on private data).

### 51.10 Comparison to competitors

| Capability | ASIAir | NINA | SharpCap | ARA |
|---|---|---|---|---|
| HFR threshold alert | Yes | Yes | Yes | Yes |
| Multi-signal pattern diagnosis | No | No | Limited | **Yes** (§51.2) |
| Cause-naming notifications ("clouds passing" vs just "HFR up") | No | No | No | **Yes** |
| Auto-pause on cloud detection | No | No | No | **Yes** |
| Auto-correction (refocus, skip target, re-slew) | Limited | Manual | No | **Yes** |
| Configurable aggression level | No | Threshold settings only | Limited | **Yes** (§51.5) |
| Per-frame diagnostic FITS metadata | No | No | No | **Yes** (§51.8) |
| Learning over time (v0.1.0) | No | No | No | **Planned** |

This is the section that's worth showing in marketing screenshots: a side-by-side of "ARA detected: clouds passing, pausing until recovery (4 frames in queue)" vs ASIAir's "HFR is high."

### 51.11 v0.0.1 vs v0.1.0 honest scope split

**v0.0.1 ships:**
- Diagnostic decision tree (§51.2) with all 12 patterns — runs on every frame regardless of mode
- Auto-actions defined: `auto_refocus`, `pause_until_recovery`, `skip_to_next_target`, `re_slew_and_plate_solve`, `notify_only`, `safety_abort`
- Real-time Health Indicator + Diagnostic Panel UI (§51.4) — always visible during a session
- User policy / aggression dial (§51.5) with 4 modes
- **Default mode: `notify_only`** — diagnostics run + surface in UI + enrich FITS metadata, but ARA takes no autonomous action without explicit user opt-in
- Per-frame FITS metadata enrichment (§51.8)
- API endpoints + WebSocket events (§51.7)

**v0.1.0:**
- Per-user threshold calibration (§51.9 first bullet) — once thresholds calibrate per user, `balanced` can become a safe default
- Promotion path: `balanced` becomes the default mode in v0.0.2 / v0.1.0 once telemetry confirms calibrated thresholds rarely false-positive
- ML pattern detection (§51.9 second bullet)
- Predictive alerts (§51.9 third bullet)
- More sophisticated patterns (we'll learn what works from real user feedback)

### 51.12 §61 search registry entries

- `diagnostics.mode` — keywords: `diagnostics mode, smart corrections, auto refocus, auto pause, auto skip, aggression dial, notify only, balanced, aggressive, conservative`
- `diagnostics.auto_refocus_threshold` — keywords: `hfr threshold, refocus trigger, focus drift sensitivity`
- `diagnostics.pause_on_cloud_detection` — keywords: `cloud detection, auto pause clouds, star count drop`
- `diagnostics.pause_threshold_star_count_drop_pct` — keywords: `star count drop, cloud sensitivity, pause threshold`
- `diagnostics.max_consecutive_solve_failures` — keywords: `plate solve failures, re-slew threshold, off-target detection`

---

## 52. Mount handling — Alpaca-only commitment + feature detection

### 52.1 Alpaca-only is a permanent architectural commitment

ARA speaks ASCOM Alpaca exclusively. **INDI and INDIGO are not, and will not become, native protocols.** This is not "deferred to v0.1.0" — it's a permanent design choice. Reasons:

| Standard | Conformance validation | Driver quality bar |
|---|---|---|
| **ASCOM Alpaca** | [ConformU](https://github.com/ASCOMInitiative/ConformU) — formal test suite operated by the ASCOM Initiative; required for certified drivers | Consistently high; non-conformance is detectable + reportable |
| INDI | None (community-curated) | Variable; quirks must be discovered + worked around per-driver |
| INDIGO | Limited (some validation but no equivalent ConformU-style certification) | Better than INDI but still inconsistent |

Supporting INDI/INDIGO natively means accumulating brand-quirk workarounds forever — exactly the maintenance burden ARA exists to avoid. NINA learned this the hard way; we don't repeat it.

### 52.2 Bridge path for INDI / INDIGO users

Users with INDI or INDIGO equipment connect via a bridge that exposes the equipment as Alpaca:

- **INDIGO native Alpaca server** — INDIGO ships with an `-A` flag that exposes all connected INDIGO drivers as Alpaca devices on the local network. Zero-config for users already running INDIGO. Recommended path.
- **AlpacaPi** ([github.com/msproul/AlpacaPi](https://github.com/msproul/AlpacaPi)) — INDI → Alpaca bridge. Runs alongside INDI on the same Pi.
- **AlpacaBridge** ([github.com/AlpacaBridge](https://github.com/AlpacaBridge)) — bridges ASCOM COM (Windows) + USB drivers → Alpaca. Already the canonical equipment hub for ARA per §2.

ARA documentation (DEPLOY.md + README) makes the bridge path explicit. Users coming from INDI ecosystems are pointed at these tools, not asked to wait for native ARA support that isn't coming.

### 52.3 Feature detection — no brand-specific code in ARA

ARA does **not** maintain a "known mount quirks database." Mount-specific logic is anti-pattern that defeats the abstraction layer. Instead, ARA queries Alpaca's standard capability flags + properties:

| What ARA needs to know | Standard Alpaca property |
|---|---|
| Can the mount Find Home? | `CanFindHome` |
| Can the mount park? | `CanPark` / `CanSetPark` |
| Slew speed options? | `AxisRates(TelescopeAxis)` |
| Settle time? | `SlewSettleTime` |
| Coordinate system? | `EquatorialSystem` |
| Alignment mode? | `AlignmentMode` |
| Tracking rates supported? | `TrackingRates` |
| Side of pier sensing? | `CanSetPierSide` / `SideOfPier` |
| Pulse guiding? | `CanPulseGuide` / `MaxPulseGuideRate` |
| Async slew? | `CanSlewAsync` / `CanSlewAltAzAsync` |
| Custom vendor commands? | `SupportedActions` (array of strings) |

ARA's UI adapts at runtime: if `CanFindHome = false`, the "Find Home" button is hidden. If `CanPulseGuide = false`, PHD2 is configured for ST4 guiding instead. Etc.

This is the standard Alpaca workflow. ARA doesn't second-guess it.

### 52.4 Sensible defaults, not brand-specific overrides

The wizard (§37) sets sensible defaults that work for any conformant Alpaca driver. Examples:

- Default slew settle time: 5 seconds (overridden by `SlewSettleTime` if the driver provides it)
- Default plate-solve tolerance after slew: 60 arcsec (per §28.2)
- Default meridian flip behavior: auto if `CanSetTracking = true` + `CanSlewAsync = true`, otherwise prompt
- Default tracking rate: sidereal

These are generic. Users override per-profile if needed. **Nothing in ARA's code says "if mount name contains 'iOptron CEM' then…"** — that path leads to brand-quirk cruft.

### 52.5 Optional first-connect conformance check

When ARA connects to an Alpaca mount for the first time, it can optionally run a lightweight conformance check (subset of ConformU's tests) and surface results:

- **Pass**: silent; mount is added normally
- **Warning**: e.g., *"Driver reports tracking but `CanSetTracking = false` — vendor bug? Behavior may be undefined."* — mount added but flagged
- **Fail (critical)**: e.g., driver returns malformed JSON or wrong types — surface error to user with link to driver project's issue tracker

This is **optional, off by default** in v0.0.1 (requires implementation work). User toggle in Settings → Equipment → "Run conformance check on connection." v0.1.0 may turn it on by default as ARA's compliance testing matures.

Reporting workflow when a driver fails: ARA shows a "Report this issue" button that opens the driver's GitHub issues with a pre-filled bug report including the failing tests + Alpaca conformance test number. Encourages users to push driver quality upstream rather than have ARA work around bugs.

### 52.6 What about NINA's mount-specific code?

NINA contains a fair amount of brand-specific mount handling — special-case branches for EQMod, iOptron CEM park modes, SiTech axis limits, OnStep extensions, etc. **During the Phase 8 port, ARA strips this brand-specific code.** Generic Alpaca handling replaces it.

If a brand-specific behavior turns out to genuinely require special handling, the path is:
1. Report the discrepancy to the driver author
2. Wait for the driver to be updated to handle the case via standard Alpaca calls
3. NOT add special-case code to ARA

This is firm. The maintenance burden of one mount-quirk-database is genuinely worse than the inconvenience of waiting for a driver update.

### 52.7 What v0.1.0 may add (without changing the core philosophy)

- **Community-curated tips file** — shared markdown file (e.g., `MOUNT_TIPS.md` in the open-astro/openastro-ara-community repo) where users contribute *user-knowledge* tips for specific mounts ("On Mach3, I found setting X helps for my setup"). This is documentation, not hardcoded behavior — ARA doesn't read it programmatically.
- **First-connect ConformU integration** — optional auto-run of the official ASCOM ConformU tool against connected mount, with results saved to the session log. Tightens feedback loop for surfacing driver bugs.
- **Driver version awareness** — show user a notification if a connected driver has a known bug fixed in a newer version (community-curated registry of "driver X v1.2.3 has issue Y, fixed in v1.2.4").

None of these change the core: **ARA stays Alpaca-only, trusts the standard, and pushes driver bugs upstream rather than working around them.**

### 52.8 Cross-section updates this commitment implies

- **§2.1** Equipment row — "Alpaca only" language is now permanent (not v0.0.1-only)
- **§6 / §20.3** Equipment provider abstraction stays at one implementation (`AlpacaEquipmentProvider`); the `IEquipmentProvider` interface need never be re-implemented
- **§37.2** Wizard's "Protocol choice" screen — drop INDI/INDIGO entirely (no "future support" placeholder). Just shows "ASCOM Alpaca" with a tooltip explaining the bridge path
- **§24 done criteria** — confirms Alpaca-only-forever as the architectural baseline
- **DEPLOY.md / README** — explicit "If you have INDI/INDIGO equipment, run a bridge" guidance; link to AlpacaBridge, AlpacaPi, INDIGO Sky options

---

## 53. Accessibility (WCAG 2.1 AA-leaning baseline)

ARA commits to a **targeted accessibility baseline** that benefits many user groups beyond formally-impaired users — color-blind users (~8% of males), aging astrophotographers (median demographic is 50+), low-vision users, anyone using the app in bright dawn glare, anyone preserving night vision with a red headlamp, anyone keyboard-navigating on desktop, and observatories/outreach orgs that need to comply with ADA / Section 508 / EU EAA.

**Scope: WCAG 2.1 AA-leaning, not formally certified.** We follow the standard where it's cheap and broadly beneficial; we don't pursue paid audits or marketing the compliance.

### 53.1 What's in scope

| Requirement | Implementation |
|---|---|
| Color contrast 4.5:1 minimum for text | Verified across all `AraColors` tokens (§25.2) during Phase 12; failing combinations adjusted |
| Color + symbol on status indicators | Per §53.2 below |
| Font scaling honored | Flutter's `MediaQuery.textScaleFactor` respected automatically by `Text` widgets; ARA does not override |
| High-contrast theme variant | Per §53.3 below — opt-in via Settings → Display |
| Reduce-motion OS setting honored | Wrap animations in a check on `MediaQuery.disableAnimations`; transitions become instant when user has reduced motion enabled |
| Keyboard navigation on desktop | Flutter Material widgets handle this by default; ARA verifies tab order on every screen |
| Visible focus indicators | Flutter Material default (focus ring on focused element); ARA does not strip them |
| Touch targets ≥ 44pt | Flutter Material default for most controls; verified on custom controls |
| Semantic widget annotations on custom controls | Custom CustomPaint widgets (e.g., polar-align bullseye, frame viewer, sky atlas overlays) wrapped in `Semantics(label: ...)` to give screen readers a description |
| Screen reader smoke test | Manual test of major flows on VoiceOver (macOS/iOS) + TalkBack (Android) before v0.0.1 release. Verify nav rail, equipment chips, sequence editor, settings are announceable. |

### 53.2 Color-blind friendly status indicators

Every status indicator uses **shape/symbol AND color**, never color alone:

| State | Color | Symbol | Used in |
|---|---|---|---|
| Nominal / healthy / connected | 🟢 Green (`#4CAF50`) | ✓ check | Equipment chips, health indicator, frame quality |
| Busy / in-progress / advisory | 🟡 Yellow (`#FFB300`) | ⚠ warning triangle | Active autofocus, recovery, soft warnings |
| Error / critical / disconnected | 🔴 Red (`#E53935`) | ⛔ no-entry circle | Faults, urgent safety, disconnected equipment |
| Disconnected / disabled / unknown | ⚪ Gray (`#606060`) | ○ empty circle | Equipment not connected, feature disabled |
| Info | 🔵 Blue (`#42A5F5`) | ⓘ info circle | Notifications, hints |

Defined as a reusable `StatusIndicator` Flutter widget that takes a state enum and renders both. Used consistently throughout the app (Health Indicator, equipment chips, notifications, frame quality icons, etc.).

### 53.3 High-contrast theme variant

In addition to the default dark theme (`AraColors` tokens from §25.2), ARA ships a high-contrast dark variant:

| Token | Default dark | High-contrast |
|---|---|---|
| Background primary | `#1A1A1A` | `#000000` |
| Panel background | `#262626` | `#0A0A0A` |
| Panel alternate | `#2E2E2E` | `#1A1A1A` |
| Text primary | `#E0E0E0` | `#FFFFFF` |
| Text secondary | `#A0A0A0` | `#D0D0D0` |
| Border | `#404040` | `#888888` (thicker borders too) |
| Accent (connected) | `#4CAF50` | `#00FF00` |
| Accent (busy) | `#FFB300` | `#FFCC00` |
| Accent (error) | `#E53935` | `#FF3333` |

Toggle: Settings → Display → "High contrast mode" (on/off). Persisted per-WILMA-device.

Also includes:
- Slightly larger default font (110% of base) when high contrast is enabled
- Thicker focus indicator borders
- Disable subtle hover/elevation effects that low-contrast users can't perceive

No light-theme variant in v0.0.1 — observatory astrophotography is universally dark-themed (preserves night vision). Light theme is a v0.1.0+ consideration if users ask.

### 53.4 Implementation notes

**Flutter accessibility primitives ARA leans on:**
- `Semantics(label: ..., hint: ..., onTap: ...)` for custom controls' screen-reader descriptions
- `MergeSemantics` to combine related controls into logical units (e.g., the equipment chip's icon + label + status)
- `ExcludeSemantics` for purely decorative elements (e.g., logo placeholder)
- `MediaQuery.of(context).textScaleFactor` — auto-applied by `Text` widgets; ARA doesn't override
- `MediaQuery.of(context).disableAnimations` — wrap animation builders in a check; default to instant when true
- `Focus` + `FocusableActionDetector` for keyboard-navigable custom widgets

**Per-screen review during Phase 12:** every screen the AI builds is verified for:
- Tab order is logical (top-left → bottom-right reading order, mostly)
- All actionable elements reachable by keyboard
- Visible focus indicator on every focusable element
- Custom CustomPaint widgets wrapped in Semantics

### 53.5 What's explicitly NOT in v0.0.1

- Formal WCAG 2.1 AA certification or compliance audit
- Paid third-party accessibility testing
- Compliance attestation documentation
- AAA-level requirements (e.g., 7:1 contrast, sign-language alternatives, etc.)
- "WCAG Compliant" marketing claims
- Light theme variant (observatory software is dark-themed)
- Voice control or Switch Control specific testing beyond what Flutter handles automatically

These are deferred to v0.1.0+ if user demand or legal requirements emerge (e.g., observatory deploying ARA for public outreach may need formal compliance).

### 53.6 Acknowledgment

The honest case for this baseline: it's cheap, it's the right thing to do, and the people who benefit are mostly *not* fully-blind astrophotographers — they're color-blind, aging, low-vision, glare-affected, and night-vision-preserving users who form a meaningful fraction of every demographic that uses astrophotography software. ARA does the baseline because it's craftsmanship, not because we're pursuing certifications.

---

## 54. Bug report submission + PII handling

§18.C established the "Submit Bug Report" feature: zip logs + open GitHub issue. This section specs how that handles potentially-sensitive information.

### 54.1 Design principle: logs keep full info on disk, sharing is review-first

ARA's local logs include data that's **useful for debugging** but potentially **sensitive when shared publicly**: GPS coordinates, equipment serial numbers, hostnames, paths with usernames. This data stays in logs locally — it helps the user diagnose their own issues — and is also genuinely useful for maintainer debugging (e.g., "the user's GPS shows the target was below horizon at that timestamp, which explains why the slew failed").

Removing this data from logs entirely would hurt debugging. The solution is **review-first submission**: the user sees what's about to be shared and chooses how much detail to include.

### 54.2 Submission flow

```
User taps [Submit Bug Report] in About panel
   ↓
WILMA generates a bug-report zip:
  - Last 5 log files
  - System info (OS, app version, .NET version, hardware)
  - server-info.json: equipment connected, capabilities, current state
  - notifications-recent.json: last 50 notifications
   ↓
WILMA scans the zip contents for sensitive patterns (per §54.4)
   ↓
WILMA shows the Review modal (per §54.3)
   ↓
User picks redaction mode + confirms
   ↓
WILMA applies the chosen redaction; produces final zip
   ↓
WILMA saves the final zip to user's Desktop OR offers it via system share sheet
   ↓
WILMA opens https://github.com/open-astro/openastro-ara/issues/new?template=bug_report.md
   in the system browser with a pre-filled template and a reminder to attach the zip
```

User attaches the zip manually in GitHub — ARA cannot upload directly via API (would require auth + would feel sneaky for a public issue tracker).

### 54.3 Review modal

```
┌──────────────────────────────────────────────────────┐
│  Review Before Submitting                             │
│  ──────────────────────────────────────────────────  │
│  Your bug report will include:                        │
│                                                       │
│  📁 5 log files (24 MB)                               │
│  📋 System info (OS, app version, hardware)           │
│  📋 Equipment connection state                        │
│  📋 Recent notifications (50)                         │
│                                                       │
│  ⚠ Potentially sensitive information found:           │
│    • GPS coordinates: 30.27°N, 97.74°W (your site)    │
│    • Equipment serial numbers: 3 devices              │
│    • Hostname: pi-observatory.local                   │
│    • Username in paths: /home/joey/...                │
│                                                       │
│  GitHub issues are PUBLIC. Anyone can see this info.  │
│                                                       │
│  Sharing mode:                                        │
│   ● Include everything (best for debugging) — default │
│   ○ Coarse GPS only (round to ~111 km, city-level)    │
│   ○ Redact all sensitive items                        │
│   ○ Let me edit the zip first                         │
│                                                       │
│  [Cancel]              [Continue with selection]      │
└──────────────────────────────────────────────────────┘
```

### 54.4 Sensitivity detection

The scan looks for these patterns:

| Pattern | Detection | Sensitivity |
|---|---|---|
| GPS coordinates near user's known site (from profile) | Floating-point lat/long in logs within 1° of profile coords | Medium-high |
| Equipment serial numbers | Vendor-known patterns (e.g., ZWO ASI cameras have `ASIyy-cccc-ddee` format); cross-reference active profile's equipment | Medium |
| Hostnames | Strings ending in `.local`, `.lan`, or matching profile hostname | Low |
| User home paths | `/home/<name>/`, `/Users/<name>/`, `C:\Users\<name>\` | Low |
| Internal IPs | RFC 1918 ranges (`10.x`, `192.168.x`, `172.16-31.x`) | Low |

The scan is best-effort regex — surfaces detections to the user but doesn't claim completeness. The modal copy explicitly says *"potentially sensitive information found"* — implies "this is what we noticed" not "guaranteed exhaustive."

### 54.5 Sharing modes

**1. Include everything (default)** — highest debug fidelity. Zip contains original log content. Maintainer can replay exactly what the user saw.

**2. Coarse GPS only** — round latitude/longitude to nearest 1° (~111 km) in all log lines. Removes the "I just told the internet where my expensive observatory is" risk. Still useful for "was target above horizon" math at city/region scale (e.g., 30°N is enough to know Andromeda is high-sky from Texas in October). Equipment serial numbers, hostnames, paths preserved.

**3. Redact all sensitive items** — replaces every detected item with placeholders:
- GPS → `[REDACTED-GPS]`
- Serial numbers → `[REDACTED-SN]`
- Hostnames → `[REDACTED-HOST]`
- Usernames → `[REDACTED-USER]`
- IPs → `[REDACTED-IP]`

Maintainer sees structure of the issue (which device, which API call, etc.) but not identifying details. Less useful for debugging; safest for users who don't want any PII shared.

**4. Let me edit the zip first** — WILMA saves the zip to Desktop, opens the containing folder in the file manager, shows: *"Edit the zip's contents however you like, then attach it on GitHub. Drag the zip into the GitHub issue page once you're ready."* Power-user escape hatch.

### 54.6 Always-blacklisted patterns (regardless of mode)

These are stripped from every submission, every mode:

- **Token-like strings**: anything matching `X-OpenAstroAra-Token: [a-zA-Z0-9_-]{20,}` or `Bearer [a-zA-Z0-9_-]{20,}`
- **API keys / secrets**: patterns like `[A-Za-z0-9_-]{32,}=` (base64-like), `sk_[a-zA-Z0-9]+`, `ghp_[a-zA-Z0-9]+` (GitHub PATs that someone might accidentally have in their environment), `xoxb-` (Slack), etc.
- **SSH keys**: `-----BEGIN ... PRIVATE KEY-----` blocks
- **File contents from outside ARA data dirs** — bug zips never include `/etc/passwd`, `/etc/shadow`, `~/.ssh/`, browser-cookies, etc. ARA only zips files under `/var/log/openastroara/`, `/etc/openastroara/`, and `/media/openastroara/.araback/`. Hard whitelist.

This is the one thing that's NOT user-toggleable. Sneaking through credentials should be impossible regardless of user choice.

### 54.7 Bug report template

GitHub issue auto-opened with this template pre-filled:

```markdown
## Description
<!-- What were you doing when this happened? -->

## Expected Behavior
<!-- What did you expect to happen? -->

## Actual Behavior
<!-- What happened instead? -->

## Steps to Reproduce
1. 
2. 
3. 

## Attachments
<!-- Drag the bug-report zip from your Desktop here. -->

## System Info
ARA Server version: 0.0.1-ara.X
WILMA client version: 0.0.1+X
OS: <auto-filled>
.NET / Flutter version: <auto-filled>
Sharing mode used: include-everything | coarse-gps | redacted | manual-edit
```

The "Sharing mode used" line tells the maintainer how much redaction was applied — so they don't waste time asking the user "what was your GPS?" if they chose "redacted."

### 54.8 What about private bug reports?

For users who absolutely don't want any info on a public GitHub issue, v0.0.1 has no built-in private channel. Recommended path documented in About → Help: email the maintainer directly with the zip. v0.1.0+ may add a private-submission backend (with TLS, auth, rate-limiting) if user demand justifies the infrastructure.

### 54.9 Privacy-by-default summary

- Logs locally contain full info (useful for the user themselves)
- Logs are NEVER transmitted automatically
- Submission requires explicit user action + explicit review of contents
- Default mode (include-everything) is debug-friendly, but the user sees what they're sharing
- Power users can always hand-edit the zip
- Credentials/secrets are blacklisted regardless of user choice — these never leak

This matches the §18.C "no network telemetry" commitment: anything leaving the user's network is a deliberate user action.

---

## 55. v0.1.0+ Roadmap (consolidated)

Items deferred to v0.1.0+ are scattered across the playbook as one-liner notes. This section aggregates them so the AI (and the user) can see the post-v0.0.1 trajectory in one place.

### 55.1 v0.1.0 — Committed features

These were explicitly marked as v0.1.0 commitments during planning (not "maybe"):

| Feature | Source | Notes |
|---|---|---|
| **Live stacking** | GAPS-ARA Tier 3 | User explicit: "will do it for sure just later." Real-time integration preview during imaging; star registration + sigma-clipped running stack. EAA + "is this target worth tonight" feedback. ASIAir/SharpCap parity. |
| **Plugin SDK + equipment scripting hooks** | §10, GAPS-ARA Tier 3 | Bundled together; same v0.1.0 design pass. Pre-sequence / post-frame hook scripts; custom equipment control; community plugin ecosystem. Fresh Avalonia-native SDK schema. |
| **AlpacaBridge + openastro-phd2 WILMA-push updates** | §33.6 | Same atomic-swap + rollback pattern as ARA Core's WILMA push, extended to siblings. WILMA app size grows ~50-100 MB combined. |
| **Bulk asteroid catalog** | §36.8 | Currently targeted-lookup-only ("Ceres", "433 Eros"). v0.1.0 adds smart-culled MPC asteroid layer (~1.4M numbered asteroids) with visibility/magnitude filtering. |
| **Survey downloader polish** | §36 | Parallel downloads with resume across app restarts; background download on mobile; incremental updates via `If-Modified-Since`. |
| **Dedicated polar-alignment camera support** | §45.14 | Native handling for iPolar / PoleMaster / other Alpaca-tagged "PolarAlignCamera" devices. Same UI + math, just smaller frames. |
| **Per-user diagnostic threshold calibration** | §51.9 | ARA learns user's normal HFR / star-count / etc. baselines from observed sessions; adjusts diagnostic thresholds rather than relying on global defaults. |
| **ML pattern detection for diagnostics** | §51.9 | Small on-device model trained on user-labeled diagnostic events; improves cause-diagnosis accuracy over rule-based approach. Opt-in. |
| **Predictive alerts** | §51.9 | "Based on the last 3 nights you usually hit dew formation around 03:30 — your heaters aren't keeping up." Proactive vs reactive. |
| **Multi-target stream backup** | §44.11 | Mirror frames to two desktop WILMAs simultaneously. |
| **Cloud streaming backup** | §44.11 | rclone-based push to S3 / Google Drive / etc. for off-site backup. |
| **Stats: Equipment Health view** | §50.19 | Cooler power trend, fault-rate analytics, mechanical-drift detection. Threshold tuning per equipment type. |
| **Stats: Session Efficiency view** | §50.19 | Time-breakdown analysis (light vs autofocus vs slewing vs faults). Requires sequencer instrumentation. |
| **Stats: Conditions correlation view** | §50.19 | Quality vs weather + lunar correlations. Requires reliable weather data. |
| **Stats: Achievements / milestones** | §50.19 | Light gamification (streaks, records, discovery badges). |
| **Stats exports: PDF + Astrobin format** | §50.19 | Per-target PDF reports; Astrobin-ready JSON for direct posting. |
| **Notification channels: push, email, Discord/Slack webhooks** | §46.9 | Outbound integrations beyond in-app feed. Requires FCM/APNs setup (push) or SMTP config (email). |
| **Notification scripting** | §46.9 | User-defined "when X happens, do Y" rules (IFTTT-style). |
| **TLS / remote-internet access** | GAPS-ARA Tier 3 late | TLS termination + remote-access mode with warnings. v0.0.1 documented workaround is VPN. |
| **Multi-device WILMA settings sync** | GAPS-ARA Tier 3 late | Server-side storage of WILMA UI preferences; sync across user's Mac + iPad + phone on connect. |
| **Read-only multi-client / spectator mode** | §27.4 | Beyond single-client; add "spectator" connections (e.g., remote-observatory viewer). |
| **First-connect conformance check (default on)** | §52.5 | Currently optional + off; v0.1.0 turns on by default once compliance testing matures. |
| **Driver-version-awareness registry** | §52.7 | Community-curated registry of "driver X v1.2.3 has bug Y, fixed in v1.2.4." |
| **Community-curated MOUNT_TIPS.md** | §52.7 | User-contributed mount-specific tips, as documentation (not hardcoded behavior). |
| **Comet motion tracking during exposure** | GAPS-ARA Tier 3 late | Update RA/Dec per exposure from orbital elements for moving targets. |
| **Astrometry.net solver support** | §18.I | If user demand emerges, with Survey-Manager-style UI for 4100/4200/5000-series index downloads. |
| **OpenAPI-generated SDKs** | §49.7 | Auto-generated client packages for Python/JS/Go from the OpenAPI spec. Useful for community integrations. |
| **Generated docs for multiple versions** | §49.7 | Swagger UI multi-spec selector showing v0.0.1, v0.1.0, etc. |
| **Sequence templates expansion** | GAPS-ARA Tier 3 | Beyond the 3 v0.0.1 templates (LRGB, SHO, comet). Community-contributed templates registry for DSO + comet workflows. |
| **WILMA mobile builds (iOS + Android)** | §18.G, §41 | Mobile companion mode (§41) shipped as iOS App Store + Google Play listings. Requires Apple Developer Program ($99/yr) + Play Console ($25 one-time) + per-platform review processes + signing-cert + privacy-manifest maintenance. v0.0.1 spec'd the mobile companion API surface so no server changes needed when v0.1.0 turns this on. TestFlight public beta as the iOS rollout-staging mechanism; Play Store open-testing track as the Android equivalent. |
| **OpenAstro Hub (community profile + sequence sharing)** | §70.6 | Central catalog at openastro.net/hub for browsing/rating/contributing share files. WILMA browse-and-import in-app. Curated starter packs per scope class. Builds on the v0.0.1 `profile-share-v1` / `.araseq.json` wire formats — no breaking changes. |
| **Concurrent multi-server (observatory mode)** | §30.8 | One WILMA managing N Pis with concurrent WS connections + tabbed UI + cross-rig stats rollups + aggregated notification feed + cross-rig single-emergency-stop + optional cross-rig sequence orchestration (mosaic-split, alternating targets). Engineering touch is per-server state forking throughout the app shell. |

### 55.2 v0.2.0 — Larger projects

Genuinely ambitious work for the v0.2.0 milestone:

| Feature | Notes |
|---|---|
| **Pre-built RPi OS image** | Alternative to .deb install — flashable image with everything pre-configured. Requires CI image-build pipeline. ASIAir-level zero-friction install. |
| **WCAG 2.1 AA formal certification** | Move from "AA-leaning baseline" (§53) to formal compliance with third-party audit. Only if observatory/outreach use justifies the cost. |
| **Light-mode theme variant** | Most users want dark; this is for daytime planning + outreach demo contexts. |
| **Web UI option** | A web frontend (Vue/React/Svelte) for users who don't want a desktop app. Same OpenAPI client + API surface. |
| **Native Flutter sky-renderer** | Replace Aladin Lite WebView with a pure-Flutter sky atlas using Skia direct rendering. SharpCap/SkySafari quality, no WebView overhead. |
| **Imaging campaigns / adaptive scheduling** | Multi-target survey programs; "image whichever target is best right now" scheduler. Beyond manual sequences. |
| **Plugin marketplace UI** | Once SDK is stable, an in-app browsable plugin store (the plugin browser UI §10 ships in v0.0.1 but pointing at an empty manifest). |

### 55.3 Out of scope indefinitely

Items deliberately not on any roadmap (avoid scope-creep pull):

- **Native INDI / INDIGO protocol support** — committed Alpaca-only forever per §52. Bridges only.
- **In-app FITS post-processing** (stacking, integration, gradient removal, etc.) — out of scope; users use PixInsight/Siril/AstroPixelProcessor for that. ARA captures + organizes; processing is its own tool category.
- **Solar imaging specifics** (solar filter detection, prominence tracking) — niche; not on roadmap. Solar imagers can use ARA but ARA won't specialize for them.
- **Mount homing mechanical-knob automation** — physical altitude/azimuth knob automation requires hardware. ARA guides the human; doesn't drive knobs.
- **Astrometric measurement tools** (asteroid astrometry submission to MPC, supernovae search workflows) — research-grade workflows; out of scope for the imaging tool.
- **Planetary / lunar lucky-imaging** — high-frame-rate (5–30 fps) capture, ROI streaming, SER file output, surface-feature tracking. Architecturally blocked: Alpaca has no video API (per §52 Alpaca-only commitment), so the workflow primitive isn't available. NINA has the same limitation. Users wanting planetary use FireCapture / SharpCap / AstroDMx with vendor-native drivers — different tool category. Per §18.J this is permanent, not deferred.

### 55.4 What's NOT on this list (and why)

If a feature seems missing from this roadmap, it's likely either:
1. **Already in v0.0.1** — check the TOC; many things you might expect to be "future" are already in scope
2. **AI-handled during the port** — documentation, NINA-feature preservation verification, NOTICE.md, README rewrite, CONTRIBUTING.md updates
3. **A user-policy decision rather than a feature** — anything the user can already configure via existing settings (e.g., "make autofocus more aggressive") isn't a roadmap item
4. **Outside ARA's product scope** — see §55.3

---

## 56. Migrating from NINA

For existing NINA users coming to ARA, here's what's different and how to bring your work over. This section becomes the basis for a "Migrating from NINA" guide on the openastro-ara wiki/README during Phase 15.

### 56.1 What carries over

| What | How |
|---|---|
| Your sequence `.json` files | Import via WILMA → Sequencer → "Import from NINA" (§38.4). Server remaps equipment IDs to your current Alpaca devices; flags any unsupported instruction types for review. |
| Your imaging targets | Re-add manually in WILMA's Framing Assistant (Aladin Lite); search by name or coords; save as a target template. Existing FITS files keep their RA/Dec metadata, so old captures still align via §40.6 Resume Target. |
| Your imaging style (filters, exposures, gain/offset) | Configured fresh in the §37 wizard. Takes ~10 minutes for a standard rig. |
| Your existing FITS files | Drop them on the Pi's USB drive under any session folder; ARA's library scanner picks them up. Or process them externally in PixInsight/Siril unchanged. |
| Your bias/dark/flat libraries | Manually copy to `/media/openastroara/calibration/` matching the layout from §39.9. Or just re-capture fresh — for many users this is easier than migrating. |

### 56.2 What does NOT carry over

| Lost | Why |
|---|---|
| Your NINA profile | Different schema; rebuild in the wizard (§37) — ~10-15 minutes for a typical rig |
| Your AvalonDock UI layout | ARA's UI is fixed (§25); no dockable panels in v0.0.1 |
| NINA plugins | No plugin support in v0.0.1; plugin SDK in v0.1.0 (per §55.1) and authors must port to Avalonia-native API |
| Crowdin translations | English-only in v0.0.1 (§18.E); other languages may return in future versions |
| ASCOM COM equipment | Use AlpacaBridge on Windows to expose COM drivers as Alpaca (§52.2). Direct COM is gone permanently. |
| Native vendor SDK support | All native SDKs removed (Nikon, Canon, ZWO direct, QHY direct, etc.). Use the vendor's Alpaca driver (most vendors ship one) or AlpacaBridge. |
| MGEN guider integration | Removed; use PHD2 (via openastro-phd2 for cross-platform) |
| PlateSolve2 | Removed; use ASTAP (§18.I) |
| In-app updater | Removed (§18.A); update via APT (`sudo apt upgrade openastroara-server`) or WILMA-push (§33) |

### 56.3 What's BETTER in ARA

The reasons to migrate, honestly:

| Feature | NINA | ARA |
|---|---|---|
| **Cross-platform client** | Windows only | Mac, Linux, Windows desktop + iOS/Android companion |
| **Headless server architecture** | No — laptop must stay running | Yes — Pi runs unattended; close your laptop, imaging keeps going |
| **Disconnect-resilient imaging** | No | Yes — §27 + §32 |
| **Per-frame quality analytics** | Basic | Comprehensive (§50 Stats dashboard + §51 real-time diagnostics) |
| **Real-time cause diagnosis** | No | Yes — "clouds passing" vs "focus drift" vs "trees" auto-detected (§51) |
| **Session-matching flats (replay equipment state)** | No | Yes — §39.5 |
| **Multi-year target alignment via Resume Target** | Manual | Automated — server replays plate-solve + rotator angle (§40.6) |
| **Real-time backup stream to desktop** | No | Yes — §44 |
| **Multi-wavelength sky atlas (21 surveys, X-ray to far-IR)** | External tool needed | Native via Aladin Lite (§36) |
| **First-launch wizard guides full setup** | Sprawling settings tree | 18-screen guided wizard (§37) |
| **WILMA-pushed updates from app to Pi** | N/A | Yes — no internet on Pi needed (§33) |

### 56.4 Migration paths

**Quick start (most users)** — give it ~30 minutes for first session:
1. Install Pi OS Trixie + USB drive
2. `sudo apt install openastroara-server` (§34.1)
3. Open WILMA on your Mac, connect to the Pi
4. Run through the wizard (§37) — fresh profile setup
5. Import your one or two most-used NINA sequence files (§38.4)
6. First imaging session

**Deep migration (power users)** — bring everything over:
1. Quick-start steps 1-4
2. Manually copy `calibration/` from your old NINA setup to `/media/openastroara/calibration/`
3. Import all NINA sequence files
4. Run §40.6 Resume Target on existing target captures to verify alignment math still works
5. Spend a session validating that ARA's behavior matches what you expected from NINA

**Try-without-committing** — run both side by side:
1. Set up ARA Core on a Pi
2. Use ARA for one target, NINA for another in parallel (same night)
3. Compare results
4. Migrate fully once confident

### 56.5 Where to ask questions

- GitHub Discussions: `github.com/open-astro/openastro-ara/discussions`
- GitHub Issues for bugs: `github.com/open-astro/openastro-ara/issues`
- (Future) Discord / community channels when established

### 56.6 What ARA does NOT promise

In the interest of setting accurate expectations:
- ARA is not "NINA on Mac." It's a different architecture (headless server + cross-platform client) with different strengths and trade-offs.
- ARA is in v0.0.1. Polished UX takes iteration. Expect some rough edges that NINA-3.2 doesn't have.
- ARA is open-source and donation-supported. There's no commercial backing or guaranteed support timeline.
- Some NINA users will find ARA isn't right for them and will stay on NINA — that's a perfectly fine outcome. Stefan's NINA continues to be excellent for Windows users.

---

## 57. Stop Mount + slew safety

Centered on one principle: **whenever the mount is moving autonomously, the user can stop it in one tap.** A complementary control to §35.3 Emergency Stop, scoped narrowly to mount motion rather than full session abort.

### 57.1 Two distinct stop concepts, kept clearly separate

| Control | What it does | When visible | After triggering |
|---|---|---|---|
| **Stop Mount** (§57) | Issues Alpaca `AbortSlew()` — mount halts in place; nothing else touched | Contextual — only during autonomous slews | Sequence pauses; mount stays where halted; cooler/guider/etc. keep running. Modal prompts [Verify Position] / [Resume] / [Skip Target] / [End Session] |
| **Emergency Stop** (§35.3) | Full session abort — camera abort, guider stop, mount park, sequence aborted | Persistent — always visible in WILMA shell | Session over |

Different buttons, different colors, different positions, different consequences. Stop Mount = "this slew is going somewhere I don't like." Emergency Stop = "kill everything now." Users learn the distinction quickly because each only appears when contextually relevant.

### 57.2 When Stop Mount appears

The button surfaces (as a large, prominent overlay near the top of the active view) any time the mount is autonomously slewing under server control:

- **Sequencer-initiated slew** — slew to target, slew between mosaic panels, slew to flat target
- **Meridian flip slew** — the actual flip movement (§58)
- **Recovery slew** — §28 mount-home or slew-to-saved-target during crash recovery
- **Plate-solve re-center slew** — post-slew correction
- **Park / unpark / home** — these are also slews; the button appears
- **Polar-align initial RA rotation** — §45's 30° RA rotation for the seed

Does **not** appear for:
- Sidereal tracking (not a slew event)
- PHD2 guide corrections (sub-arcsecond, not user-visible motion)
- Manual slewing initiated from WILMA's manual control panel (those controls have their own abort built into the jog buttons)

The button disappears when the server detects `IsSlewing = false` plus a 1-second grace period (handles mounts that briefly stop between sub-slews).

### 57.3 Visual treatment

```
┌──────────────────────────────────────────────────────┐
│                                                       │
│  Mount is slewing to M81 (RA 9h55m, Dec +69°)        │
│  Est. arrival: 23 s                                   │
│                                                       │
│         ┌─────────────────────────┐                   │
│         │                         │                   │
│         │   ⛔  STOP MOUNT        │                   │
│         │                         │                   │
│         └─────────────────────────┘                   │
│                                                       │
│  Live position: RA 9h12m, Dec +52° → +69°             │
│                                                       │
└──────────────────────────────────────────────────────┘
```

Sized for panic-press: minimum 200×80 px on desktop, 280×80 pt on mobile. Red (`AraColors.accentError`). Single-tap, no confirmation gate (the button IS the confirmation). Z-index above modals so it's never hidden behind other UI.

Keyboard shortcut on desktop: **Space bar** when any slew is in progress. Single-press is fine — the button is only present during slews, so accidental Space-presses on other screens don't trigger it. Configurable per profile via `mount_safety.stop_mount_keyboard_shortcut_desktop`.

### 57.4 After the user taps Stop Mount

1. Server immediately issues `AbortSlew()` via Alpaca — universal call, every mount supports it (mandatory in `ITelescope`)
2. Server pauses the sequencer (in-flight instruction marked `paused`)
3. WebSocket event `mount.slew_aborted` fires; all connected clients update
4. WILMA shows a clear modal:

```
┌──────────────────────────────────────────────────────┐
│  Mount stopped at user request                        │
│  ──────────────────────────────────────────────────  │
│  Current position: RA 9h34m, Dec +61°                 │
│  Sequence paused at: "Slew to M81" (instruction 4/47) │
│                                                       │
│  Cooler, guider, and other equipment are still        │
│  running. Verify the mount is in a safe position      │
│  before resuming.                                     │
│                                                       │
│  [ Verify Position — show me an image ]               │
│  [ Resume slew to M81 ]                               │
│  [ Skip this target ]                                 │
│  [ End session ]                                      │
└──────────────────────────────────────────────────────┘
```

**[Verify Position — show me an image]** triggers a quick exposure (default 1-2 s) and plate-solves it; result shows where the mount is actually pointing vs where the slew intended. Lets the user confirm "OK, this is fine" before any further movement.

### 57.5 Latency target

Tap → `AbortSlew()` issued: **< 200 ms on LAN, < 500 ms over AP mode.** Tap → mount mechanically halted: depends on mount and slew speed but typically another 0.5-2 s. Total panic-to-stop budget: **< 3 seconds in the worst case.** Server logs the actual latency per Stop Mount incident in the `faults` table so we can tune.

### 57.6 Safety-speed slews

During all autonomous slews (sequencer, recovery, flip, plate-solve re-center, polar-align seed rotation), the server uses a **reduced slew rate** — default 50% of mount max, configurable per profile. Rationale: slower slew + fast Stop Mount = more time for the user to react before damage.

Manual slews from WILMA's control panel use the user-selected rate (the user has hands on the rig and can see what's happening). The safety speed applies only to server-initiated autonomous motion.

For mounts already at limited slew rates (some strain-wave mounts), this is a no-op. For fast mounts (CEM120, EQ8) it adds maybe 10-30 seconds per session transition, in exchange for meaningful "user can intervene before damage" headroom.

Profile setting:

```json
{
  "mount_safety": {
    "autonomous_slew_rate_pct": 50,
    "stop_mount_keyboard_shortcut_desktop": "Space"
  }
}
```

### 57.7 Hardware kill switch — DEPLOY.md recommendation

Software stop has Alpaca + network + mount-controller + mechanical latency in series. For expensive rigs, DEPLOY.md documents the recommendation: wire a physical e-stop button to cut mount power (mains-side or 12V-side). That's the gold standard. ARA does not implement this; ARA recommends it in user-facing docs.

### 57.8 API endpoints

| Method | Path | Purpose |
|---|---|---|
| `POST` | `/api/v1/mount/stop` | Issue `AbortSlew()` + pause sequence. Body empty. Returns current mount state + sequence state. Idempotency-Key required (§60.5). |
| `GET` | `/api/v1/mount/state` | Current mount state (slewing/idle/tracking/parked), position, predicted target if mid-slew |

WebSocket events:

```json
{ "type": "mount.slew_started",  "payload": { "target_ra": ..., "target_dec": ..., "estimated_seconds": ... } }
{ "type": "mount.slew_aborted",  "payload": { "halted_at_ra": ..., "halted_at_dec": ..., "reason": "user_request" } }
{ "type": "mount.slew_complete", "payload": { "final_ra": ..., "final_dec": ..., "duration_seconds": ... } }
```

### 57.9 What's deferred to v0.1.0 (Mount Safety v2)

The lean v0.0.1 scope addresses the panic-button gap. The following are deferred to a v0.1.0 "Mount Safety v2" pass once we see how lean §57 lands in practice:

- Horizon profile (alt-vs-azimuth table for declared obstructions)
- HA limit configuration (separate from `pause_after_min`)
- No-go polygons (oddly-shaped obstructions)
- High-dec slew warnings
- First-slew-on-profile confirmation (separate from §58's first-flip-confirm)
- Slew confirmation policy modes (`always_confirm` / `confirm_if_large` / `never`)
- Park-between-targets option

---

## 58. Meridian flip workflow

Builds on §57's primitives (Stop Mount, safety-speed slews, hardware-kill-switch recommendation). This section covers the specific orchestration of a meridian flip — pause windows, post-flip recovery, unattended-safety layers, graceful shutdown on failure.

### 58.1 What a meridian flip is (and isn't)

A German Equatorial Mount (GEM) — and every GEM-descendant including strain-wave mounts — tracks a target across the sky. When the target crosses the **meridian** (the N-S line through zenith), if the mount kept tracking, the OTA would eventually swing into the tripod, mount head, or pier. So the mount must flip — rotate the RA axis 180° and swing the camera to the other side — to keep imaging.

Three things must happen in a flip:
1. Stop imaging briefly (no exposures while the mount is moving)
2. Mount executes the flip slew
3. Re-acquire: plate-solve to confirm framing, optionally re-focus, restart guiding (PHD2 needs to know the axes are flipped)

**Important:** every GEM-descendant must flip eventually. Strain-wave mounts (AM5, HEM27/45, RST-135, NYX-101) **delay** the flip — typically 45-75 minutes past meridian — but they still flip. The advantage is a longer window, not a permanent exemption. The only setups that genuinely never flip are alt-az mounts (different geometry, but suffer field rotation) and true fork-on-wedge equatorial mounts (rare today).

### 58.2 Timing windows — three numbers, all configurable per profile

| Knob | Default | What it means |
|---|---|---|
| `pause_before_min` | 1.0 min | Stop starting new exposures within 1 min of meridian crossing |
| `pause_after_min` | 5.0 min | Wait this long *after* the crossing before flipping (target is safely past the meridian) |
| `max_wait_after_min` | `pause_after_min + 10` | If something prevented the flip from happening within this window, give up on this target |

**`pause_after_min` is a hardware constraint, not a user preference.** It expresses *"this is when my specific physical rig must flip by, before the OTA risks collision."* It depends on OTA length, dovetail saddle height, pier height, counterweight position. Get it wrong and the OTA can swing into the tripod.

### 58.3 Rule-of-thumb table per rig class

The wizard (§37.3 Screen 8) suggests a starting value based on rig class:

| Rig class | Suggested `pause_after_min` |
|---|---|
| Long SCT (C11, C14) / Long Newtonian on GEM | 1-2 min |
| Medium SCT/RC (8″) on GEM | 3-5 min |
| Medium refractor (ED102, FSQ-85) on GEM | 5-10 min |
| Tiny refractor (RedCat, FRA300) on GEM | 10-15 min |
| Any OTA on strain-wave mount (AM5, HEM, RST, NYX) | 45-75 min (still flips, just much later) |
| Fork-on-wedge equatorial (rare) | `enabled: false` |
| Alt-az / fork alt-az | `enabled: false` |

This is a starting suggestion; user adjusts for their specific setup. Pier height, saddle plate, dovetail length, and counterweight position all shift the actual safe value. Wizard prompts user to verify the first flip in-person.

### 58.4 Post-flip recovery — what runs, in what order

1. **Slew to target RA/Dec** — explicit slew after the flip completes, to clean up any drift
2. **Plate-solve + re-center** — mandatory. Tolerance per §28.2 (60″ position, 1° rotation). Up to 3 retries.
3. **Re-focus** — *conditional*. Default policy: re-focus only if sensor temp has drifted > 2°C since last AF run. Three options: `always` / `if_temp_drifted` / `never`.
4. **Restart guiding** — PHD2 needs to know the axes flipped. Two modes:
   - `auto_restore` (default) — PHD2's "Auto Restore Calibration" with reverse-Dec flag set. Fast (~30 s).
   - `full` — full PHD2 recalibration. Slow (5-10 min) but most reliable for finicky mounts.

### 58.5 Side-of-pier verification

After the flip, ARA queries Alpaca's `SideOfPier`. If it didn't change (or driver returns `pierUnknown`), log a warning but continue — some Alpaca drivers lie about pier side. If the driver doesn't expose `SideOfPier` at all (`CanSetPierSide = false`), ARA infers pier side from hour angle. No brand-quirk database (per §52 Alpaca-only commitment).

### 58.6 Profile schema

Profile JSON gains a `meridian_flip` block (lives alongside §35.6 safety policy and §57 mount_safety):

```json
{
  "meridian_flip": {
    "enabled": true,
    "mode": "auto",                          // "auto" | "prompt" | "never"
    "pause_before_min": 1.0,
    "pause_after_min": 5.0,
    "max_wait_after_min": 15.0,
    "recenter_after_flip": true,
    "refocus_after_flip": "if_temp_drifted", // "always" | "if_temp_drifted" | "never"
    "refocus_temp_threshold_c": 2.0,
    "guider_recal": "auto_restore",          // "auto_restore" | "full" | "skip"
    "skip_target_if_below_floor": true,
    "first_flip_confirmed": false            // see §58.8
  }
}
```

For non-flipping setups (alt-az detected via Alpaca `AlignmentMode`, fork-on-wedge configured manually), set `enabled: false` and the meridian-flip UI is hidden from that profile's wizard + Settings.

### 58.7 Failure handling matrix

| Failure | Action |
|---|---|
| Mount slew error during flip | Retry 3× → pause sequence + critical notification |
| Post-flip plate-solve fails after 3 retries | Pause + warning notification (user can manually re-center) |
| Re-focus fails | Log warning, continue without re-focus (don't abort the night over a bad AF run) |
| Guider re-cal fails | Retry → pause + warning |
| Target below profile's hard altitude floor after flip | Skip target, advance to next; surface "M42 below horizon after flip" notification |

All equipment-impacting failures are subject to the unattended-safety pipeline in §58.9 if no user is present.

### 58.8 First-flip confirmation safety net

When a profile fires its meridian-flip trigger **for the first time** (`first_flip_confirmed: false`), instead of flipping autonomously, ARA sends a critical notification ~60 seconds before the flip:

> ⚠ **First meridian flip on this profile**
>
> About to flip in 60 seconds. This is the first flip ARA has run for "C14 on CEM120" — verify your `pause_after_min` value (currently 2 min after meridian) is safe for this rig.
>
> [Proceed] [Pause sequence — let me check]

If user confirms or doesn't respond in 60 s, flip proceeds. Sets `first_flip_confirmed: true` on the profile so subsequent flips run silently. Reset on any equipment change in the profile (the safety net assumes the rig hasn't changed since user verified).

Catches the one failure mode that actually breaks gear: user creates a new profile, gets the timing number wrong, OTA tries to flip into the tripod. The 60-second pause is cheap insurance.

### 58.9 Unattended flip safety — four independent layers

For overnight unattended sessions (the most common astrophotography use case), four layers of defense protect a sleeping user. Each layer catches a different failure mode independently.

**Layer 1: Pre-flip flight check (runs ~2 minutes before the flip)**

Before issuing the flip slew, server verifies:

- **Endpoint prediction safe** — predict post-flip RA/Dec/alt/az. If below profile's hard altitude floor, skip target instead of flipping.
- **Mount reports healthy state** — no Alpaca-reported faults, tracking active, not parked, communication OK
- **Required equipment connected** — camera, guider (if re-cal enabled), focuser (if re-focus enabled). If anything is disconnected, attempt §42.3 hot-reconnect; if reconnect fails, abort the flip.
- **Predicted slew duration sane** — if mount estimates the flip slew will take longer than expected (e.g., > 90 s for a typical mount), suspect a stuck axis or driver confusion; abort and notify.

If any check fails, the flip does **not start**. Sequence pauses, urgent notification fires. Mount stays in pre-flip state (tracking sidereal) — a known-safe configuration.

**Layer 2: Watchdog during the flip slew**

Once the slew is in flight, a server-side watchdog samples mount state every 5 seconds:

- **Position must be progressing** — if reported RA/Dec hasn't changed for 15 seconds, mount is stalled → issue `AbortSlew`, mark flip failed
- **Hard timeout** — flip slew must complete within 3× predicted duration or 5 minutes, whichever is shorter. Exceeded → `AbortSlew`, fail.
- **No Alpaca fault events** — driver reports a fault mid-slew → `AbortSlew`, fail
- **Pier side must change** — if mount reports `SideOfPier` unchanged after `IsSlewing = false`, flip likely didn't actually flip → fail (don't resume imaging on a possibly-still-on-wrong-side mount)

**Layer 3: Post-flip verification gate**

After the slew completes, before *any* imaging resumes:

- **Plate-solve mandatory** — up to 3 retries (per §58.4)
- **If all retries fail: imaging does NOT resume.** Sequence paused, urgent notification fires. Better to lose the rest of the night than to image with a misaimed scope.
- **Solved position must be within sanity bounds** of the intended target (default ± 2°). If solve succeeds but says "you're 30° off where you should be," fail rather than trust it.

**Layer 4: Safe rest state on any failure**

When any of Layers 1-3 trigger a failure, the mount goes to a known-safe state immediately:

- If `Park` is configured and `CanPark = true` → **park the mount**. Safest possible position.
- If `Park` is not available → stop tracking, leave mount where the abort caught it.
- Cooler stays running (user may want to resume if conditions allow)
- Guider stopped (PHD2 corrections on a mis-aimed scope can drift further)
- Sequence marked paused; in-flight target's frames preserved
- Urgent notification fires — bundled looping alarm audio plays on any connected WILMA device, per §35.5

### 58.10 Severity escalation during unattended hours

Profile gains an `unattended_hours` range (default: from astronomical dusk to astronomical dawn at site, or user-set time window). During unattended hours, all equipment-impacting event severities are **bumped one level**:

- `warning` → `critical`
- `critical` → `urgent`

Same events, louder consequences, only when the user is presumed asleep. User adjusts the hours in profile if their schedule differs (e.g., a remote-observatory user with the observatory on a different timezone).

### 58.11 Connecting to "how the user hears about it"

v0.0.1's "no push notifications" limitation (§46.9) means the practical answer for the sleeping user is:

**Keep a desktop or tablet running WILMA on a device that is audibly near where you sleep.** The bundled safety alarm (§35.5) loops at max volume on urgent notifications until acknowledged. A Mac in the bedroom, an iPad on the nightstand, or a Linux laptop downstairs — anything that can play audio and has WILMA running.

DEPLOY.md adds explicit guidance: *"For unattended overnight sessions, keep at least one WILMA device running with audio enabled near where you sleep. The urgent-alarm pattern is your wake-up signal."*

True push notifications (FCM/APNs to phone lock screen even with WILMA closed) are committed v0.1.0 per §46.9 and §55.1.

### 58.12 Unattended-failure graceful shutdown (10-minute countdown)

Triggers from **any** urgent-severity equipment-impacting failure where the sequence has paused awaiting user input. The flip failure case is one instance; mid-sequence equipment fault is another; storage unmount mid-night is another. Same pattern handles them all.

**The countdown:** when an urgent failure fires and the sequence enters `paused_awaiting_user` state, server starts a 10-minute countdown.

What resets the countdown (= "user has come back"):
- User acknowledges the urgent notification via WILMA (tap [Acknowledge])
- User issues any explicit API command (resume / abort / skip / Stop Mount / emergency stop / equipment control)
- User opens a new WebSocket connection from any WILMA device

What does NOT count as user attention:
- WILMA polling `/state` in the background
- Pre-existing WebSocket connections that have been quiet (heartbeat pings only)
- Background automated processes

If 10 consecutive unattended minutes elapse, **graceful shutdown executes**.

**Shutdown sequence (in strict order, with timeouts):**

1. **Stop guider** (PHD2 stop, ~1 s)
2. **Warm cooler** — set target to ambient, ramp down at profile's configured rate (default 1°C/min, same as §28's ramp-up). Slow step — typically 10-30 min for a −10°C → +20°C ramp. Server doesn't block on this; warmup runs in background while subsequent steps execute.
3. **Park mount** if `CanPark = true` and a park position is configured (~30-60 s)
4. **Disconnect filter wheel / focuser / rotator / flat panel** (~5 s each)
5. **Disconnect camera** — *after* warmup completes. Important: don't disconnect mid-warmup; that strands the cooler at a cold setting and the sensor warms violently when power drops. Camera disconnect waits for ramp completion or a hard 30-min cap, whichever comes first.
6. **Mark session `ended_at`** in DB with reason `unattended_shutdown_after_failure`
7. **Stop the urgent alarm** — situation is now stable; no more siren

What stays running:
- The ARA server itself (so user can reconnect, review logs, see what happened)
- mDNS announce (so WILMA can still find the Pi)
- Notification system (both the failure event and the shutdown event remain in the feed)

**Why 10 minutes specifically** — Goldilocks zone. Long enough for an alarmed user to grab their phone, fumble to WILMA, and tap acknowledge. Short enough that a sleeping user who genuinely doesn't wake up doesn't burn 6 hours of battery. NINA has no equivalent (it just keeps running); ASIAir powers things off harder on some failures but the threshold isn't user-configurable. ARA splits the difference and gives the user the dial.

**Configuration in profile:**

```json
{
  "unattended_failure_shutdown": {
    "enabled": true,
    "wait_minutes": 10,
    "actions": ["stop_guider", "warm_cooler", "park_mount", "disconnect_equipment"],
    "keep_server_running": true,
    "stop_alarm_after_shutdown": true
  }
}
```

The 10-min wait is configurable per profile (5 for battery-limited field rigs, 30 for wall-powered observatories). Disabling shutdown entirely is allowed but not recommended; wizard flags it as such.

### 58.13 What the user sees when they come back

When user reconnects to ARA the next morning, the first-screen-after-server-handshake shows a clear post-incident summary:

```
┌──────────────────────────────────────────────────────┐
│  Session ended overnight                              │
│  ──────────────────────────────────────────────────  │
│                                                       │
│  At 03:14 last night, meridian flip on M81 failed:    │
│  plate-solve after flip could not converge (3/3       │
│  retries failed).                                     │
│                                                       │
│  Sequence paused, urgent alarm fired for 10 min       │
│  with no response → equipment shut down gracefully    │
│  at 03:24.                                            │
│                                                       │
│  Frames captured: 47 (M81, all rated)                 │
│  Cooler warmed and camera disconnected normally.      │
│  Mount parked.                                        │
│                                                       │
│  [ Review failure details ]  [ View frames ]          │
│  [ Resume session ]  [ End session ]                  │
└──────────────────────────────────────────────────────┘
```

### 58.14 Pre-sleep checklist

Just before astronomical dusk, ARA runs a one-shot self-test and surfaces a pre-sleep summary in WILMA:

```
Tonight's sequence — pre-flight check

✓ All equipment connected and healthy
✓ Storage: 412 GB free
✓ Plate solver tested OK
✓ Cooler at target (−10 °C)
✓ Mount safety: tracking + slew rate verified

Planned meridian flips tonight:
  • 02:14 — M81 (alt 67° at flip, OK)
  • 04:33 — NGC 6188 (alt 12° at flip — BELOW soft warning, above hard floor)

Unattended hours: 23:00 — 06:30
Alarm device: macbook-bedroom.local (verified audible)

  [ All good — let me go to bed ]   [ Adjust ]
```

User taps "all good" once before sleeping. Catches mis-set thresholds, disconnected equipment, low storage, etc., before they become 3am alarms.

### 58.15 WebSocket events

Beyond the §46.3 catalog entries (`meridian_flip.imminent` info, `meridian_flip.starting` info, `meridian_flip.complete` info, `meridian_flip.failed` critical), §58 adds:

```json
{ "type": "meridian_flip.preflight_failed",   "payload": { "reason": "endpoint_below_floor", "target": "M42" } }
{ "type": "meridian_flip.watchdog_aborted",   "payload": { "stage": "slew", "reason": "position_stalled" } }
{ "type": "meridian_flip.postflip_solve_failed", "payload": { "retries": 3, "last_error": "..." } }
{ "type": "unattended_shutdown.countdown_started", "payload": { "wait_minutes": 10, "trigger_event_id": "..." } }
{ "type": "unattended_shutdown.completed",   "payload": { "actions_taken": [...], "duration_seconds": ... } }
```

### 58.16 API endpoints

| Method | Path | Purpose |
|---|---|---|
| `GET` | `/api/v1/meridian-flip/state` | Current flip state (idle/imminent/executing/complete/failed); time-to-next-flip estimate |
| `POST` | `/api/v1/meridian-flip/skip` | Skip the upcoming flip; advance to next target instead |
| `POST` | `/api/v1/meridian-flip/confirm-first` | User confirms the first-flip prompt (§58.8) |
| `GET` | `/api/v1/preflight/today` | Pre-sleep checklist data (§58.14) |
| `POST` | `/api/v1/unattended-shutdown/acknowledge` | User acknowledges urgent failure, cancels countdown |

All mutating endpoints require `Idempotency-Key` (§60.5).

### 58.17 What's deferred to v0.1.0

- Per-target flip overrides in the sequence (per-target "always re-focus after flip" etc.)
- Custom `BeforeMeridianFlip` / `AfterMeridianFlip` user hook scripts (folds into plugin/scripting v0.1.0 per §55.1)
- Mount-driven trigger mode (using mount's reported pier-side change instead of HA timing — relies on driver honesty; v0.1.0 once Alpaca driver compliance settles per §52.5)
- "Permitted side of meridian" constraint (advanced — force always-east or always-west imaging)
- Push notifications to phone lock screen (FCM/APNs — §46.9 v0.1.0)

---

## 59. Autofocus — Smart Focus + Classic AF fallback

The most-tweaked NINA subsystem, redesigned. NINA's autofocus panel exposes 17 settings the user must understand before they can focus. **ARA exposes 6, and discovers the rest from observation.** This section is one of the §0.5 "better than NINA" wins.

### 59.1 Two modes — Smart Focus (primary) + Classic AF (calibration + fallback)

| Mode | When used | Duration per run |
|---|---|---|
| **Smart Focus** | After calibration. Every routine AF trigger uses this. | 30-90 s (2-3 exposures) |
| **Classic AF** (9-step parabolic/hyperbolic curve, HocusFocus star detection, inherited from NINA) | First calibration on a new profile. Fallback when Smart Focus diverges. User-requested via manual "Run full focus curve" button. | 3-5 min (9-11 exposures) |

Smart Focus is the visible mode; Classic AF runs only when needed. Users typically only see Classic AF on first night per profile (calibration) and almost never again.

### 59.2 The core insight behind Smart Focus

A defocused image carries information about *how far* and *which direction* it's out of focus, not just *that* it's out of focus. SCTs, Maksutovs, RCs, and Newtonians defocus into donuts whose diameter scales linearly with distance from focus; refractors broaden in characteristic FWHM patterns. Once we've learned the relationship between defocus distance and image features for a specific rig, **we don't need a 9-step curve every time — we need one image to read the rig's current state and predict the correct focuser move.**

NINA doesn't do this. ASIAir doesn't either. PixInsight has a research-grade tool for it but it's offline-only. ARA brings it to the live capture pipeline.

### 59.3 Smart Focus algorithm

**Phase 1 — Calibration (once per profile, ~5 minutes, auto-runs on first AF trigger of a new profile):**

Server runs a traditional 9-step Classic AF curve. At each step it records a feature vector richer than HFR alone:

- HFR (Half-Flux Radius)
- Star FWHM
- **Donut outer diameter** (obstructed scopes — SCT, Mak, RC, Newtonian)
- **Donut inner diameter** (central obstruction shadow)
- **Donut ring thickness** = outer − inner
- **Asymmetry coefficient** — intra-focal vs extra-focal star profiles differ; this disambiguates "which side of focus" we're on
- **Median star roundness**
- **Background-corrected star peak**
- **Stars detected count**

Server fits the curve as before and stores the **inverse mapping**: given a feature vector, what focuser offset would produce it? This calibration table persists in the profile across sessions.

After the curve, a backlash probe routine runs (§59.7) — adds 60-90 s.

After backlash probe, a collimation health check runs (§59.10) — adds zero time (uses calibration images already captured).

Total calibration cost: **~5 minutes, one-time per profile.**

**Phase 2 — Smart Focus run (every subsequent AF trigger):**

```
1. Take one exposure at current focuser position (5 s default)
2. Extract feature vector from the frame (same metrics as calibration)
3. Predict offset + direction by looking up the feature vector in the
   calibration table — gives both magnitude AND sign
4. Move focuser by predicted offset, applying backlash compensation
   (§59.7 — auto-discovered, no user input)
5. Take second exposure, measure HFR
6. Done in TWO shots IF:
     - HFR within target tolerance (default 5% above session-best
       HFR for this filter)
     - HFR improved vs shot 1
7. Done in THREE shots IF: HFR improved but missed target →
   small correction (±20% of step 3's magnitude), final exposure
8. Fall back to Classic AF IF: HFR got worse after shot 2 (direction
   prediction wrong) OR feature vector outside calibration range OR
   fewer than 30 stars detected OR prediction confidence < threshold
```

Typical time: **30-90 seconds.** vs Classic AF's 3-5 minutes.

### 59.4 Telescope-type model

The features that matter differ by optical design. User declares once in the wizard:

| Type | Primary defocus features used |
|---|---|
| **Refractor** (no central obstruction) | FWHM, asymmetry coefficient, peak-to-background ratio |
| **Schmidt-Cassegrain (SCT)** | Donut outer/inner diameter, ring thickness, central shadow depth |
| **Maksutov-Cassegrain** | Same as SCT but with tighter inner-hole expectations (smaller secondary) |
| **Ritchey-Chrétien (RC)** | Donut features with smaller central obstruction model |
| **Newtonian** | Donut features + diffraction spike length/angle (spider vanes shift visibly when defocused) |
| **Other / unknown** | Fall back to HFR-only (Classic AF behavior) |

Wizard's telescope screen captures this once. Server uses the right feature extractor automatically. Tooltip on the dropdown shows example out-of-focus star images per type so the user understands what they're picking.

### 59.5 Triggers — when ARA auto-runs Smart Focus

Six trigger conditions, OR'd together, individually configurable:

| Trigger | Default | What it catches |
|---|---|---|
| **Sequence start** | ON | Establishes baseline |
| **Time interval** | 90 min | Long-term drift catch-all |
| **Sensor temp Δ** | 1.5 °C since last AF | Temperature is the dominant drift driver |
| **HFR drift** | 15% above session median over 3 frames | Catches drift the time-interval missed |
| **Post-meridian-flip** | per §58.4 `refocus_after_flip` policy | OTA flexure + temp drift during flip |
| **First use of a filter** | ON | Discovers per-filter focus offsets |

Each trigger consults `GET /api/v1/diagnostics/current` before firing (§59.9). Non-nominal diagnostic states defer the AF.

### 59.6 Filter policy — `use_current_filter` only

AF always runs on whichever filter is currently in the wheel. No swap-to-luminance behavior.

**Why this matters:** users with non-parfocal filters would have their focuser positioned wrong if AF swapped to L and then back to (e.g.) Hα. Per-filter offsets are learned naturally as the sequencer uses each filter for the first time and triggers AF (per the "first use of a filter" trigger).

If a filter has too few stars (< 30 detected at the calibration exposure time), server doubles exposure and retries once. Still fewer than 30 stars → AF skipped with a notification: *"Insufficient stars on this filter for autofocus. Continuing with previous focus position. Consider manual focus check from the equipment panel."*

The `always_luminance` alternative was considered and rejected: too dangerous when filters aren't parfocal, too time-costly when they are (extra swap = extra delay = extra mount tracking variance).

### 59.7 Backlash auto-discovery (three layers)

Backlash is **never manually measured by the user**. Three layers handle it:

**Layer 1 — Backlash probe routine (appended to first calibration, ~60-90 s):**

After the calibration curve completes, server runs a dedicated probe:

```
1. Move focuser to predicted-best position P₀
2. Capture, measure HFR_baseline (should be near curve minimum)
3. Move IN by step_size × 4 → P₀ − 4·S
4. Capture, measure HFR_in_check
5. Move OUT by step_size × 4 → back to P₀ (in theory)
6. Capture, measure HFR_return_attempt
   ├─ If HFR_return ≈ HFR_baseline → zero backlash on OUT direction
   └─ If HFR_return > HFR_baseline → focuser didn't actually return

7. If backlash detected: move OUT additional small steps (10, 20, 40),
   capture each, find the step count where HFR snaps back to baseline.
   That step count = OUT-direction backlash.

8. Once OUT backlash known, apply compensation and verify HFR returns
   cleanly. Lock in OUT backlash value.

9. Repeat with directions swapped (OUT first, IN correction) to
   measure IN-direction backlash.

10. Save both values to profile. Mark backlash_verified = false.
```

8-10 extra exposures × ~6 s each = 60-90 s. One-time per profile.

**Layer 2 — Passive refinement during every Smart Focus run:**

Every direction change is a backlash data point. Server tracks:
- Commanded delta vs achieved HFR delta vs predicted HFR delta
- If achieved HFR delta < predicted → backlash absorbed motion
- Magnitude of discrepancy refines backlash estimate

After N stable runs (default 5) with backlash variance < 10%, `backlash_verified = true` flips. Refinement still runs but only acts on >2σ anomalous observations.

**Layer 3 — Equipment-change invalidation:**

Server identifies focusers via Alpaca's `DriverInfo` + `Name` + device-ID. When any of those changes:
- Profile's `focuser.device_signature` is updated
- Backlash values invalidated (set to null, `auto_discover` reset to true)
- Calibration curve invalidated
- Next AF trigger triggers full re-calibration including backlash probe

User-visible notification: *"Focuser changed — re-calibrating Smart Focus on next AF run."* User can also manually invalidate via Settings → Focuser → [Re-calibrate Smart Focus].

Compensation mode defaults to `overshoot` (forgiving, works on focusers with non-linear backlash). `absolute` mode is available via the advanced disclosure for users on absolute-encoder focusers like ZWO EAF Pro.

### 59.8 Curve fitting + Classic AF specifics (calibration + fallback path)

When Classic AF runs (calibration or fallback):

- **Primary algorithm:** parabolic with weighted least-squares
- **Auto-fallback:** hyperbolic if parabolic R² < 0.85
- **Trendlines:** available via advanced disclosure for unusual star profiles
- **Steps per run:** 9 (4 above + center + 4 below)
- **Step size:** auto from focuser's reported step size + previous curve width
- **Exposure per step:** 5 s default
- **Star detection:** HocusFocus algorithm (inherited from NINA), threshold 5σ above background, minimum 30 stars

### 59.9 Diagnostic integration — smart skip during bad conditions

Per §51 diagnostics: if the live signal pattern matches `clouds_passing`, `aperture_blocked`, or `dew_formation`, **AF triggers defer** rather than fire.

Smart Focus run takes 30-90 s and Classic AF fallback takes 5 min; running either during clouds will just fail and waste sky time. Better to wait for conditions to recover, then fire AF, then resume imaging.

Implementation: every AF trigger consults `GET /api/v1/diagnostics/current` before firing. If diagnostic state is non-nominal, queue the AF for "fire when state returns to green." User-visible notification: *"Autofocus deferred — clouds passing. Will run when conditions recover."*

NINA fires AF regardless of conditions. This is a meaningful sky-time savings per §0.5 pillar 1.

### 59.10 Collimation health detection — free byproduct of calibration

Calibration already captures donut images at multiple defocus distances. For obstructed scopes (SCT / Mak / RC / Newtonian), server extracts:

- Per-star donut outer-ring centroid
- Per-star donut inner-ring centroid (central obstruction shadow center)
- Per-step centroid offset vector (magnitude + direction)
- Per-step ring asymmetry coefficient

In perfect collimation, outer and inner centroids coincide. Offset → secondary tilt (SCT/Mak/RC) or primary+secondary tilt (Newtonian).

**Robustness:** server averages measurements across many stars near the center of the FOV (off-axis stars are affected by coma/field-curvature), and across multiple defocus steps (real miscollimation grows linearly with defocus; atmospheric seeing produces random offsets). False-positive resistance is the priority.

**Severity thresholds:**

| Centroid offset (% of donut diameter) | Verdict | Surface |
|---|---|---|
| < 5% | "Collimation looks good" | Info line in calibration completion summary |
| 5-15% | "Slight miscollimation detected" | Warning notification with clock-position direction |
| > 15% | "Significant miscollimation — recommended to collimate before continuing" | Critical notification |

**Direction reporting — clock position viewed from behind the eyepiece:**

> *"Donut centroid offset: 12% of donut diameter, toward 4 o'clock (viewed from behind the eyepiece).
> For your Schmidt-Cassegrain, this typically means the secondary mirror is tilted. Consult your scope's collimation procedure documentation.
> [Acknowledge]  [Don't warn again this session]"*

Notification stays in the feed; user can revisit anytime.

**What v0.0.1 does NOT do:**

- Prescriptive screw-turning guidance (per-scope-model + per-mount-orientation; consequences of bad guidance are real — wait for community-curated knowledge base in v0.1.0)
- Auto-collimation routines (require motorized secondary or robotic collimation tools — out of scope)
- Refractor collimation detection (signature is subtle in defocus images; deferred to v0.1.0 design pass)

### 59.11 Failure handling

| Failure | Action |
|---|---|
| Calibration curve quality bad (Classic AF couldn't fit even with hyperbolic fallback) | Notify user; abort calibration; Smart Focus disabled until manually re-attempted |
| Smart Focus shot 1 → shot 2: HFR worsened | Direction prediction wrong; reverse direction with half magnitude; take shot 3. If still worse → fall back to full Classic AF |
| Smart Focus diverges (3 shots, HFR still worse than start) | Restore original position; fall back to full Classic AF on next trigger; log; if happens twice in a session, mark calibration stale and queue re-calibration |
| Feature vector outside calibrated range (severely defocused) | Fall back to Classic AF — calibration only covers ±N steps |
| Diagnostic state non-nominal | Defer per §59.9 |
| Fewer than 30 stars detected at current filter | Double exposure, retry once; still <30 → skip AF + notify |
| Focuser stall (commanded position not reached) | Per §42.2 retry → recalibrate backlash → notify |
| Three consecutive AF runs with declining curve quality | Per §40.7 cycling-degradation pattern — notify *"AF can't catch this — probably transient atmospheric, not a focus issue"* + pause AF triggers for next 30 min |

### 59.12 UI during an AF run

Silent multi-minute UI is bad UX. WILMA shows a live AF panel during both Smart Focus and Classic AF:

```
┌──────────────────────────────────────────────────────┐
│  Smart Focus in progress (shot 2 of 2-3)             │
│  Filter: L   Calibrated: 2026-05-12                  │
│  ──────────────────────────────────────────────────  │
│                                                       │
│  Shot 1 — current position 14820:                     │
│    HFR 1.85   Donut Ø 24 px → prediction: move OUT 38│
│                                                       │
│  Shot 2 — predicted position 14858:                   │
│    HFR 1.42 ✓ (target ≤ 1.47)                         │
│                                                       │
│  [thumb of shot 2's frame]                            │
│                                                       │
│  [ Cancel — restore previous position ]               │
└──────────────────────────────────────────────────────┘
```

Classic AF (calibration / fallback) shows the traditional HFR-vs-position curve plot in real-time (as I specced in the earlier §59 draft):

```
┌──────────────────────────────────────────────────────┐
│  Calibrating Smart Focus (Classic AF, step 5 of 9)   │
│  Filter: L   Telescope: Schmidt-Cassegrain           │
│  ──────────────────────────────────────────────────  │
│  HFR                                                  │
│  3.5  ●                              ●                │
│  3.0     ●                        ●                   │
│  2.5        ●                  ●                      │
│  2.0           ●  ←now      ●                         │
│  1.5              ●  ?    ●                           │
│        14600  14700  14800  14900  15000              │
│  Stars: 487   Best so far: 14820 (HFR 1.4)            │
│  ──                                                   │
│  After this curve: backlash probe (~60s),             │
│  then collimation check (instant), then ready.        │
└──────────────────────────────────────────────────────┘
```

Implemented via existing WebSocket event stream — server emits `autofocus.shot_complete` / `autofocus.step_complete` events.

### 59.13 Profile schema

```json
{
  "autofocus": {
    "enabled": true,
    "telescope_type": "sct",                  // refractor | sct | mak | rc | newtonian | other
    "triggers": {
      "on_sequence_start": true,
      "time_interval_min": 90,
      "temp_delta_c": 1.5,
      "hfr_drift_pct": 15,
      "hfr_drift_consecutive_frames": 3,
      "post_meridian_flip": "if_temp_drifted",
      "first_use_of_filter": true
    },
    "target_hfr_tolerance_pct": 5,
    "diagnostic_skip_when_unstable": true,
    "classic_fallback_enabled": true,
    "backlash": {
      "auto_discover": true,
      "in_steps": null,
      "out_steps": null,
      "compensation_mode": "overshoot",
      "passive_refinement": true,
      "verified": false,
      "last_refined_at": null
    },
    "collimation_check": {
      "enabled": true,
      "warn_threshold_pct": 5,
      "critical_threshold_pct": 15,
      "last_check": null,
      "last_offset_pct": null,
      "last_offset_clock_position": null,
      "last_severity": null
    },
    "advanced": {
      "calibration_temp_delta_c": 8,
      "exposure_seconds": 5,
      "min_stars": 30,
      "classic_algorithm_primary": "parabolic",
      "classic_algorithm_fallback": "hyperbolic",
      "classic_fallback_r2_threshold": 0.85,
      "classic_steps_total": 9
    }
  }
}
```

**Six user-visible knobs.** Everything else (advanced + backlash details + collimation thresholds) hidden behind a disclosure. Compare to NINA's 17-knob panel.

### 59.14 Reformed wizard's focuser/telescope screen

```
Telescope type:        [▼ Schmidt-Cassegrain (SCT)             ]
                          Refractor
                          Schmidt-Cassegrain (SCT)
                          Maksutov-Cassegrain
                          Ritchey-Chrétien (RC)
                          Newtonian
                          Other / unknown

Focuser step size:     0.5 microns/step  (auto-detected from Alpaca)

Run focus on:          ☑ Sequence start
                       ☑ Filter change
                       ☑ Temp change > 1.5°C
                       ☑ Every 90 minutes
                       ☐ After meridian flip (configured per §58)

ARA will calibrate Smart Focus on your first autofocus run of
the night (~5 minutes, one-time per setup). It learns your rig
including backlash, and also checks collimation while it's at
it. After calibration, autofocus completes in 2-3 exposures.

  [< Back]    [Skip — use defaults]    [Next >]
```

**Five user-facing questions.** Zero backlash questions. Zero algorithm-internals questions. Vs NINA's seventeen.

### 59.15 API endpoints + WebSocket events

| Method | Path | Purpose |
|---|---|---|
| `POST` | `/api/v1/autofocus/run` | Manual trigger (runs Smart Focus if calibrated, else Classic AF) |
| `POST` | `/api/v1/autofocus/recalibrate` | Force full calibration (Classic curve + backlash probe + collimation check) |
| `POST` | `/api/v1/autofocus/cancel` | Cancel in-flight AF; restore previous position |
| `GET` | `/api/v1/autofocus/state` | Current state + current shot/step if running |
| `GET` | `/api/v1/autofocus/calibration` | Current calibration table (for display/debug) |
| `GET` | `/api/v1/autofocus/collimation` | Latest collimation check result |
| `GET` | `/api/v1/autofocus/history` | Per-session list of AF runs with metrics (for §50 Stats Focus & Temperature view) |

WebSocket events:

```json
{ "type": "autofocus.started",         "payload": { "mode": "smart" | "classic", "filter": "L", "trigger": "..." } }
{ "type": "autofocus.shot_complete",   "payload": { "shot_index": 1, "hfr": 1.85, "predicted_offset": -38, "stars": 487 } }
{ "type": "autofocus.step_complete",   "payload": { "step_index": 5, "position": 14820, "hfr": 1.43, "stars": 487 } }
{ "type": "autofocus.curve_fit",       "payload": { "algorithm": "parabolic", "r_squared": 0.94, "best_position": 14817 } }
{ "type": "autofocus.calibration_complete", "payload": { "best_position": 14817, "backlash_in": 23, "backlash_out": 18, "collimation_offset_pct": 4, "collimation_severity": "good" } }
{ "type": "autofocus.completed",       "payload": { "mode": "smart", "final_position": 14858, "final_hfr": 1.40, "duration_seconds": 47, "shots": 2 } }
{ "type": "autofocus.deferred",        "payload": { "reason": "clouds_passing" } }
{ "type": "autofocus.fallback_classic","payload": { "reason": "smart_focus_diverged" } }
{ "type": "autofocus.failed",          "payload": { "reason": "...", "restored_position": ... } }
{ "type": "collimation.warning",       "payload": { "offset_pct": 12, "clock_position": "4 o'clock", "scope_type": "sct" } }
```

### 59.16 §61 settings registry coverage

Every Smart Focus setting registered with appropriate `keywords`:

- `autofocus.enabled` — keywords: `autofocus, AF, smart focus, focus, automatic`
- `autofocus.telescope_type` — keywords: `telescope, scope, optical design, SCT, refractor, newtonian, RC, maksutov`
- `autofocus.triggers.time_interval_min` — keywords: `autofocus interval, focus frequency, AF every`
- `autofocus.triggers.temp_delta_c` — keywords: `temperature, temp, thermal, drift, focus on temp`
- `autofocus.triggers.first_use_of_filter` — keywords: `filter offset, per filter, filter change focus`
- `autofocus.collimation_check.enabled` — keywords: `collimation, alignment, donut, secondary mirror`
- `autofocus.classic_fallback_enabled` — keywords: `classic AF, full curve, NINA-style focus, fallback`

Search for "collimation" surfaces both the check-enabled toggle and the latest reading. Search for "backlash" surfaces the auto-discover toggle and current values in the advanced section.

### 59.17 What's NINA-preserved vs ARA-changed

**Preserved verbatim:** HocusFocus star detection algorithm, parabolic/hyperbolic/trendline curve fitting math, NINA's retry semantics within a Classic AF run, per-filter offset model, temp comp slope math.

**ARA additions:**
- Smart Focus feature-vector calibration table + inverse lookup
- 2-3 shot focus pattern after calibration
- Telescope-type model for feature selection
- Three-layer backlash auto-discovery
- Collimation health detection during calibration
- Diagnostic-skip integration with §51
- Equipment-change auto-invalidation
- Live UI panel showing per-shot progress
- Settings reduced from 17 to 6 visible
- §50 Stats Focus & Temperature integration (every AF run's curve / shots stored)

### 59.18 v0.1.0 expansion paths

- **ML-driven feature extraction** — small on-device CNN trained on (out-of-focus image → focuser offset) pairs collected from v0.0.1 users' calibration runs (opt-in telemetry). Improves prediction accuracy and reduces shot count to 1-2 typical.
- **Temperature-aware backlash model** — backlash varies with lubricant viscosity at cold temps. Build backlash-vs-temp curve per profile.
- **Load-aware backlash** — vertical-hang imaging trains pull on the focuser differently than horizontal pointing. Account for mount altitude.
- **Prescriptive collimation guidance** — per-scope-model + per-mounting-orientation screw-direction guidance, community-curated knowledge base (paired with `MOUNT_TIPS.md` pattern from §52.7).
- **Star-test mode** — dedicated workflow for diagnosing collimation without running a sequence. User taps "Check Collimation"; server takes a single defocused image, shows annotated centroid offsets per star across the field.
- **Refractor collimation detection** — research-grade signal extraction from out-of-focus Airy disk distortion.
- **Collimation drift trending** — multi-session collimation tracking in §50 Stats Equipment Health.
- **Tilt-aware focus** — focus position varies across the field. v0.1.0 measures sensor tilt and accounts for it.
- **Adaptive step pattern in Classic AF** — start wide, narrow on subsequent steps once a rough minimum is found.
- **Multi-star sampling** — track 5 specific named stars instead of bulk HFR median (better for non-uniform fields).

---

## 60. API conventions

Cross-cutting rules that apply to every endpoint defined in §9 and elsewhere in the playbook. Belongs alongside §9 (contract) and §49 (docs) conceptually; placed at the end to avoid renumbering existing cross-references.

### 60.1 Error response shape — RFC 7807 problem+json

Every non-2xx response carries an `application/problem+json` body following RFC 7807. ASP.NET Core's `ProblemDetails` middleware handles the bulk of this for free.

```json
{
  "type": "https://openastro.net/errors/equipment-not-found",
  "title": "Equipment not found",
  "status": 404,
  "detail": "No camera with id 'cam-abc123' is connected",
  "instance": "/api/v1/equipment/camera/cam-abc123",
  "deviceType": "camera",
  "knownDevices": ["cam-xyz789"]
}
```

- `type` URLs do **not** need to resolve to a live page in v0.0.1 (they're identifiers, not docs links). v0.1.0 may publish a real `/errors/` site.
- ARA-specific context lives as top-level extension fields, not nested under `extensions` (per RFC 7807 §3.2 — extension members are inline).
- 422 validation errors include an `errors` map keyed by JSON-pointer path, mirroring ASP.NET Core's default:
  ```json
  {
    "type": "https://openastro.net/errors/validation",
    "title": "Request validation failed",
    "status": 422,
    "errors": {
      "/schemaVersion": ["unrecognized version 'openastroara-sequence-v2'"],
      "/instructions/3/camera_id": ["camera 'cam-abc' not in active profile"]
    }
  }
  ```

### 60.2 Pagination — cursor-based, opaque token

All list endpoints (`/notifications`, `/frames`, `/sessions`, `/sequences`, `/mosaics`, `/stats/*` that return rows) follow the same shape:

```
GET /api/v1/notifications?limit=50&cursor=eyJ0cyI6IjIwMjYtMDUtMTkifQ

Response:
{
  "items": [ ... ],
  "next_cursor": "eyJ0cyI6IjIwMjYtMDUtMTgifQ" | null,
  "has_more": true
}
```

- Default `limit` = 50; maximum = 500; values outside the range clamp.
- `cursor` is opaque (base64-encoded JSON internally). Clients must not interpret or construct it; they only echo what the server returned.
- `next_cursor: null` + `has_more: false` means end of results.
- Endpoints where total count is genuinely useful (e.g., Stats Overview tiles) include `total` on the **first page only** (request without `cursor`). Subsequent pages omit it (computing total over a moving window is expensive and rarely needed once you're scrolling).

### 60.3 Request size limits

| Body type | Default cap | Override |
|---|---|---|
| JSON request body | 10 MB | `OPENASTROARA_MAX_JSON_MB` env var |
| Multipart upload (NINA import, backup restore, server update push) | 100 MB | `OPENASTROARA_MAX_UPLOAD_MB` env var |
| WebSocket message (in either direction) | 64 KB | not configurable |

Exceeding the cap returns 413 with a problem+json body explaining the limit + override path.

The server-update endpoint (§33.3) is a special case — its tarball is typically 30-50 MB; the 100 MB cap accommodates future growth without env-var tweaks.

### 60.4 `GET /api/v1/server/state` — UI rehydrate snapshot

Single endpoint that returns everything WILMA needs to redraw the app shell. Called on first connect and after every reconnect per §32.5. Not paginated; expected response size 5-50 KB JSON.

```json
{
  "server":          { "version": "0.0.1-ara.1", "status": "ready", "uptime_seconds": 12345, "session_id": "sess_abc" },
  "time_sync":       { ... §31.3 shape ... },
  "storage":         { ... §29.2 shape ... },
  "active_profile":  { "id": "prof_xyz", "name": "Backyard Texas" },
  "equipment": {
    "camera":  { "connected": true, "device_id": "...", "state": "idle" },
    "mount":   { "connected": true, "device_id": "...", "state": "tracking" },
    "focuser": { "connected": false },
    "filter_wheel": { "connected": true, "device_id": "...", "current_slot": 1 },
    "rotator":  { "connected": false },
    "guider":   { "connected": true, "rms_total": 0.42 },
    "dome": { "connected": false },
    "switch": { "connected": false },
    "weather": { "connected": false },
    "safety_monitor": { "connected": false },
    "flat_panel": { "connected": false }
  },
  "sequence":      { "id": "seq_def", "status": "running", "current_target": "M42", "current_instruction": "TakeManyExposures", "progress_pct": 47 },
  "current_frame": { "id": "frm_ghi", "exposure_remaining_seconds": 23.4, "preview_url": "/api/v1/frames/frm_ghi/preview" },
  "polar_align":   null,
  "diagnostics":   { ... §51.7 current shape ... },
  "active_alerts": [ ... open critical/urgent notifications, newest first ... ],
  "ws_resume_token": "ws_token_jkl"
}
```

**`ws_resume_token`** — the cleanest answer to "did WILMA miss any events while disconnected." Despite the name, this is a *replay cursor*, not an auth token (ARA has no API auth per §67). The server keeps a 1-hour ring buffer of WebSocket events. On reconnect, the client passes the cursor via WS query string (`?resume=ws_token_jkl`); server replays events the client missed, then resumes live streaming. Beyond 1 hour (or if the cursor is unrecognized), the client trusts the `/state` snapshot and starts fresh.

Any nullable section means "not currently active / not configured" — clients render accordingly rather than treating it as an error.

### 60.5 Idempotency — `Idempotency-Key` header, 24h dedup

Every mutating endpoint (POST / PUT / PATCH / DELETE) accepts an `Idempotency-Key: <client-generated-uuid>` header:

| Server sees | Behavior |
|---|---|
| Key + identical request body, within 24h | Returns the cached response verbatim |
| Key + different request body | 409 Conflict with problem+json explaining the mismatch |
| No key supplied | No dedup; request processed normally (client opted out) |

Required-vs-optional per-endpoint declared in the OpenAPI spec. Required on:

- `POST /api/v1/sequences/{id}/start`
- `POST /api/v1/server/emergency-stop`
- `POST /api/v1/polar-align/*`
- `POST /api/v1/server/update`
- `POST /api/v1/server/backup/restore`

Optional on read-only ops + naturally-idempotent ops (e.g., `PUT /api/v1/frames/{id}` rating update — setting the same rating twice is fine).

WILMA generates a fresh UUID per user-initiated mutation; HTTP-level retries after network blips reuse the key. Server-side storage: SQLite table `idempotency_keys (key TEXT PRIMARY KEY, request_hash TEXT, response_status INT, response_body BLOB, expires_at TIMESTAMP)` with 10k-entry LRU + nightly prune of expired rows.

### 60.6 Rate limiting

v0.0.1 keeps it minimal — LAN-only, single user, no auth (per §67), no real abuse vector:

| Surface | Limit |
|---|---|
| WebSocket connections | 1 active per Pi (enforced by §27 single-client policy) |
| General API requests | None |
| Backup-stream pull (§44) | Client-side token-bucket bandwidth limit; no server-side cap |

v0.1.0 may add per-endpoint limits once §67.4 remote-access mode lands — at that point auth, TLS, and rate limiting all enter together for the internet-facing surface.

### 60.7 Cross-section updates

- **§9** — add forward-reference: "See §60 for shared API conventions (errors, pagination, size limits, idempotency)."
- **§9 endpoint list** — add `/api/v1/server/state` to the Server group.
- **§32.5** — replace "fetches `GET /api/v1/server/state` snapshot" (which referenced an unspec'd endpoint) with explicit reference to §60.4.
- **§40.3, §44.5, §46.8, §47.7, §50.16** — list endpoints inherit cursor pagination from §60.2 without restating.
- **OpenAPI spec (`openapi.yaml`)** — every endpoint documents its error shape via `application/problem+json` responses, declares whether `Idempotency-Key` is required, and references the shared component schemas for the pagination wrapper.

### 60.8 Liveness + readiness endpoints — `/healthz` + `/readyz`

Lightweight HTTP probes for external monitoring (uptime-kuma, Prometheus blackbox, Pingdom, etc.). Distinct from `/api/v1/server/state` (which is the heavy UI-rehydrate snapshot per §60.4) — these are cheap, low-latency, no-auth checks designed for high-frequency polling.

**Path convention.** Both endpoints live at the root, NOT under `/api/v1/` — they exist outside the versioned API surface because external monitors expect well-known names regardless of API version, and we want them callable even during partial startup states when the v1 router may not be fully mounted.

**Authentication.** None, matching §67 (trusted-LAN posture). Both endpoints are safe to expose on any interface the server binds to. v0.1.0 remote-access mode keeps them open on the LAN interface but adds auth on the remote interface.

**`GET /healthz` — liveness:**

- **Purpose:** "is the process alive and able to respond to HTTP?"
- **Response time target:** < 10 ms. No DB queries, no disk I/O, no equipment probing.
- **Logic:** if the HTTP handler runs, return 200. That's it. The fact that the response was produced proves liveness.
- **Response (200 OK):**
  ```
  Content-Type: text/plain
  ok
  ```
- **Cache headers:** `Cache-Control: no-store` so monitors get a fresh result every time.
- **Use case:** monitor's primary "is the server up?" check. Suitable for poll intervals as fast as every 5 s with negligible server cost.

**`GET /readyz` — readiness:**

- **Purpose:** "is the server able to do real work right now?" Checks the dependencies that must be healthy for capture, mount control, etc. to succeed.
- **Response time target:** < 100 ms (one DB ping, one storage stat, one cached AlpacaBridge handshake result — no fresh AlpacaBridge probe per request).
- **Checks performed:**
  | Check | Pass condition | Why it matters |
  |---|---|---|
  | `database` | `SELECT 1` returns within 50 ms | Capture pipeline writes to DB per §28.6; failure = no new frames recorded |
  | `storage` | `/media/openastroara` is mounted + writable (stat + a 1-byte test write to `.araback/.readyz_probe`, cleaned up next probe) | FITS writes per §28.7 fail without writable storage |
  | `alpaca_bridge` | Cached handshake result from §68.1 within last 30 s shows version-acceptable status | Equipment ops fail without a healthy bridge |
  | `pending_restart` | `/var/lib/openastroara/.needs-restart` flag is NOT set (per §34.7) | Server is running on stale binary; user should be aware |
  | `migration_state` | `__EFMigrationsHistory` matches expected current version (per §28.14) | Confirms schema is consistent with running binary |
- **Response (200 OK — all checks pass):**
  ```json
  {
    "status": "ready",
    "version": "0.0.1-ara.5",
    "uptime_s": 14523,
    "checks": {
      "database": "ok",
      "storage": "ok",
      "alpaca_bridge": "ok",
      "pending_restart": "ok",
      "migration_state": "ok"
    }
  }
  ```
- **Response (503 Service Unavailable — any check fails):**
  ```json
  {
    "status": "not_ready",
    "version": "0.0.1-ara.5",
    "uptime_s": 14523,
    "checks": {
      "database": "ok",
      "storage": "ok",
      "alpaca_bridge": {
        "status": "unreachable",
        "detail": "Last successful handshake 412s ago; min version 1.2.0 required"
      },
      "pending_restart": "ok",
      "migration_state": "ok"
    },
    "last_frame_age_s": 41
  }
  ```
  Failing checks include a `detail` string for human-readable troubleshooting. Passing checks collapse to the scalar `"ok"` to keep the response small.
- **HTTP status mapping:** `200` if all checks pass; `503` if any check fails. (`pending_restart` set to non-null reduces severity — see below.)
- **Severity nuance:** `pending_restart` set to true does NOT push readiness to 503 by default — the server is still doing real work, just on a stale binary. Reports `pending_restart: { status: "pending", new_version: "..." }` but `status` stays `"ready"`. Operators wanting strict readiness can interpret the nested status.

**Cache headers:** `Cache-Control: no-store` on both endpoints; `readyz` results are not cached server-side either (each request re-runs the checks against current state).

**Logging:** both endpoints log at Trace level (lower than Debug) to avoid drowning the log file when polled every 5 s. logrotate handles any residual volume per §29.9.

**Implementation surface:**

- ~30 LOC C# for the two handlers in `OpenAstroAra.Server/Endpoints/Health.cs`
- Storage write probe uses the same path as §28.7's atomic-write pipeline but with a single tiny file under `.araback/` so it doesn't pollute the FITS tree
- AlpacaBridge handshake cache is shared with §68.1 — `/readyz` reads the cache; the handshake itself runs on connect + periodically every 30 s

**§14.1 integration test cases (added):**

- `healthz_responds_200_ok_during_normal_operation`
- `healthz_responds_during_database_unavailable` — kill DB connection mid-test; `/healthz` still 200 (it doesn't check DB)
- `readyz_returns_503_when_alpaca_bridge_unreachable`
- `readyz_returns_503_when_storage_unwritable` — chmod 000 storage dir; `/readyz` returns 503 with `storage` failing
- `readyz_returns_200_with_pending_restart_status` — set `.needs-restart` flag; `/readyz` returns 200 but `pending_restart: pending`

**§61 search registry entry** (added in 12h Settings sub-PR — these are operator-facing endpoints surfaced in the in-app help):

- `monitoring.health_endpoints` — keywords: `health check, healthz, readyz, uptime monitor, prometheus, monitoring, uptime kuma, pingdom, status endpoint`

**DEPLOY.md addition** (in §34.6's content list, item 8):

8. Monitoring sidebar — for users running uptime monitors or observatory dashboards:
   - `GET http://<pi>:5555/healthz` — liveness, poll every 30–60 s
   - `GET http://<pi>:5555/readyz` — readiness with check breakdown, poll every 5–10 min
   - Both are unauthenticated per §67 trusted-LAN posture
   - Example Prometheus blackbox config snippet (one-liner)

### 60.7.1 CORS policy (allow any origin in v0.0.1)

`OpenAstroAra.Server` applies the most permissive CORS policy in v0.0.1:

```csharp
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .AllowAnyOrigin()
    .AllowAnyMethod()
    .AllowAnyHeader()));
// ...
app.UseCors();
```

This matches §67's trusted-LAN posture (no auth, all endpoints open). Cross-origin requests from any browser tool work freely:
- WILMA desktop client (in-process Flutter, same-origin — works trivially)
- Flutter web debug from `localhost:<dev-port>` during development
- Swagger UI rendered by Scalar (§49) — works from any origin including Mac dev machines hitting a Pi
- External monitoring dashboards (Grafana, Uptime Kuma) calling `/healthz` + `/readyz` (§60.8) from their own UI origins
- curl / Postman / direct REST clients (no preflight needed; no CORS impact)

**Why not tighter:**

CORS protects against browser-mediated CSRF when a malicious site can trick the browser into making authenticated requests to a target with the user's credentials. v0.0.1 has no auth (§67) — there are no credentials to steal and no CSRF surface to protect. Tightening CORS in this environment just makes legitimate cross-origin tooling harder without adding any real security.

**v0.1.0 remote-access mode (§67.4):**

When auth + TLS enter via the v0.1.0 remote-access interface, CORS tightens on that interface only:
- Remote interface: `AllowedOrigins` list configured by user (typically the URLs of dashboards / management consoles they trust)
- LAN interface: keeps `AllowAnyOrigin` (trusted LAN posture unchanged)

The split mirrors the auth split — same physical server binary, different policies per network interface.

**Implementation note (AOT-friendly):**

ASP.NET Core's `AddCors` middleware is fully AOT-compatible per §71. No reflection involved.

### 60.8.1 API versioning policy (v1 forever-additive; v2 only for breaking changes)

`/api/v1/` is the v0.0.1 API surface and stays additive-only forever. Breaking changes ship under `/api/v2/`; both versions coexist in the same binary for at least one release cycle (~1 year) after v2 GA, with explicit deprecation headers + CHANGELOG-documented sunset dates ≥ 6 months out.

**Additive changes (stay in v1):**

- New endpoints (`POST /api/v1/sequences/new-feature`)
- New optional fields in response bodies (clients that don't know about the field ignore it)
- New optional query parameters with safe defaults
- New optional request body fields with server-side defaults
- New WebSocket event types (clients that don't recognize them ignore per §60.9)
- New severity levels in existing fields where the field is documented as extensible
- New enum values in response fields documented as extensible

**Breaking changes (require v2):**

- Removing an endpoint entirely
- Removing a field from a response body
- Changing a field's type (e.g., `int` → `string`, scalar → array)
- Changing a field's semantics (e.g., a "duration" field switches from seconds → milliseconds)
- Making an optional field required in requests
- Renaming an endpoint (also `v1/old` 308-redirects to `v1/old-renamed` only if the change is truly cosmetic — otherwise it's v2)
- Changing authentication requirements on an endpoint (v0.1.0 only; v0.0.1 has no auth per §67)
- Restructuring nested objects (e.g., flattening or deepening)
- Changing pagination behavior (cursor format, page-size meaning)
- Changing error code semantics for existing endpoints

**Coexistence model:**

When v2 ships, the server's route table maps both:

```
GET /api/v1/profiles/{id}  → ProfilesV1Endpoint.Get(id)     // legacy shape preserved
GET /api/v2/profiles/{id}  → ProfilesV2Endpoint.Get(id)     // new shape
```

Both endpoints back onto the same underlying service layer; the version difference is purely the DTO + serialization boundary. Adapters translate the underlying domain model to the appropriate version shape — no duplicated business logic.

OpenAPI spec (§71.3) generates `openapi.yaml` covering both — clients can choose which version to bind against (Dart client generation can produce v1 or v2 stubs).

**Deprecation lifecycle:**

When a v1 endpoint is targeted for removal:

1. **GA + 0 months:** v2 ships; both endpoints respond identically (modulo the breaking change). CHANGELOG.md announces the deprecation; documents the v2 migration path.
2. **GA + 1 month:** v1 endpoint responses include HTTP headers:
   ```
   Deprecation: true
   Sunset: 2027-05-23T00:00:00Z
   Link: <https://openastro.net/docs/migrating-from-v1>; rel="deprecation"
   ```
3. **GA + 3 months:** server emits `api.deprecation_used` WS event (severity: info) every time a deprecated v1 endpoint is called, including endpoint path + client User-Agent. Logged for the maintainer to know whether anyone still uses it.
4. **GA + 6 months minimum:** v1 endpoint removal candidate. Final go/no-go based on telemetry from step 3 — if active users remain, slip the sunset date + announce.
5. **Removal:** v1 endpoint returns `410 Gone` with `application/problem+json` body referencing the v2 endpoint. Removed from server route table at the next major server release.

**API minimum-supported-version (MSV):**

Client + server negotiate compatible API versions via `/api/v1/server/state`'s `api_versions` field (added in this section). Server reports:

```json
{
  "api_versions": {
    "supported": ["v1"],
    "deprecated": [],
    "removed": [],
    "recommended": "v1"
  }
}
```

When v2 ships:

```json
{
  "api_versions": {
    "supported": ["v1", "v2"],
    "deprecated": ["v1"],
    "removed": [],
    "recommended": "v2"
  }
}
```

WILMA picks `recommended` unless the user-installed WILMA version doesn't support it (then falls back to highest mutually-supported). Surfaces "Your WILMA is using an older API version" notification (severity: info) per §46 when on a deprecated version, with [Update WILMA] action.

**Cross-references:**

- §33.7 — CHANGELOG.md is the canonical announcement surface for deprecation timelines
- §49 — Swagger UI shows both v1 + v2 specs side-by-side once v2 ships
- §60.9 — WebSocket protocol versioning is independent (X-Ara-WS-Version header)
- §71.3 — OpenAPI spec generated for both versions from endpoint metadata
- §55 — v0.1.0+ roadmap entry: "First v2 API surface" added when known breaking changes accumulate

### 60.9 WebSocket wire protocol

§60.1–§60.8 govern the REST surface. WebSocket has its own conventions, pinned here so client + server stay consistent.

**Endpoint:** `ws://<host>:5555/api/v1/ws` (no auth in v0.0.1 per §67; v0.1.0 remote-access mode adds `wss://` + token via `Authorization` header on the upgrade request).

**Version negotiation:** the upgrade request includes header `X-Ara-WS-Version: 1`. Server responds:
- `101 Switching Protocols` on success
- `426 Upgrade Required` if the version is unsupported (with a `Sec-WebSocket-Version` body listing supported versions)
- `400 Bad Request` if the header is malformed

URL versioning (`/api/v1/ws`) and the header are belt-and-suspenders — different deprecation models. URL version changes for breaking event-shape changes; header version changes for breaking framing/heartbeat/compression conventions. Both rarely change.

**Frame size:** 1 MB max. Higher than the REST 64 KB cap (§60.3) because WS events can include batched notification groups + WS-resume replay bursts. Single events stay small (typical JSON event: 200 bytes – 4 KB; only stress cases approach the cap).

**Compression:** `permessage-deflate` extension enabled by default. Saves ~70% on JSON-heavy event traffic (which is most of it); negligible cost on already-compressed binary payloads (rare in WS — previews go over HTTP per §65). Clients can disable via the standard `Sec-WebSocket-Extensions: ` header omission; server falls back to uncompressed transparently.

**Heartbeat:** server sends a WS ping every 30 s. Client must respond with pong within 60 s of the ping. If 2 consecutive pings go unanswered, server closes the connection with code 1011 (`server_initiated_disconnect_unresponsive_client`). Client should likewise close + trigger §32 reconnect modal if it sees no server activity (ping OR event) for 90 s.

**Resume protocol:** after connect-upgrade succeeds, the client may send its FIRST message as a JSON resume request:

```json
{ "resume_token": "opaque-string-from-prior-ws_resume_token" }
```

Server responds with one of:
- `{ "resumed": true, "missed_events": <count>, "last_event_id": "<id>" }` then immediately replays the missed events as normal events
- `{ "resumed": false, "code": "resume_token_expired", "reason": "..." }` then continues as a fresh subscription (no replay)
- `{ "resumed": false, "code": "resume_token_invalid" }` if token is unparseable; client should clear and reconnect fresh

If the client's first message is anything OTHER than a resume request (e.g., immediately starts sending its own commands), server treats the connection as fresh (no replay). Resume is opt-in by sending the request as message #1.

Resume tokens are issued by REST (`GET /api/v1/server/state` per §60.4) and have a 1-hour validity window. After that window, `resume_token_expired` and the client falls back to a fresh `/api/v1/server/state` rehydrate.

**Event envelope:** every server-sent message follows:

```json
{
  "type": "frame.complete",
  "ts": "2026-05-23T19:14:33.123Z",
  "id": "evt-7f3a9b",
  "payload": { /* event-specific */ }
}
```

`id` is a monotonically increasing opaque ID per server boot; used internally for resume bookkeeping (the server tracks last-acked-by-each-client). Clients echo no IDs; resume tokens encode the bookmark server-side.

**Close codes:**

| Code | Direction | Meaning |
|---|---|---|
| 1000 | both | Normal closure (client logout, server shutdown) |
| 1001 | both | Going away (network change, app backgrounded) |
| 1011 | server → client | Server-side error (unrecoverable; client should reconnect after backoff) |
| 1012 | server → client | Service restart imminent (pairs with `server.restart_imminent` event from §34.7); client should reconnect after the configured delay |
| 4000 | reserved | (placeholder, unused in v0.0.1) |
| 4001 | server → client | (v0.1.0 only) Auth required / token invalid for remote-access mode (§67.4) |
| 4002 | server → client | Resume token expired (client clears + reconnects with REST snapshot) |
| 4003 | server → client | WS protocol version mismatch (client must downgrade or upgrade) |
| 4004 | server → client | Single-client policy: another WILMA took over (§27) |

Codes 4000-4099 reserved for ARA-specific close reasons; 4100+ available for future v0.1.0 additions (per-channel close, per-subscription close, etc.).

**Backpressure** (per §60.6 + §66.4): if a client's WS send buffer exceeds 256 messages (per-client bound from §66.2), server closes the connection with code 1011 + reason `client_too_slow`. Client reconnect uses the resume protocol to catch up.

**§14.1 server integration test cases (added):**

- `ws_upgrade_with_version_1_accepted`
- `ws_upgrade_with_version_2_returns_426`
- `ws_heartbeat_pings_every_30s_under_load`
- `ws_unresponsive_client_closes_with_1011_after_60s`
- `ws_resume_with_valid_token_replays_missed_events`
- `ws_resume_with_expired_token_returns_4002_close`
- `ws_oversize_frame_above_1MB_rejects_with_1009`
- `ws_compression_negotiation_works_with_permessage_deflate`

**§14.2 widget test cases (added):**

- `wilma_reconnects_after_ws_close_1012_with_configured_delay`
- `wilma_falls_back_to_rest_snapshot_after_resume_token_expired`
- `wilma_handles_4004_single_client_takeover_by_showing_session_transferred_modal`

**§61 search registry entries:**

- `monitoring.ws_protocol` — keywords: `websocket version, ws protocol, ws debugging, ws heartbeat, ws resume`

**Cross-references:**

- §32 — reconnect modal triggered by close codes 1001 / 1011 / 1012 / 1009
- §34.7 — code 1012 pairs with `server.restart_imminent` event
- §60.4 — `ws_resume_token` issued by `/api/v1/server/state`
- §66.4 — backpressure → code 1011 reason `client_too_slow`
- §67 — no auth in v0.0.1; 4001 reserved for v0.1.0 remote-access mode
- §27 — code 4004 single-client takeover

---

## 61. Smart settings search

Addresses NINA's single biggest UX weakness: settings are scattered across a deep tree and users cannot find them. ARA commits to making every setting **discoverable in under 10 seconds without prior knowledge of where it lives** (per §0.5 design principle 3).

### 61.1 Two search locations

| Location | Trigger | Persistent? |
|---|---|---|
| **WILMA top bar** (right side, magnifying-glass icon) | Click icon, or keyboard shortcut `⌘K` (macOS) / `Ctrl+K` (Win/Linux) | Always one keystroke away from any screen |
| **Top of the Settings/Options tab** (default focus when entering Settings) | Auto-focused on tab open | Lives within Settings only |

Both surface the same search behavior. Mobile companion (§41) shows only the top-bar location (Settings tab on mobile is read-mostly; full editing is desktop-class).

### 61.2 Results dropdown

User types `dither` → live-filtered results dropdown:

```
┌──────────────────────────────────────────────────────┐
│  🔍  dither                                           │
│  ──────────────────────────────────────────────────  │
│  Dither pixels                                        │
│  Settings → Guider → PHD2                             │
│  Currently: 5 px              [edit inline ▼]         │
│                                                       │
│  Dither settle threshold                              │
│  Settings → Guider → PHD2                             │
│  Currently: 1.5 px for 10 s   [edit inline ▼]         │
│                                                       │
│  Dither timeout action                                │
│  Settings → Safety → Faults                           │
│  Currently: Continue (warn)   [edit inline ▼]         │
│                                                       │
│  Auto-flat dither cadence                             │
│  Settings → Calibration                               │
│  (related — matches "dither")                         │
│                                                       │
│  ─────                                                │
│  Or jump to: Settings → Guider                        │
└──────────────────────────────────────────────────────┘
```

Three actions per result:

1. **Inline edit** — for booleans, sliders, dropdowns, short text fields, integer/range. Toggle or adjust without leaving the dropdown. Saves immediately on commit (debounced 500 ms for sliders).
2. **Jump to setting** — navigates to the parent panel with the setting highlighted (yellow border, 2-second fade-out).
3. **Show description** — hover/tap reveals the full description plus "users searching for X also adjust Y" cross-links.

### 61.3 Settings registry — the implementation foundation

Every setting in WILMA is registered with structured metadata. The registry is a Dart compile-time constant catalog at `client/openastroara_client/lib/settings/registry.dart`.

```dart
Setting(
  id: 'guider.dither_pixels',
  label: 'Dither pixels',
  description:
    'How many pixels PHD2 dithers between exposures. Larger values '
    'randomize hot-pixel positions more aggressively; smaller values '
    'settle faster.',
  keywords: ['dither', 'guide', 'guider', 'phd2', 'randomize', 'hot pixel'],
  path: ['Settings', 'Guider', 'PHD2'],
  type: SettingType.intRange(min: 0, max: 50),
  defaultValue: 5,
  profilePath: 'guider.dither_pixels',          // dotted path in profile JSON
  relatedSettings: [
    'guider.dither_settle_threshold',
    'guider.dither_timeout_action',
  ],
)
```

Serves three purposes:
1. **Search index** — fuzzy-matched against `label` + `description` + `keywords`
2. **Inline edit rendering** — `type` tells WILMA which control to draw
3. **Cross-linking** — `relatedSettings` powers "users searching for X also adjust Y" hints in §61.2

### 61.4 PR review rule

A setting that isn't registered in `registry.dart` doesn't merge. Mechanically enforced — not relying on reviewer vigilance.

Four-layer enforcement spec lives in [`design/COMMIT-PR-RULES.md` → "Settings-registry gate"](COMMIT-PR-RULES.md):

1. Local pre-commit hook (`check-settings-registry.mjs --staged`) — blocks the commit
2. CI check on every PR — blocks merge
3. PR template mandatory checkbox — manual confirmation
4. CodeRabbit review focus on `lib/screens/settings/**` and `lib/wizard/**` diffs

The gate activates at **Phase 12 of the port** (when Settings UI begins). Phase 12 sub-PR 12h is the natural home for the registry's initial bulk-population. After 12h, the gate is the steady-state enforcement for all subsequent work and all community contributions.

There is intentionally no opt-out. Discoverability is a first-class requirement per §0.5, and we enforce it mechanically because reviewer vigilance is unreliable over a project lifetime.

### 61.5 Search algorithm — fuzzy + keyword-weighted

- Dart `fuzzy` package (or similar) for typo tolerance ("dithr" finds "dither")
- Keyword matches score higher than label substring matches
- Multi-word queries AND-combine ("guide settle" finds settings with both terms)
- Common synonyms baked in:
  - "scope" → telescope
  - "filter wheel" / "fw" → EFW
  - "AF" → autofocus
  - "PA" → polar alignment
  - "cam" → camera
- Recent searches saved per WILMA device (top of dropdown when search is empty)
- Empty search shows: recent searches + "Common settings" curated list (top 8 most-edited settings across all users — could be a hand-curated list or telemetry-derived; v0.0.1 hand-curated since no telemetry)

### 61.6 Coverage scope

The registry covers, at minimum:

- All profile settings (~80-100 entries when fully built out)
- All app-level preferences (theme, language, units, etc., ~20 entries)
- All safety policy fields (§35)
- All notification preferences (§46.6)
- All survey-manager controls (§36.2)
- All calibration settings (§48.7)
- All meridian flip + mount safety settings (§57 + §58)
- Storage / backup / network settings
- Display / accessibility settings (§53)

Roughly **200-250 searchable entries** total across the whole app. Real but bounded.

### 61.7 Inline edit — types and rendering

| `SettingType` | Inline control |
|---|---|
| `bool` | Toggle switch |
| `intRange(min, max)` | Slider with numeric input |
| `doubleRange(min, max, step)` | Slider with numeric input |
| `enum(values)` | Dropdown |
| `string` | Single-line text input |
| `duration` | Number + unit dropdown (s/min/hr) |
| `color` | Color swatch picker |
| `keyboardShortcut` | Capture-key input |
| `path` | File picker dialog (jump-only; not inline) |
| `complex` | Jump-only (e.g., horizon profile editor, polygons editor) |

For `jump-only` types, the inline-edit affordance is replaced by the [Jump to setting] button only.

### 61.8 No server endpoint required

The registry is purely client-side Dart code. WILMA already fetches setting *values* via existing endpoints (`GET /api/v1/profiles/{id}` for profile settings; WILMA-local preferences via `flutter_secure_storage` / `shared_preferences`). The search adds no new server endpoints.

This keeps the feature lightweight and means the search works even when the server is briefly disconnected (last-known values served from cache).

### 61.9 Mobile companion behavior

On phone/tablet companion mode (§41), the search bar exists but inline-edit is restricted to settings that make sense on mobile (notifications, alarm sound, theme, server connection). For anything else — sequence settings, equipment config, calibration — the result row shows:

```
Dither pixels
Sequence → Guider settings — desktop only
[Open on desktop]
```

Tapping [Open on desktop] copies an `araapp://settings/guider/dither_pixels` deep-link to clipboard (per §41.2 desktop-redirect pattern), which the desktop WILMA picks up.

### 61.10 v0.1.0 expansion — general command palette

Same `⌘K` / `Ctrl+K` shortcut, registry expands to include:

- **Actions** — "park mount", "run autofocus on L", "start sequence M42 LRGB"
- **Targets** — "M42" jumps to that target in Image Library or Framing Assistant
- **Sessions** — "last week" jumps to recent sessions in the library
- **Help** — "how do I import a NINA sequence" surfaces relevant docs / wizard re-entry
- **Equipment** — "camera temp" jumps to live equipment panel with that reading highlighted

VS Code's `Cmd+Shift+P` pattern applied to astrophotography. Builds on §61's foundation — the registry just gains new entry kinds (`ActionEntry`, `TargetEntry`, `SessionEntry`, etc.) with the same search/dropdown/inline-action UX.

Committed v0.1.0 per §55.1.

### 61.11 Cross-section updates this enables

- **§25 UI sections** — every settings-bearing section is re-evaluated through the "is this discoverable via search?" lens
- **§37 wizard** — wizard remains the friendly setup flow; search is the friendly *re-find* flow afterwards
- **§55.1 roadmap** — general command palette explicitly added as a v0.1.0 commitment

---

## 62. Dither policy

PHD2 handles the actual mount nudging; ARA decides when/how/whether. Three "better than NINA" wins built in: auto-magnitude from pixel scale, auto-disable for short-exposure workflows, diagnostic-aware skip during bad conditions.

### 62.1 What dithering does (60-second context)

Between exposures, the mount nudges framing by a few pixels in random directions. Hot pixels and sensor artifacts then don't land on the same pixels in every frame; when frames stack, those artifacts average out. Without dither, hot pixels persist and you get walking-noise patterns in the integrated image.

The actual nudge happens via PHD2 — ARA sends PHD2 a `dither` JSON-RPC command, PHD2 commands the mount, PHD2 waits for guiding to re-settle, PHD2 reports back. Settle wait is typically 5-15 seconds depending on mount + atmosphere.

### 62.2 Default cadence — auto, based on exposure length

| Exposure length | Default cadence |
|---|---|
| ≥ 60 s (typical DSO) | Every frame |
| < 60 s (e.g., bright-target short subs, comet bursts) | Dither disabled |
| Calibration frames (flats/darks/bias) | Never |

Auto-determined from the sequence's exposure length per instruction. User override available per-instruction in the sequencer + per-profile as a default.

### 62.3 Default magnitude — auto-computed from pixel scale

NINA asks for "dither pixels" (typical answer: 5). The right answer depends on the rig — cameras with small pixels need more pixels to cover the same angular displacement.

```
target_angular_displacement = 5 arcsec    # rule of thumb for hot-pixel mitigation
pixel_scale = (camera_pixel_size_microns × 206.265) / telescope_focal_length_mm
dither_pixels = round(target_angular_displacement / pixel_scale)
clamp to [3, 15]
```

Examples:
- ZWO ASI2600MM (3.76 μm) on 540 mm refractor → 1.43"/px → ~3.5 px dither
- ZWO ASI2600MC (3.76 μm) on 2000 mm SCT (C8 + 0.7 reducer) → 0.39"/px → ~13 px dither
- ZWO ASI120MM (3.75 μm) on 1480 mm SCT (C8 native) → 0.52"/px → ~10 px dither

Wizard shows: *"Dither magnitude: auto (~4 px for current rig)"*. Override available via slider in advanced disclosure.

### 62.4 Default direction + pattern

- **RA+Dec random** (default) — most thorough hot-pixel coverage
- **RA-only** — available via advanced disclosure for users on mounts with unreliable Dec axis (rare)
- **Spiral** — alternate pattern; useful for cameras with very localized fixed-pattern noise

### 62.5 Settle parameters

PHD2's settle behavior:

- **Settle pixels:** 1.5 px (PHD2's RMS error must drop below this)
- **Settle time:** 10 seconds (RMS must stay below threshold for this long)
- **Settle timeout:** 60 seconds (give up + continue with warning)

User can tighten/loosen in advanced. Most users never touch these.

### 62.6 Cross-meridian-flip behavior

When the §58 meridian flip's `guider_recal: auto_restore` path runs (PHD2's "Auto Restore Calibration" with reverse-Dec flag), dither direction handling is automatic — PHD2 knows the axes flipped and signs the dither commands correctly. Same path NINA uses.

If `guider_recal: full` (full re-calibration after flip), dither resumes after the new calibration completes. Equally automatic.

### 62.7 No-guider fallback (v0.0.1 = disabled)

If the profile has no guider configured OR PHD2 is connected but unresponsive: dither is disabled with a one-time per-session notification:

> *"Dither requires a guider — your sequence will continue without dithering. Hot pixels may persist in stacked images."*

Direct-mount-pulse dither (without guider) is technically possible but only useful for short-exposure workflows where dither matters less anyway. Deferred to v0.1.0.

### 62.8 Diagnostic-aware skip (the §59.9 pattern repeated)

Every dither command consults `GET /api/v1/diagnostics/current` before firing. Non-nominal states (`clouds_passing`, `aperture_blocked`, `dew_formation`) skip the dither — no point spending 10-15 s settling when the next exposure is just going to be paused anyway.

When diagnostic state recovers and exposures resume, the next dither cadence cycle picks up normally. Skipped dithers don't accumulate.

NINA dithers regardless of conditions; this is sky-time savings per §0.5 pillar 1.

### 62.9 Profile schema

```json
{
  "dither": {
    "enabled": true,
    "cadence": "auto",                  // "auto" | "every_frame" | "every_n_frames" | "disabled"
    "cadence_n_frames": 1,              // only used when cadence = "every_n_frames"
    "magnitude": "auto",                // "auto" | numeric pixel count
    "magnitude_target_arcsec": 5,       // when auto: target angular displacement
    "direction": "ra_dec_random",       // "ra_dec_random" | "ra_only" | "spiral"
    "settle": {
      "pixels": 1.5,
      "time_seconds": 10,
      "timeout_seconds": 60
    },
    "diagnostic_skip_when_unstable": true,
    "skip_for_short_exposures_threshold_sec": 60
  }
}
```

**Four user-visible settings** (enabled / cadence / magnitude / direction). Settle + diagnostic-skip + threshold are in the advanced disclosure.

### 62.10 Per-target-type overrides

Sequence instructions can override the profile defaults:

- DSO target (default): inherit profile (`every_frame`, auto-magnitude, RA+Dec)
- Short-exposure DSO bursts or comet cadence templates that opt out: `enabled: false`
- Calibration frames (always): `enabled: false`

A user with a DSO sequence followed by a quick lunar capture doesn't have to manually toggle dither off — the lunar instruction's template handles it.

### 62.11 Wizard exposure — minimal

Wizard's PHD2 / guider screen (§37.3 screen 10) updated to:

```
Dithering between exposures:

  Mode:           [▼ Auto (recommended)         ]
                     Auto (recommended)
                     Every frame
                     Every 3 frames
                     Every 5 frames
                     Disabled

  Magnitude:      [▼ Auto — ~4 px for your rig  ]

  Dithering nudges the mount slightly between exposures so hot
  pixels don't persist in stacked images. Auto mode picks the
  right cadence and magnitude based on your camera and scope.

  [< Back]    [Skip — use defaults]    [Next >]
```

Two questions. Both default to "auto." Most users tap [Next].

### 62.12 Failure handling

| Failure | Action |
|---|---|
| PHD2 settle timeout exceeded | Log warning, continue with next exposure (don't abort — slight star elongation in next frame is recoverable in processing) |
| PHD2 unresponsive to dither command | Per §42.2 retry, fall through to "no guider" path if persistent |
| Three consecutive dither timeouts in a session | Notify *"Guiding is unstable — dithering is repeatedly timing out. Check mount balance, cable drag, or wind."* + temporarily reduce cadence to every 3 frames automatically |

The "auto-reduce cadence on persistent failure" is an ARA-native graceful-degradation pattern.

### 62.13 API endpoints + WebSocket events

| Method | Path | Purpose |
|---|---|---|
| `POST` | `/api/v1/dither/trigger` | Manual dither (e.g., user observed walking noise); body: optional magnitude override |
| `GET` | `/api/v1/dither/state` | Current dither state (idle / dithering / settling); last dither timestamp + magnitude |

WebSocket events:

```json
{ "type": "dither.started",       "payload": { "magnitude_pixels": 4, "direction": "ra_dec_random" } }
{ "type": "dither.settling",      "payload": { "current_rms_pixels": 2.3, "target": 1.5 } }
{ "type": "dither.completed",     "payload": { "duration_seconds": 12, "final_rms_pixels": 0.9 } }
{ "type": "dither.skipped",       "payload": { "reason": "diagnostic_unstable" | "short_exposure" | "no_guider" } }
{ "type": "dither.timeout",       "payload": { "elapsed_seconds": 60, "final_rms_pixels": 2.1, "continued_anyway": true } }
```

### 62.14 §61 settings registry coverage

- `dither.enabled` — keywords: `dither, dithering, hot pixel, walking noise, sensor noise`
- `dither.cadence` — keywords: `dither frequency, dither every, dither interval, how often dither`
- `dither.magnitude` — keywords: `dither pixels, dither size, dither amount, dither distance`
- `dither.direction` — keywords: `dither direction, dither pattern, dither RA dec`
- `dither.settle.*` — keywords: `dither settle, settle time, settle threshold, PHD2 settle`

### 62.15 What's NINA-preserved vs ARA-changed

**Preserved verbatim:** PHD2 JSON-RPC dither command + settle protocol, settle threshold math, dither pattern algorithms (random/spiral).

**ARA additions:**
- Auto-compute magnitude from camera + telescope (vs fixed user-input)
- Auto-disable for short-exposure workflows (vs always-on)
- Per-target-type defaults via sequence templates
- Diagnostic-aware skip during bad conditions
- Auto-reduce cadence on persistent timeout failures
- Wizard reduced to 2 dither questions
- §30.7 equipment-change check invalidates dither magnitude when camera or telescope changes

### 62.16 v0.1.0 expansion paths

- **Direct-mount-pulse dither** for no-guider workflows
- **Adaptive magnitude** — if server detects walking-noise patterns persisting in stacked frames, auto-increase dither magnitude
- **Per-filter cadence override** — narrowband dithered more aggressively than broadband
- **Dither-quality scoring** — per-dither analytics surfaced in §50 Stats Guiding view (e.g., "your dithers settle in 8s avg, but the last 5 took 22s — guiding may be degrading")

---

## 63. PHD2 lifecycle + profile/dark-library push

ARA's guider integration depends on **openastro-phd2**, a separately-maintained Linux/Pi-friendly fork of PHD2 with the systemd headless lifecycle, JSON-RPC API surface, and ASCOM Alpaca + INDI transports already designed. This section specs how ARA Core integrates with it — process supervision, profile management, dark library, equipment-change handling. The implementation pattern is *"ARA assumes openastro-phd2 is present and uses its documented RPC surface"* — ARA does not modify PHD2.

**Source of truth for the RPC surface:** [`openastro-phd2/doc/jsonrpc_api.md`](https://github.com/open-astro/openastro-phd2/blob/master/doc/jsonrpc_api.md) — 80+ documented methods. Pin to whatever version ships with the ARA Core .deb's Recommends (`openastro-phd2`).

### 63.1 Lifecycle — managed by openastro-phd2's own systemd unit

Installed by the `openastro-phd2` .deb (per §34.2 Recommends). ARA Core does **not** ship a competing systemd unit.

Existing service (from the .deb):

```ini
[Unit]
Description=OpenAstro PHD2 Headless Guiding Server
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=openastro-phd2
Group=openastro-phd2
WorkingDirectory=/var/lib/openastro-phd2
Environment=LD_LIBRARY_PATH=/usr/lib/openastro-phd2
ExecStart=/usr/bin/xvfb-run -a /usr/bin/openastro-phd2 --headless --headless-auto-connect
Restart=on-failure
RestartSec=3

[Install]
WantedBy=multi-user.target
```

Notes for ARA:
- **`xvfb-run` is required** — PHD2 uses wxWidgets which needs a display context even headless. xvfb-run provides a virtual X server. Not optional, not removable.
- **`--headless --headless-auto-connect`** flags are PHD2 fork additions that make remote-RPC operation practical
- Service runs as user `openastro-phd2` (separate from `openastroara` — they're sibling services)
- PHD2's own logs + profiles + calibration data live under `/var/lib/openastro-phd2/`
- `RestartSec=3` is more aggressive than ARA's own server (5s) — appropriate because PHD2 should recover quickly to keep guiding available

ARA's Pi-side integration with the service:

- **Connection target:** `localhost:4400` (PHD2 default JSON-RPC port)
- **Restart authority:** ARA can request restart via `systemctl restart openastro-phd2` (privileged via NOPASSWD sudoers drop-in, similar to §33.5 update.sh pattern)
- **Stop authority:** ARA can request stop via `systemctl stop openastro-phd2` (rare; mostly used by tests)
- **Status observation:** ARA polls `systemctl is-active openastro-phd2` for service-level health alongside the JSON-RPC ping

### 63.2 Connection lifecycle (ARA's PHD2 client state machine)

ARA's `IGuider` client follows this state machine:

```
not_attempted
   ↓ first guiding-relevant operation
connecting (TCP connect to localhost:4400)
   ↓ TCP ack
connected_idle (RPC reachable, no equipment yet)
   ↓ ensure correct PHD2 profile + connect_equipment via set_connected(true)
connected_ready (camera + mount in PHD2, ready to guide)
   ↓ guide (start_guiding RPC)
calibrating
   ↓ cal complete
guiding
   ↓ sequencer-controlled
paused | settling-after-dither | star_lost | etc.
```

Surfaced via `/api/v1/guider/status` endpoint + `equipment.state` WebSocket events.

### 63.3 Crash detection + auto-restart layered on top of systemd

systemd's `Restart=on-failure` handles basic crash recovery. ARA layers additional monitoring:

- ARA's PHD2 client polls `get_app_state` every 10 s when idle, every 2 s during guiding
- 3 consecutive RPC failures → ARA classifies PHD2 as **down**
- ARA queries `systemctl status openastro-phd2`:
  - If `activating` (systemd restarting) → wait with backoff (1s → 5s → 15s → 30s → 60s → 120s)
  - If `failed` (systemd gave up) → ARA fires **urgent** notification, guider-dependent ops disabled
  - If `active` but RPC unresponsive → ARA classifies as **hung**, issues `systemctl restart openastro-phd2`
- Mid-guiding crash → §42.2 fault flow: pause sequence at safe point, critical notification, systemd auto-restarts

### 63.4 Per-ARA-profile to PHD2-profile mapping

Each ARA profile maps 1:1 to a PHD2 profile by name. Mapping is `ara-<short-slug>`:

```
ARA profile "C14 on CEM120"     ↔ PHD2 profile "ara-c14-cem120"
ARA profile "RedCat on HEQ5"    ↔ PHD2 profile "ara-redcat-heq5"
ARA profile "Field rig AM5"     ↔ PHD2 profile "ara-field-am5"
```

**Profile lifecycle synced via RPC:**

| ARA event | PHD2 RPC sequence |
|---|---|
| User creates an ARA profile (§30.4) and reaches the guider wizard screen | `create_profile(name="ara-...", select=true)` |
| User loads an existing ARA profile (§30.2) | `set_profile_by_name(name="ara-...")` followed by validation of pushed params |
| User updates ARA profile equipment in §30.7 | `set_connected(false)` → `set_selected_camera/mount/...` → `set_connected(true)` |
| User deletes an ARA profile | `delete_profile(name="ara-...", delete_dark_files=true)` |
| User clones an ARA profile | `clone_profile(source="ara-...", new_name="ara-...-copy", select=true)` |

PHD2 preserves per-profile calibration + dark library data, so switching ARA profiles never loses guider state for either rig.

### 63.5 Profile parameters ARA pushes to PHD2

Captured by §37.3 Screen 10 wizard, pushed via `set_profile_setup` (which accepts any subset of fields):

| ARA-side input | PHD2 RPC field |
|---|---|
| Guide camera (Alpaca discovery from `discover_alpaca_servers` + `query_alpaca_devices`, or INDI camera name) | `set_selected_camera` + `set_selected_camera_id` (for Alpaca-with-multiple-devices) OR `set_selected_indi_camera_driver` |
| Guide camera pixel size | `get_alpaca_camera_pixelsize` auto-fetched, override via `set_profile_setup` if user manually entered |
| Guide scope focal length | `set_profile_setup({focal_length: ...})` |
| Mount (paired to ARA's mount selection) | `set_selected_mount` (Alpaca path uses `set_alpaca_server` + `set_selected_alpaca_device`) |
| Calibration step size | Auto-computed from FL/pixel scale; pushed via `set_profile_setup({calibration_step_ms: ...})` |
| RA aggressiveness | `set_algo_param(axis="ra", name="aggressiveness", value=0.75)` |
| Dec aggressiveness | `set_algo_param(axis="dec", name="aggressiveness", value=0.65)` |
| Min motion (RA + Dec) | `set_algo_param` per axis |
| Dec guide mode (auto / always-positive / always-negative) | `set_dec_guide_mode` |

**Important precondition:** PHD2 rejects `set_selected_*` and `set_profile_setup` while equipment is connected. ARA must:

```
1. set_connected(false)      # disconnect equipment
2. set_selected_camera(...)  # push new selections
   set_selected_mount(...)
   set_alpaca_server(...)
   set_profile_setup({...})  # push remaining params
3. set_connected(true)       # reconnect with new config
4. poll get_connected until true (with backoff + timeout)
```

ARA wraps this as a single atomic "update profile" operation; intermediate state isn't user-visible.

### 63.6 Dark library + defect map management

openastro-phd2 distinguishes two calibration-file types:

- **Dark library** — multi-exposure stack used by PHD2 during normal operation for noise subtraction
- **Defect map (hot-pixel map)** — per-pixel mask of consistently-bad pixels; PHD2 ignores those pixels during star detection

ARA exposes both, with dark library as primary (defect map is advanced):

**Build flow (initiated from wizard's Screen 10 or Settings → Guider → Build Dark Library):**

1. User taps [Build dark library now (~2 min)]
2. ARA prompts via modal: *"Cover the guide scope with a dark cap. Tap Continue when ready."*
3. ARA issues `build_dark_library({frame_count: 10, clear_existing: true, load_after: true, notes: "ARA wizard"})`
4. PHD2 captures the frame stack; WebSocket events stream progress (via PHD2's own event server, ingested by ARA — see §63.8)
5. Completion event from PHD2 → ARA fires `guider.dark_library.complete` event → modal updates: *"Dark library built. Uncover the guide scope before guiding starts."*
6. ARA records `calibration_state.guider.dark_library` in profile

**Defect map** is built less frequently (typically once per camera, very stable). Wizard offers a [Also build defect map (~3 min, advanced)] checkbox; default unchecked. Power users can enable from Settings → Guider → Advanced.

**Per-profile storage** — both dark library and defect map are stored per-PHD2-profile by PHD2 itself. ARA's per-ARA-profile mapping means each ARA profile gets its own dark library, automatically.

### 63.7 Equipment-change invalidation (extends §30.7)

The §30.7 invalidation matrix extends to invoke the PHD2 update sequence for guider-affecting changes:

| Equipment change | PHD2 update action | Dark library effect |
|---|---|---|
| Guide camera | Disconnect → `set_selected_camera`/`_id` to new device → reconnect | Invalidated (different sensor = different hot pixels) → rebuild required |
| Guide scope (focal length) | `set_profile_setup({focal_length: ...})`, no disconnect required for this specific field | Library still valid (geometry change doesn't affect dark frames) |
| Mount | Disconnect → `set_selected_mount` / `set_alpaca_server` / `set_selected_alpaca_device` → reconnect | Library still valid (mount change doesn't affect guide camera noise) |
| PHD2 config / aggressiveness / min-motion (user-edited in Settings → Guider → Advanced) | `set_algo_param` per changed param | Library still valid |

When dark library invalidates, ARA surfaces a banner in the main shell:

> *"Guide camera changed — PHD2's dark library is stale. Build a new one now (~2 min) or guiding will use unsubtracted frames."  [Build now]  [Later]*

If user defers, guiding continues with no dark subtraction. Quality may degrade (more star-detection false positives from hot pixels) but it works.

### 63.8 Log + event ingestion via PHD2's event stream

PHD2's JSON-RPC socket carries both method responses AND asynchronous events on the same connection. ARA subscribes to the event stream and ingests guiding-relevant events into ARA's session log:

- `StarLost` → ARA notification + session log entry
- `Calibrating` / `CalibrationComplete` / `CalibrationFailed`
- `SettleBegin` / `SettleDone` / `Settling`
- `GuideStep` (frequent — per-frame guide correction; ARA samples for §50 Stats, doesn't notify per-step)
- `Alert` (PHD2's own notification mechanism — passed through to ARA's notification feed)

Implementation: ARA maintains the persistent TCP connection PHD2 uses, dispatching events to the appropriate ARA subsystem (logging, notifications, Stats DB).

**ARA does NOT tail PHD2's log files.** The JSON-RPC event stream is the canonical channel. PHD2's `/var/lib/openastro-phd2/PHD2_GuideLog_*.txt` files are still written for forensic post-mortem use but ARA doesn't depend on them.

### 63.9 PHD2 version detection + handshake

On every (re)connect, ARA calls `get_app_state` and inspects PHD2 version info. The fork identifies itself distinctly from upstream PHD2:

- If version reports `openastro-phd2 vX.Y.Z` → log "Connected to openastro-phd2 vX.Y.Z" + verify against ARA's known-compatible range
- If version reports stock PHD2 → log warning *"Connected to upstream PHD2 — some Linux/headless features may be unavailable. Recommend installing openastro-phd2 from apt.openastro.net."* and continue (graceful degradation)
- If version is older than ARA's tested minimum → log compatibility warning + continue

ARA does not refuse to operate against upstream PHD2. The fork is recommended; not required.

### 63.10 PHD2 not installed — graceful degradation

If `openastro-phd2` is not installed (user opted out of Recommends, or removed it):

- ARA server still starts cleanly; reports `guider_available: false` in `/api/v1/server/state`
- Wizard's guider screen shows a banner: *"openastro-phd2 not detected. Install with `sudo apt install openastro-phd2`."*
- Sequences with guiding requirements warn before start: *"This sequence requires guiding but openastro-phd2 is not installed. Continue without guiding (frames may show star trails)?"*
- Dither auto-disables (per §62.7)

### 63.11 Failure handling matrix

| Failure | Action |
|---|---|
| openastro-phd2 not installed | `guider_available: false`; wizard banner; sequences warn before start |
| Service won't start (systemd `failed`) | Urgent notification; guider features disabled until user fix |
| RPC connect fails repeatedly | Backoff (1/5/15/30/60/120 s); after all fail, treat as hung, restart service |
| `set_connected(true)` fails (equipment not present after profile push) | Per §42.3 hot-reconnect; surface to wizard as "Equipment not connected in PHD2 — check that the camera/mount you selected is reachable" |
| Mid-session crash | Pause sequence per §42.2; critical notification; systemd auto-restarts; ARA reconnects |
| Hung mid-guiding (RPC unresponsive, process alive) | Force `systemctl restart openastro-phd2`; treat as star_lost during recovery |
| `build_dark_library` fails | Surface specific error (no camera / capture active / save failure); user retries from Settings |
| Profile push fails (precondition violation) | Retry: send `set_connected(false)` first, then re-push; if still fails, surface explicit error |

### 63.12 API endpoints + WebSocket events

| Method | Path | Purpose |
|---|---|---|
| `GET` | `/api/v1/guider/status` | PHD2 lifecycle state + version + last-seen app state + connected equipment |
| `POST` | `/api/v1/guider/restart` | Force `systemctl restart openastro-phd2`; idempotent per §60.5 |
| `POST` | `/api/v1/guider/profile/push` | Push current ARA-profile params to PHD2; runs the disconnect-update-reconnect sequence |
| `POST` | `/api/v1/guider/dark-library/build` | Initiate dark library build (with prompt-cover modal flow on client) |
| `GET` | `/api/v1/guider/dark-library/state` | Returns `get_calibration_files_status` result (paths, exists, loaded, frame count) |
| `DELETE` | `/api/v1/guider/dark-library` | `delete_calibration_files({delete_dark_library: true})` for current profile |
| `POST` | `/api/v1/guider/defect-map/build` | Build defect map (advanced; opt-in) |
| `GET` | `/api/v1/guider/equipment-choices` | Mirrors `get_equipment_choices` — lists available cameras/mounts for wizard dropdowns |
| `POST` | `/api/v1/guider/discover-alpaca` | Mirrors `discover_alpaca_servers` — surfaces Alpaca servers PHD2 can see (useful when ARA's discovery and PHD2's discovery disagree about what's on the network) |

WebSocket events:

```json
{ "type": "guider.lifecycle",      "payload": { "state": "connected_ready", "previous": "connecting", "phd2_version": "openastro-phd2 2.6.14" } }
{ "type": "guider.crashed",        "payload": { "uptime_seconds": 14523, "restart_attempt": 1 } }
{ "type": "guider.restart_failed", "payload": { "attempts": 5, "next_action": "user_intervention_required" } }
{ "type": "guider.profile_pushed", "payload": { "ara_profile_id": "...", "phd2_profile_name": "ara-c14-cem120", "fields_changed": ["camera", "mount", "focal_length"] } }
{ "type": "guider.dark_library.building", "payload": { "frame_index": 3, "total_frames": 10, "exposure_ms": 1500 } }
{ "type": "guider.dark_library.complete", "payload": { "frame_count": 10, "took_seconds": 117, "path": "..." } }
{ "type": "guider.dark_library.invalidated", "payload": { "reason": "guide_camera_changed" } }
{ "type": "guider.event_passthrough", "payload": { "phd2_event": "StarLost", "raw": {...} } }
```

### 63.13 §61 search registry coverage

- `guider.service_management` — keywords: `PHD2 service, openastro-phd2 systemd, guider service`
- `guider.profile.camera` — keywords: `guide camera, PHD2 camera, guider camera, OAG camera`
- `guider.profile.focal_length` — keywords: `guide scope focal length, guide scope FL, OAG focal length`
- `guider.profile.aggressiveness` — keywords: `PHD2 aggressiveness, guide aggressiveness, RA aggressive, Dec aggressive`
- `guider.dark_library_build` — keywords: `PHD2 dark, guide camera dark, hot pixel dark, dark library, build dark`
- `guider.defect_map_build` — keywords: `PHD2 defect map, hot pixel map, bad pixel map, defect map`
- `guider.restart` — keywords: `restart PHD2, restart guider, guider unresponsive, openastro-phd2 restart`

### 63.14 DEPLOY.md updates

Replace any earlier wording about "one-time VNC setup of PHD2" with:

> *PHD2 is installed and managed automatically as the openastro-phd2 package (pulled in via Recommends when you install openastroara-server). It runs as a background service starting at Pi boot. ARA's wizard configures PHD2's profile, equipment selections, and dark library through PHD2's documented JSON-RPC API — you don't need to interact with PHD2 directly via VNC or a remote display.*

### 63.15 What's NINA-preserved vs ARA-changed

**Preserved (via openastro-phd2 itself, not ARA):** PHD2's JSON-RPC API contract (NINA depends on the same surface), settle event protocol, calibration data shape, guide algorithm internals.

**ARA additions:**
- systemd-managed PHD2 (via the openastro-phd2 .deb's own unit)
- Auto-restart with exponential backoff layered on systemd
- Hung-process detection via JSON-RPC ping
- Per-ARA-profile to PHD2-profile mapping
- Profile + dark-library push from ARA's wizard (vs NINA's "user runs PHD2 manually")
- Event stream ingestion into ARA's session log + Stats (§50)
- Equipment-change auto-update via the §30.7 invalidation pipeline
- Graceful handling when openastro-phd2 not installed

### 63.16 v0.1.0 expansion paths

- **WILMA-pushed openastro-phd2 binary updates** (§33.6, §55.1) — same atomic-swap + rollback pattern as ARA Core's WILMA push, applied to the openastro-phd2 sibling package
- **AI-driven calibration assistance** — ARA observes user's manual PHD2 calibration once; learns optimal params; suggests improvements in subsequent sessions
- **Multi-guider support** — second OAG camera, dual-rig observatory setups
- **PHD2 advanced algorithm tuner** — visual UI for `set_algo_param` parameters with explanations, since PHD2's own UI is dense and headless ARA users can't access it

---

## 64. Live View / Loop Imaging

A continuous loop of short exposures with previews pushed straight to WILMA and no FITS persistence. Used for **framing a target, eyeball focus checks, sky/cloud assessment, and dusk setup** — interactive workflows where the user is sitting at the screen and wants near-real-time visual feedback, but doesn't want to commit frames to the library.

Separate from running a Sequence (§38), Take One (single saved frame), Polar Alignment (§45, also a loop but with PA-specific overlay), and Smart Focus (§59, also short exposures but goal-directed).

### 64.1 What Live View is — 60-second context

The user opens the Imaging tab (§25.5), sets exposure / gain / binning, and taps **Live View**. The server starts capturing short frames in a loop, pushing each preview JPEG to WILMA as it finishes. Cadence is typically one frame every 2-4 seconds (bounded by exposure + Alpaca readout). No FITS files are written; no library entries are created. The user nudges the mount, turns a Bahtinov mask, or watches for clouds. When done, they tap **Stop** and the loop ends (current exposure aborted if mid-flight).

If they see a frame they want to keep, they tap **Save Current** and the next finished frame is promoted through the normal capture pipeline (FITS write, preview generation, library indexing, §40 + §51 metadata enrichment).

### 64.2 Why ARA has to build this from scratch (no NINA code to port)

NINA's existing Live View (`ImagingVM.StartLiveView`) is **driver-specific**: QHY uses native `BeginQHYCCDLive()` video-stream mode, Canon DSLRs use EOS native Live View, and `Generic` / ASCOM / Alpaca cameras return `CanShowLiveView => false` — the button is grayed out and the feature doesn't exist.

ARA is Alpaca-only forever (§52). Standard Alpaca `ICamera` has no streaming mode, and **ASCOM `IVideo` is deprecated** (kept only for backward compatibility). There is no standards path to native video streaming.

So ARA implements Live View as a server-side loop of short normal captures with previews routed straight to WebSocket and no FITS save. This works with every Alpaca camera — AlpacaBridge-driven (ZWO, QHY, SVBONY, ToupTek, PlayerOne), third-party ASCOM-Alpaca drivers, simulators for CI/dev. Cadence is bounded but adequate for the framing/focus-check use case.

Per §18.J, ARA's scope is DSO + comets only — high-frame-rate workflows (planetary / lucky-imaging) are permanently out of scope, not deferred. The loop-of-captures approach is therefore not a temporary workaround; it's the right primitive for the workloads ARA targets.

### 64.3 Scope — framing + focus-check, not stacking

**In scope for v0.0.1:**
- Continuous loop of single short exposures
- Live preview JPEG per frame via WS
- Inline frame stats (HFR, star count, mean ADU) on each frame
- Exposure / gain / offset / binning controls, changeable mid-loop
- Single Save Current Frame action
- Mutual exclusion with Sequence, Polar Align, Smart Focus, plate-solve (all use the same camera)

**Out of scope for v0.0.1 (deferred to v0.1.0):**
- Running-average / live stacking (covered by v0.1.0 live stacking commitment in §55)
- Multi-frame averaging for visible noise reduction during display

**Permanently out of scope (per §18.J):**
- Native SDK video mode / high-frame-rate capture — Alpaca has no video API; not coming
- ROI (region-of-interest) live capture for lucky-imaging — same architectural reason
- SER file format support (planetary stack input format) — no workflow needs it

### 64.4 The loop — how it actually works

Server-side state machine:

```
IDLE → (start request) → STARTING → LOOPING → STOPPING → IDLE
                              ↑          │
                              └──── (frame complete, restart) ─────┘
```

Inside `LOOPING`:

1. Server calls Alpaca `StartExposure(duration, light=true)` with the configured exposure
2. Polls `ImageReady` until true (or `ExposureAbort` fires from a Stop request)
3. Calls `ImageArray`, downloads the bytes
4. In-memory: extracts a quick **frame stat block** (mean, median, std, HFR via Hocus Focus star detector, star count)
5. Generates a stretched JPEG (OpenCvSharp4, same stretch pipeline as §26 / §40.2 preview tier, no separate thumb — single JPEG only)
6. Writes JPEG to `/var/lib/openastroara/tmp/live-current.jpg` (overwrites previous)
7. Fires WS event `live.frame` with frame metadata + URL + frame counter
8. Loops back to step 1 immediately (no inter-frame sleep — Alpaca readout is the only cadence floor)

No FITS file is written. No SQLite row. No backup-stream enqueue. No §51 diagnostics. The frame exists only in `/tmp/` and is overwritten by the next one.

### 64.5 Exposure controls + cadence

Defaults from profile (see §64.12). User can change mid-loop:

| Control | Type | Range | Default | Notes |
|---|---|---|---|---|
| `exposure_seconds` | float | 0.001 - 30.0 | 2.0 | Short = fast cadence; long = better S/N for dim targets |
| `gain` | int | camera-dependent | profile default | Hot-mutable |
| `offset` | int | camera-dependent | profile default | Hot-mutable |
| `binning` | int (1, 2, 3, 4) | per camera caps | 1 | 2×2 or 3×3 useful for very dim framing |
| `filter_id` | string \| null | from filter wheel | current | Optional override; defaults to whatever's loaded |

**Mid-loop change behavior**: when a setting changes via `PATCH /api/v1/imaging/live/settings`, the in-flight exposure is **aborted** and the next loop iteration uses the new settings. Same abort semantics as Stop — fast response over completed-frame quality.

**Hard upper bound**: `exposure_seconds` capped at 30s. Longer than that defeats the interactive premise; user should use Take One instead. Server rejects requests > 30s with 422 (per §60 error conventions).

**Realistic cadence:**
| Exposure | Camera readout | Total per frame | Frames per minute |
|---|---|---|---|
| 0.5s | ~0.8s (ASI2600 16-bit) | ~1.3s | ~46/min |
| 2s | ~0.8s | ~2.8s | ~21/min |
| 10s | ~0.8s | ~10.8s | ~5.5/min |
| 30s | ~0.8s | ~30.8s | ~2/min |

Pi 4 can keep up with JPEG generation at this cadence with OpenCvSharp4 (full-sensor stretch + encode is ~200-500 ms on a 26 MP frame; bin-2 takes ~80-150 ms).

### 64.6 JPEG delivery + frame storage

**Storage**: single file at `/var/lib/openastroara/tmp/live-current.jpg`, overwritten every frame. The `tmp` directory is on the OS SD card (not the mandatory USB drive from §29) because:
- It's ephemeral and small (~150-800 KB per overwrite)
- USB drive should be reserved for FITS writes (SD wear protection per §29 is satisfied because we never accumulate; we just overwrite the same inode)
- A tmpfs mount would be ideal (zero SD wear) but tmpfs is RAM, and we want this to survive a brief WILMA disconnect without re-capture

**HTTP endpoint**: `GET /api/v1/imaging/live/current.jpg` serves the file. `Cache-Control: no-store` so WILMA always fetches fresh.

**Cache-busting**: each WS `live.frame` event carries an incrementing `frame_seq` counter. WILMA's `Image.network` URL is `/api/v1/imaging/live/current.jpg?seq=<N>` so Flutter's image cache treats each frame as a new resource.

**Why not WS binary frames**: tmpfile + URL preserves the same delivery pattern as `frame.complete` (§40), simplifies WILMA's `Image.network` wiring, and keeps WS messages small (just metadata + URL). The HTTP round-trip on LAN is 5-20 ms — well under per-frame readout. Trade-off accepted; v0.1.0 may revisit if native video mode lands and frame rates climb above ~5 fps.

**Cleanup**: server deletes the file on `live.stopped` and on server startup (in case of crash). The file is also harmless if it persists — it's just a stale preview.

### 64.7 Save Current Frame

Promotes the **next** finished frame through the normal capture pipeline. Not the *current* in-flight frame — the user has already seen it; they want the next one which presumably looks the same.

Flow:
1. WILMA: `POST /api/v1/imaging/live/save_current` (idempotency-key per §60)
2. Server arms a one-shot flag. Next finished frame:
   - Writes FITS to the configured session save path (§29, §39.3)
   - Generates thumb + preview per §40.2
   - Indexes in SQLite `frames` table with session metadata
   - Fires standard `frame.complete` WS event (so it shows up in the Image Library)
   - Tags the row with `capture_source = "live_view"` (so it's distinguishable in Stats and Library filters)
3. Live View loop continues uninterrupted (the saved frame is just the next iteration of the loop, dual-routed to disk + tmpfile)
4. Server fires `live.frame_saved` WS event with the saved frame's `id` and library URL

Saved frames are NOT part of any active session by default — they go to a synthetic session named `live-view-<date>` so they don't pollute real session metrics in §50 Stats. User can later promote them to a real session via Image Library bulk operations (§40).

If the user taps Save Current multiple times in quick succession, each tap arms a fresh one-shot (i.e., saves multiple consecutive frames). No coalescing — explicit intent each time.

### 64.8 Stop semantics — abort mid-exposure

Stop is **immediate**. When `POST /api/v1/imaging/live/stop` arrives mid-exposure:

1. Server calls Alpaca `AbortExposure` (or `StopExposure` if `CanAbortExposure=false` — most modern cameras support abort)
2. Discards any partial frame bytes (camera may or may not return them; either way they're dropped)
3. Deletes `live-current.jpg`
4. Fires `live.stopped` WS event with `reason: "user_requested"`
5. State → `IDLE`

Wait-time for Stop to take effect: typically < 100 ms. The user gets responsive UI even if they had set a 30s exposure.

**Other auto-stop triggers** (same code path, different `reason`):
| Trigger | `reason` | Notes |
|---|---|---|
| User Stop button | `user_requested` | Default |
| Sequence start | `sequence_started` | Sequence has priority; Live View yields |
| Plate solve invoked | `plate_solve_requested` | One-shot capture needs the camera |
| Smart Focus / Classic AF triggered | `autofocus_started` | §59 takes priority |
| Polar align invoked | `polar_align_started` | §45 takes priority |
| Take One invoked | `take_one_requested` | Single capture preempts loop |
| Camera disconnect | `equipment_disconnect` | Fault per §42 |
| WILMA disconnects > 5 min | `client_idle` | No active client means no consumer for the frames |
| Server shutdown | `server_shutdown` | Graceful via §57.8 / §35 |

WILMA receives the reason and updates UI accordingly (e.g., "Live View stopped — sequence started" toast).

### 64.9 Interaction with other systems

| System | Live View behavior |
|---|---|
| **Cooler (§28)** | Untouched. Live View is interactive (user at screen); cooler can be at any temperature including warming, ramping, or off. Warning displayed if cooler delta > 5°C from target (informational only). |
| **PHD2 / guider (§63)** | Untouched. If guiding is active, it stays active (probably useful — keeps target framed while user evaluates). User can stop guiding manually if it interferes. No auto-start. |
| **Mount / tracking (§57)** | Untouched. Tracking continues. User can slew manually via Equipment panel during Live View — Live View doesn't fight slews, frames just blur during the slew and re-stabilize after. |
| **Focuser** | Untouched. User can manually adjust focuser during Live View via Equipment panel — exactly the workflow for eyeball focus check with a Bahtinov mask. |
| **Filter wheel** | Untouched. User can change filter mid-loop via Equipment panel; current in-flight frame discarded, next frame uses new filter (similar mid-loop abort behavior as exposure change). |
| **Rotator** | Untouched. User can rotate during Live View for framing rotation checks. |
| **Sequence (§38)** | Mutually exclusive. Sequence start auto-stops Live View. Live View blocked while sequence is active or paused. Server returns 409 with `code: "sequence_active"` (§60 error). |
| **§51 diagnostics** | **Not run on live frames.** Diagnostics watch session integrity (clouds during a 4-hour run); Live View is interactive with a human present. The signals would be noisy and the auto-actions inappropriate. |
| **§35 safety policies** | Mostly irrelevant. Live View is an attended workflow; the unattended-failure shutdown machinery (§35, §58.12) doesn't engage. Stop-Mount safety button (§57) still works normally if user invokes. |
| **§42 equipment fault recovery** | Camera disconnect / fault aborts Live View per §64.8 auto-stop. Other equipment faults (mount, focuser, filter wheel) fire warnings via §46 but don't auto-stop Live View — user decides whether to continue. |
| **§44 backup stream** | Live View frames are never enqueued (no FITS = nothing to back up). Save Current frames go through standard backup-stream enqueue per §44.2. |

### 64.10 Single-session enforcement

Only one Live View session at a time, enforced by single-client policy from §27. The first WILMA connected can start Live View; if a second WILMA tries to connect, §27.4's popup-transfer flow fires, and the new client inherits the active Live View session on accept.

Server-side: in-memory `LiveViewService` is a singleton with a single state machine. Concurrent start requests get 409 with `code: "live_view_already_active"`.

### 64.11 Mobile companion behavior (§41)

Phones / tablets are **monitor-only** during Live View, consistent with §41's mobile companion mode philosophy. Specifically:

- ✅ Subscribe to `live.frame` events and display the preview JPEG with pinch-to-zoom
- ✅ Display frame stats overlay (HFR, star count, mean ADU)
- ✅ See the Stop button (visible but disabled with tooltip "Use desktop to control Live View")
- ❌ Cannot start Live View
- ❌ Cannot change exposure / gain / binning / filter
- ❌ Cannot tap Save Current (desktop-only action)

Rationale: Live View is a setup workflow (framing, focus). The user is at the rig, not across the house. Mobile relevance is mostly "show the partner what I'm seeing." If mobile demand emerges, v0.1.0 can promote mobile to full control.

Emergency Stop (§35.3) on mobile is **always active** and stops Live View as a side effect — same as it stops anything else.

### 64.12 Profile schema

Minimal — Live View doesn't need much per-profile customization. Sits under `imaging.live_view`:

```yaml
imaging:
  live_view:
    default_exposure_seconds: 2.0   # 0.001 - 30.0
    default_gain: 100               # camera-dependent default
    default_offset: 50              # camera-dependent default
    default_binning: 1              # 1, 2, 3, 4 (clamped to camera caps)
    auto_save_path: synthetic       # "synthetic" (live-view-<date>) | "current_session"
    stretch: profile_default        # inherits §40.2 user stretch
```

`auto_save_path: current_session` is for power users who want Save Current frames to count toward an active session's metrics. Default `synthetic` keeps Live View saves out of session stats.

Wizard touchpoint: **none**. Defaults are camera-aware (gain/offset come from the camera-class defaults set in §37.4 Stage 4 — Imaging Tools) and the user picks per-session anyway. Live View settings are reachable via §61 settings search ("live view exposure", "live view gain").

### 64.13 API endpoints

Per §60 conventions: RFC 7807 errors, idempotency-key on mutating endpoints, JSON-pointer 422 validation.

| Method | Path | Purpose |
|---|---|---|
| `POST` | `/api/v1/imaging/live/start` | Start the loop. Body: `{ exposure_seconds, gain?, offset?, binning?, filter_id? }`. Returns `{ session_id, started_at, settings }`. Requires Idempotency-Key. |
| `POST` | `/api/v1/imaging/live/stop` | Stop the loop. Empty body. Returns `{ stopped_at, reason, frames_delivered }`. Requires Idempotency-Key. |
| `PATCH` | `/api/v1/imaging/live/settings` | Mid-loop settings change. Body is partial (any of `exposure_seconds`, `gain`, `offset`, `binning`, `filter_id`). Returns updated settings. Aborts in-flight exposure. |
| `POST` | `/api/v1/imaging/live/save_current` | Arm one-shot save of next frame. Returns `{ armed_at }`. Idempotency-Key dedups multi-tap. |
| `GET` | `/api/v1/imaging/live/state` | UI rehydrate. Returns `{ state: "idle"|"starting"|"looping"|"stopping", settings, frames_delivered, started_at, last_frame: { seq, hfr, stars, mean, timestamp } | null }`. Surfaced via `/api/v1/server/state` snapshot per §60 too. |
| `GET` | `/api/v1/imaging/live/current.jpg` | Latest preview JPEG. `Cache-Control: no-store`. 404 if state != `looping`. Optional `?seq=N` query honored for cache-busting (server ignores the value). |

All endpoints are open per §67 (no auth in v0.0.1). Rate-limiting deferred per §60.

Errors (per §60 RFC 7807):
| Code | Status | When |
|---|---|---|
| `sequence_active` | 409 | Sequence running or paused |
| `polar_align_active` | 409 | §45 PA in progress |
| `autofocus_active` | 409 | §59 Smart Focus / Classic AF in progress |
| `live_view_already_active` | 409 | Existing Live View session active (single-client violation) |
| `camera_disconnected` | 503 | Camera not connected |
| `cooler_warming` | 503 | Cooler is in warmup mode per §28 (informational; can be overridden with `force=true` query) |
| `invalid_exposure` | 422 | `exposure_seconds` out of [0.001, 30.0] |
| `invalid_binning` | 422 | Binning exceeds camera caps |

### 64.14 WebSocket events

All events fire on the existing `/api/v1/ws` channel per §27. WS resume per §60 covers Live View events for the 1-hour replay window.

| Event | Payload | When |
|---|---|---|
| `live.started` | `{ session_id, started_at, settings }` | Loop transitions to `LOOPING` |
| `live.frame` | `{ frame_seq, url, hfr, stars, mean, median, std, exposure_used, timestamp }` | Each frame finalized + JPEG written |
| `live.frame_saved` | `{ frame_seq, frame_id, library_url, saved_at }` | Save Current frame completes |
| `live.settings_changed` | `{ settings, reason: "user_patch" }` | After successful PATCH |
| `live.stopped` | `{ stopped_at, reason, frames_delivered }` | Loop transitions to `IDLE` |
| `live.error` | `{ error: { type, title, detail }, recoverable: bool }` | Capture error mid-loop (e.g., camera fault); follows RFC 7807 shape per §60. If `recoverable: false`, loop stops; if `true`, loop continues and the error is informational. |

WILMA subscribes by default when the Imaging tab is open; subscription persists when user switches tabs so frames continue arriving (frame counter visible in status bar).

### 64.15 §61 settings registry coverage

Per the registry gate in COMMIT-PR-RULES, every user-visible setting registered. Live View contributes 6:

| Registry entry | Section | Type | Search keywords |
|---|---|---|---|
| `imaging.live_view.default_exposure_seconds` | §64.12 | float | live view, exposure, framing, loop |
| `imaging.live_view.default_gain` | §64.12 | int | live view, gain |
| `imaging.live_view.default_offset` | §64.12 | int | live view, offset |
| `imaging.live_view.default_binning` | §64.12 | int (1-4) | live view, binning, framing |
| `imaging.live_view.auto_save_path` | §64.12 | enum | live view, save, session |
| `imaging.live_view.stretch` | §64.12 | enum | live view, stretch, preview |

All searchable from the §61 omnibar.

### 64.16 What's NINA-preserved vs ARA-new

**NINA-preserved**: nothing — NINA's Live View is driver-specific (QHY/Canon native), unavailable on Alpaca, deleted in Phase 8 along with the rest of the non-Alpaca camera-driver code.

**ARA-new**:
- Server-side `LiveViewService` singleton with state machine
- Loop-of-captures over Alpaca `ICamera` standard interface
- Single-overwrite tmpfile JPEG delivery
- WS `live.*` event family
- Save Current Frame promotion path (re-routes one frame through standard capture pipeline)
- Auto-stop on mutually-exclusive operations (sequence, AF, PA, plate-solve, Take One)
- Mobile companion read-only view
- §61 settings registry entries
- Frame stat extraction reused from §51 / Hocus Focus (no new code, just calling existing analyzers)

### 64.17 v0.1.0 expansion paths

- **Live stacking integration (§55 commitment)** — Live View becomes the realtime preview surface for the v0.1.0 live-stacking pipeline. User taps "Stack" instead of (or in addition to) Live View; same loop, but each frame is registered + integrated into a running stack; preview shows the integrated result, not the latest frame.
- **Mobile full control** — if demand emerges, promote mobile companion from read-only to full Live View control.
- **Multi-frame averaging for display** — server-side running average of last N frames for noise-reduced preview (display-only, doesn't change Save Current behavior). Trades latency for visible SNR.
- **Bahtinov mask focus indicator** — automatic detection of Bahtinov mask diffraction pattern with a numeric "focus quality" score on each live frame. Power-user feature; small audience but high value for that audience.

**Permanently out of scope** (per §18.J): native SDK video mode, ROI capture, SER file format output — all are lucky-imaging features that require a video API Alpaca doesn't provide.

---

## 65. Image stretching pipeline + preview API

§40.2 generates two JPEG previews per captured FITS using the user's default stretch. This section specs the stretch palette, defaults policy, server-side compute + cache strategy, and the API knobs the client uses to request alternative renderings. All pixel processing happens server-side via OpenCvSharp4 (per §26); the WILMA client receives JPEGs over HTTP and never touches FITS pixels in v0.0.1 (real-time client-side slider deferred to v0.1.0, see §65.10).

### 65.1 Stretch palette (v0.0.1)

Seven stretch IDs ship in v0.0.1:

| Stretch ID | Algorithm | Use case |
|---|---|---|
| `auto_stf` | PixInsight-style STF — auto blackpoint/midpoint/whitepoint from histogram median + MAD (Median Absolute Deviation). Adopted defaults: shadows clipping = −2.8 σ_MAD below median, target background = 0.25, highlights clipping = 99.998%. | **Default for Light frames.** General-purpose "make it look good." Matches NINA + PI expectations. |
| `linear` | Raw values clipped at supplied black/white percentiles (defaults: 0.5% / 99.5%) and rescaled to 0–255. | Technical inspection — clipping check, ADU verification, calibration-frame review. **Auto-default for Dark/Bias/Flat** per §65.2. |
| `log` | `log(x − bp + 1)` scaling, then rescale to 0–255. | High-dynamic-range objects (M42 core, globular clusters). |
| `asinh` | Lupton arcsinh: `asinh(beta · (x − bp)) / asinh(beta)`. Default β = 3.0, tunable via `beta` param. | Galaxies + faint nebulae. Well-behaved near zero, less likely to crush shadows than log. Modern preference among PI/Siril users. |
| `sqrt` | Gamma 0.5 after black/white-point clip. | Old-school but still useful for some targets. Cheap to compute. |
| `equalized` | Full histogram equalization. | "What's there?" quick-look pass; ignores absolute brightness. |
| `manual` | User-supplied `blackpoint`, `midpoint`, `whitepoint` (0–1 range). | Power-user override; backs the manual sliders in the frame viewer. |

No per-filter stretch palette in v0.0.1 — stretches are universal across filters. (Per-filter defaults are a different question, addressed in §65.2.)

### 65.2 Defaults policy

Defaults compose as: **(frame-type auto-override) overrides (per-profile default) overrides (request-time `?stretch=` param)**.

- **Per-profile default for Light frames** — single setting in profile, default `auto_stf`. Applies to all filters; no per-filter override in v0.0.1.
- **Frame-type auto-override (automatic)** — Dark / Bias / Flat frames always render at `linear` regardless of profile default. Histogram games on calibration frames mislead users about clipping, signal-vs-noise, and bias level. No way to disable this auto-override in v0.0.1 (power users can still request `?stretch=` per-request).
- **Per-filter defaults** — explicitly out of scope for v0.0.1. Profile carries one default for Lights; if the user wants different stretches per filter, they pick per-frame in the viewer. Adds complexity now without proven UX win; reconsider in v0.1.0 if users ask.

Profile schema additions:

```json
{
  "stretch_defaults": {
    "light_default": "auto_stf",
    "manual_default_params": { "blackpoint": 0.02, "midpoint": 0.5, "whitepoint": 0.98 },
    "asinh_default_beta": 3.0,
    "linear_clip_percentiles": [0.005, 0.995]
  }
}
```

### 65.3 Where compute happens

Server-side, on top of OpenCvSharp4 (per §26):

- **Capture-time preview** (`<frame>.preview.jpg`): generated server-side at capture time, using the profile's `light_default` (or frame-type auto-override). Cached on disk alongside the FITS. Always exists for completed frames.
- **Alternative stretches**: server compute on first request, cached at `<frame>.preview.<stretch-id>.jpg` (or `<frame>.preview.manual.<hash-of-params>.jpg` for manual stretch). Subsequent requests serve from cache.
- **Manual stretch sliders (frame viewer)**: each slider drag fires `GET /api/v1/frames/{id}/preview?stretch=manual&blackpoint=...&midpoint=...&whitepoint=...` debounced 200 ms. Server computes + caches; cache key includes the rounded param values (3 decimal places) to bound the cache entry count.
- **Client-side real-time slider (no server round-trip)**: deferred to v0.1.0 (see §65.10). v0.0.1's 200 ms debounce + LAN-only deployment makes server round-trip adequate for the slider UX.

### 65.4 Cache strategy

Per-frame variant cache:

- **Default cap: 6 variants per frame**, plus the default-stretch preview that always exists.
- **LRU eviction** within a frame when cap is exceeded.
- **Cache cap configurable** via Settings → Image Processing → Preview Cache (range 1–20).
- **Manual-stretch entries** count against the cap but are coalesced by rounded params; rapid slider dragging doesn't blow the cache because near-identical param tuples hash to the same cache key.
- **Thumbnails** (`<frame>.thumb.jpg`) are **default stretch only** — re-stretch on thumbnails is not supported in v0.0.1 (would multiply cache cost for negligible UX gain; frame viewer uses the full preview).
- **Disk overhead estimate**: 6 variants × 3–8 MB ≈ 18–48 MB extra per frame in the worst case (every variant requested). Most frames will have 0–2 alternate variants in practice. USB storage from §29 has plenty of headroom.
- **Eviction policy on storage pressure**: when free space on the configured save path drops below the §29 critical threshold, preview variants (NOT defaults, NOT FITS, NOT thumbnails) evict first as recoverable cache. WS event `frame.preview.variant.evicted` notifies the client so its UI knows to fall back to default if it had been showing an alt.

### 65.5 Batch re-stretch

For "re-stretch entire session in asinh":

- `POST /api/v1/sessions/{id}/restretch` enqueues a background job. Returns 202 + `job_id`.
- Job runs server-side, generating + caching alt-stretch variants for each frame matching the filter (frame_type / filter band).
- WS events report progress: `session.restretch.progress` + `session.restretch.complete` / `session.restretch.failed`.
- Cancellable: `DELETE /api/v1/jobs/{job_id}`.
- Rate-limited to one batch job per profile at a time; second `POST` while a job runs returns 409 with the running job's id.

### 65.6 API extensions to §40.3

Extends the existing `GET /api/v1/frames/{id}/preview` endpoint and adds three new endpoints:

```
GET /api/v1/frames/{id}/preview
  Query params:
    stretch       auto_stf | linear | log | asinh | sqrt | equalized | manual
                  (default: profile's light_default, or frame-type auto-override)
    blackpoint    0.0–1.0    (manual only)
    midpoint      0.0–1.0    (manual only)
    whitepoint    0.0–1.0    (manual only; must satisfy bp < mp < wp)
    beta          > 0        (asinh only; default = profile's asinh_default_beta or 3.0)
    bp_pct, wp_pct 0.0–1.0   (linear only; default = profile's linear_clip_percentiles)

  Responses:
    200 image/jpeg            — cached variant served
    202 Retry-After: 1        — compute queued (batch-job scenario); client retries
    400                       — invalid params (e.g., manual without all three points, bp ≥ wp)
    404                       — frame not found
```

```
GET  /api/v1/profiles/{id}/stretch-defaults    → returns the stretch_defaults block
PATCH /api/v1/profiles/{id}/stretch-defaults   → updates one or more fields
```

```
POST   /api/v1/sessions/{id}/restretch         → enqueue batch job
DELETE /api/v1/jobs/{job_id}                   → cancel batch job
GET    /api/v1/jobs/{job_id}                   → job status
```

```
DELETE /api/v1/frames/{id}/preview/variants    → flush all alt-stretch variants for a frame
                                                  (keeps default + thumb; useful for cache reset)
```

### 65.7 WebSocket events

```json
{ "type": "frame.preview.ready", "payload": { "frame_id": "...", "stretch_id": "auto_stf", "is_default": true } }
{ "type": "frame.preview.variant.ready", "payload": { "frame_id": "...", "stretch_id": "asinh", "params_hash": "..." } }
{ "type": "frame.preview.variant.evicted", "payload": { "frame_id": "...", "stretch_id": "asinh", "reason": "storage_pressure" | "lru_eviction" } }
{ "type": "session.restretch.progress", "payload": { "job_id": "...", "done": 42, "total": 144, "current_frame_id": "..." } }
{ "type": "session.restretch.complete", "payload": { "job_id": "...", "frames_processed": 144, "duration_seconds": 38 } }
{ "type": "session.restretch.failed", "payload": { "job_id": "...", "error": "...", "frames_processed": 42 } }
```

### 65.8 Mobile companion behavior (§41)

§41 caps mobile to view-only / monitoring — no sequence editing, no equipment control. Stretching is a **viewing** capability (the underlying FITS is unchanged), so mobile gets the full stretch palette + manual sliders via the same `?stretch=` API. Mobile UX uses tap-to-cycle through the preset stretches and a bottom-sheet panel for manual sliders.

### 65.9 Frame viewer UI integration (§40.5)

The §40.5 frame viewer mockup gets a stretch picker:

```
┌──────────────────────────────────────────────────┐
│  M42_L_2026-05-18T22:14:32_120s.fits      ⭐⭐⭐⭐  │
│  ──────────────────────────────────────────────  │
│                                                  │
│       [full preview image, pinch/scroll to zoom] │
│                                                  │
│  [Stretch: auto_stf ▼]  [⚙ Manual sliders]      │
│  ──────────────────────────────────────────────  │
│  ...metadata...                                  │
│  [Rate]  [Tag]  [Open in App]  [Show in Folder]  │
│  [Download FITS]  [Delete]                       │
└──────────────────────────────────────────────────┘
```

The picker is a popover with the 7 stretch IDs + the current default highlighted. **[⚙ Manual sliders]** expands an inline panel with blackpoint / midpoint / whitepoint sliders + asinh-β input (when `asinh` selected). Slider drags fire the debounced `?stretch=manual` request. "Reset to profile default" button.

### 65.10 v0.1.0 paths

- **Real-time client-side slider on desktop**: WILMA desktop downloads the full FITS on user-initiated "Power Stretch" mode, parses it with a Dart FITS library, and re-stretches locally on every slider event with no server round-trip. Sub-frame-rate response. Out of scope for v0.0.1 because of FITS-parser dependency + desktop-only execution model.
- **Per-filter stretch defaults**: if users request it, add `per_filter` map to `stretch_defaults` profile block.
- **Color-channel stretching**: for OSC cameras + LRGB composites, independent R/G/B blackpoint/midpoint/whitepoint (PixInsight's screen-color-balance equivalent). v0.0.1 stretches the channels uniformly.
- **STF refinement**: NINA/PI users have tuned shadows/highlights clipping params over time; v0.1.0 could expose those as advanced knobs (currently fixed at the PI defaults).
- **GraXpert / starless preview**: starnet-style background extraction as a preview-only filter. Big win for inspecting nebulosity without star clutter, but ships ML model weights (~50 MB) — defer.
- **Per-target preferred stretch**: profile remembers "M31 always looks best at log; M42 at asinh β=4" via the §40.6 Resume Target flow.

### 65.11 §61 search registry entries

Per §0.5 pillar 3 + COMMIT-PR-RULES settings-registry gate. Every stretch knob is registered:

- `image_processing.stretch_default_light` — keywords: `default stretch, light stretch, auto stretch, stf, preview default, image rendering`
- `image_processing.stretch_default_calibration` — keywords: `dark stretch, bias stretch, flat stretch, calibration stretch, linear`
- `image_processing.manual_blackpoint_default` — keywords: `blackpoint, black point, shadows, clipping shadows`
- `image_processing.manual_midpoint_default` — keywords: `midpoint, mid point, gamma, midtone`
- `image_processing.manual_whitepoint_default` — keywords: `whitepoint, white point, highlights, clipping highlights`
- `image_processing.asinh_beta_default` — keywords: `asinh, arcsinh, beta, lupton, faint nebula`
- `image_processing.linear_clip_percentiles` — keywords: `linear clip, percentile clip, blackpoint percentile, whitepoint percentile`
- `image_processing.preview_cache_cap` — keywords: `preview cache, cache size, variants per frame, alt stretches`
- `image_processing.slider_debounce_ms` — keywords: `slider lag, slider responsiveness, debounce, stretch slider speed`
- `image_processing.open_frame_viewer` — keywords: `frame viewer, image viewer, stretch picker, preview options`

### 65.12 Implementation notes

- All stretch algorithms operate on the FITS as float-normalized (0.0–1.0) in OpenCvSharp4. STF auto-params computed from the histogram of a single channel (mono) or luminance (color).
- Manual-stretch param cache key: `sha1(blackpoint_3dp || midpoint_3dp || whitepoint_3dp || asinh_beta_3dp)` truncated to 8 chars.
- Color frames (OSC): preview generation applies STF/log/asinh/etc. to luminance, then re-applies channel ratios. Doesn't re-balance channels (that's v0.1.0).
- Saturation handling: in linear mode, saturated pixels (>= whitepoint) render as pure white. In other modes, they render at the algorithm's max output (255 after rescale).
- JPEG quality for alt variants matches §40.2 preview (quality 90).
- Compute budget per alt variant: ~50–200 ms on a Pi 4 for a 16 MP frame; faster on Pi 5. Capture pipeline (§28.5) is not blocked by alt-stretch compute (separate worker pool, lower priority).

---

## 66. Server concurrency model

Per §18.J, ARA's supported workloads are DSO + comet long-exposure capture (30–900 s exposures, occasionally short bursts to 1 s for bright targets or comet cadence). Image processing (FITS atomic-write per §28.7 + default-stretch preview per §65) takes ~1–3 s per frame on a Pi 4. The exposure-vs-processing ratio is therefore 10×–300× in capture's favor — the image processor is never the bottleneck for the supported workflows. This section specs the executor layout that exploits that headroom without over-engineering.

### 66.1 Executor pools

| Pool | Threads | Priority | Notes |
|---|---|---|---|
| **Capture** (per camera) | 1 | High | Latency-sensitive Alpaca state machine + readout. Always-available core. |
| **Image processor** | **1** | Normal | FITS atomic-write + default-stretch preview + thumbnail. Single worker is plenty for 30 s+ exposures; queue handles short-exposure bursts (calibration / comet cadence) without dropping. |
| **Alt-stretch** (§65) | 1 | Low | Handles `?stretch=X` API requests + batch re-stretch jobs. Doesn't compete with capture. |
| **WS broadcast** | 1 async | Normal | Pushes events to the single connected WILMA client (per §27). Per-client bounded send queue prevents memory leaks during disconnect scenarios. |
| **REST handler** | ASP.NET Core pool | Normal | Standard thread pool; sized to leave room for capture. |
| **Backup stream** (§44) | 1 | Lowest | Capture-aware backoff per existing §44.4 (pauses during active integration). |
| **Diagnostics** (§51) | 1 | Low | Best-effort frame analyzer; drops oldest on queue full. |
| **SQLite writer** | 1 | Normal | All writes serialized through a single writer task; reads concurrent via WAL (§28.6). |
| **Notification dispatch** (§46) | 1 async | Normal | Fan-out to WS + sound + persistence. |
| **Stats aggregator** (§50) | 1 | Idle | Runs nightly during astronomical dusk preparation or on-demand. |
| **PHD2 client** (§63) | 1 async | Normal | Persistent TCP socket + JSON-RPC reader. Events flow to WS broadcast. |

mDNS responder runs in `avahi-daemon` (system service, not ARA's concern).

### 66.2 Bounded queues

Bounding exists to prevent memory leaks during edge cases (WILMA disconnects, power user spams alt-stretch requests, USB drive briefly hangs), not to manage steady-state contention.

| Queue | Depth | Drop policy | Rationale |
|---|---|---|---|
| Image processor in-queue | 10 jobs | Block capture (rare) | Plenty for any flats/comet/calibration batch; never fills under DSO workload |
| Alt-stretch in-queue | 8 jobs | Reject API request with 503 + `Retry-After: 5` | Power-user flood protection |
| WS broadcast (per client) | 256 events | Disconnect client + flag for `ws_resume_token` replay (§60) | WILMA disconnect/stall protection |
| Backup stream buffer | 1 frame in flight | Block (pause stream) | Per §44.4 |
| Diagnostics in-queue | 3 frames | Drop oldest + log warning | Diagnostics is best-effort by design (§51) |
| SQLite writer | unbounded | Never drop | Critical path; SQLite is fast with WAL |
| Notification dispatch | 64 notifications | Drop `info` severity first; keep `warning`/`critical`/`urgent` | Storm protection |

### 66.3 Thread priorities

Standard .NET `ThreadPriority` settings on dedicated workers:

- `AboveNormal`: Capture
- `Normal`: Image processor, WS broadcast, REST handler, SQLite writer, notification dispatch, PHD2 client
- `BelowNormal`: Alt-stretch, diagnostics
- `Lowest`: Backup stream
- `Idle`: Stats aggregator

ASP.NET Core's thread pool keeps its own policy; the explicit-priority pools above are dedicated workers outside the pool.

### 66.4 Backpressure events

The only steady-state backpressure path is **image processor → capture**, and it should virtually never fire under normal v0.0.1 workloads (DSO 30 s+). When it does fire (storage slow, USB stick instead of SSD, sustained short-exposure burst longer than the queue can hold), the server emits:

```json
{ "type": "capture.backpressure", "payload": { "queue_depth": 10, "queue_max": 10, "exposure_paused_ms": 1850 } }
```

**Silent + WS event only** per the bake decision — no user notification. Visible in the WILMA Stats dashboard if/when the §50 Pi performance panel ships (v0.1.0; see §66.7).

Other backpressure-adjacent events:

```json
{ "type": "storage.slow", "payload": { "fsync_ms": 1450, "threshold_ms": 1000 } }       // sustained fsync > 1s — warning notification, suggests USB SSD upgrade
{ "type": "diagnostics.frame_dropped", "payload": { "frame_id": "...", "reason": "queue_full" } }  // log-only
{ "type": "ws.client_disconnected", "payload": { "reason": "send_queue_full", "resume_token_offered": true } }
```

### 66.5 Memory budget on Pi 4 (4 GB)

Updated 2026-05-23 per §71 Native AOT decision — runtime baseline dropped from ~200 MB (JIT + ICU + full BCL) to ~50 MB (AOT, no JIT, InvariantGlobalization).

| Component | RAM | Notes |
|---|---|---|
| .NET runtime baseline (AOT) | ~50 MB | Native AOT publish per §71; no JIT, no ICU |
| Image processor working set | ~100 MB | 1 worker × FITS + intermediate Mats |
| Image processor queue | ~50 MB | Worst case: 10 jobs × ~5 MB Alpaca readout buffer (FITS itself written via atomic-rename, not held in memory) |
| Alt-stretch working buffers | ~100 MB | OpenCvSharp4 temp Mats |
| SQLite mmap | ~256 MB | Per §28.6 `mmap_size` |
| WS queues | ~10 MB | Bounded per §66.2 |
| PHD2 client | ~10 MB | Lightweight |
| Backup stream | ~50 MB | 1 frame buffered |
| Diagnostics worker | ~50 MB | 1 frame |
| Notification dispatch + REST handlers + logs | ~50 MB | Reduced from ~150 MB under JIT |
| **Estimated peak RSS** | **~600 MB** | Comfortable on 4 GB Pi; ~3.4 GB OS headroom |
| **Estimated idle RSS** | **~50 MB** | Boots and stays small when nothing is happening |

8 GB Pi 4 / Pi 5 has substantial additional comfort; 2 GB SBC variants (some Orange Pi / RockChip) now move from borderline to comfortable — DEPLOY.md still recommends 4 GB minimum for cooler-thermal-stable Pis but 2 GB works in a pinch.

### 66.6 Storage I/O cascade

USB SSD per §29 keeps `fsync` latency to 5–50 ms typical. If USB stick is used despite §29's warning, latencies can climb to 200 ms – 2 s, which propagates:

1. `fsync` latency rises above 1 s → image processor takes longer per job → queue depth rises
2. If queue depth stays > 5 for > 30 s → emit `storage.slow` warning notification ("Storage write speed is slow. Consider upgrading to USB 3.0 SSD.")
3. If queue ever hits 10 → capture backpressure fires (`capture.backpressure` WS event), exposures briefly pause until queue drains

This cascade is self-healing once the user upgrades storage; no permanent damage and no data loss thanks to §28.7's atomic-write pattern.

### 66.7 Telemetry surface

v0.0.1 ships **log-only** telemetry:
- Per-executor queue depth, throughput EWMA, backpressure event count written to the server log every 60 s during active session
- `capture.backpressure`, `storage.slow`, `diagnostics.frame_dropped`, `ws.client_disconnected` WS events for real-time visibility

v0.0.1 does **not** ship:
- `GET /api/v1/server/internal-state` endpoint exposing live executor metrics
- WILMA Stats "Pi performance" panel

Both are v0.1.0 paths. Rationale: under DSO workloads the backpressure events fire essentially never, so the operational value of live metrics is low; if a user does hit storage problems, the WS event + log entry is sufficient diagnosis. Adding the endpoint + panel is ~2 days of work that's better spent elsewhere in v0.0.1.

### 66.8 What's out of scope for v0.0.1

- Live performance dashboard in WILMA (deferred to v0.1.0 — `GET /api/v1/server/internal-state` + Stats "Pi performance" panel)
- Per-pool runtime tuning via API (`PATCH /api/v1/server/concurrency` to change worker counts on the fly) — fixed at startup in v0.0.1
- Dynamic priority adjustment under load (e.g., promoting capture to realtime priority on Pi 4 specifically) — fixed priorities
- Hot-reload of executor config without server restart
- High-frame-rate / planetary concurrency model — permanently out of scope per §18.J (no video API in Alpaca)

### 66.9 §61 search registry entries

Pool sizes + queue depths are not exposed as user-facing settings in v0.0.1 (fixed at the values above). No registry entries needed; if v0.1.0 adds runtime tuning per §66.8, those settings register at that time.

The only related entry needed in v0.0.1:

- `diagnostics.troubleshoot_storage_slow` — keywords: `storage slow, usb performance, fsync slow, capture pausing, backpressure, slow drive, upgrade ssd`

### 66.10 Performance SLO / latency-budget table (v0.0.1 aspirational)

Consolidates targets that exist scattered through the playbook into one reference. Two columns: **v0.0.1 target** (what we aim for on a Pi 4; what feels good in a session) and **hard limit** (degraded but acceptable — beyond this number, user experience suffers enough that we should investigate). All measurements are on Pi 4 with USB 3.0 SSD storage and AlpacaBridge + openastro-phd2 colocated unless noted; Pi 5 numbers are typically 1.5–3× better.

In v0.0.1, these are **aspirational** — no automated gating in CI. Phase 14 may add opt-in perf tests that record measurements without failing the build; v0.1.0+ may promote specific operations to hard CI gates once we have baseline data. Community contributors and the AI use the table as the design bar: anything materially worse than the target should be investigated; anything worse than the hard limit is a regression and should be fixed before merge.

**HTTP / WebSocket endpoints:**

| Operation | v0.0.1 target | Hard limit | Source / cross-ref |
|---|---|---|---|
| `GET /healthz` response | < 10 ms | 100 ms | §60.8 |
| `GET /readyz` response | < 100 ms | 500 ms | §60.8 |
| `GET /api/v1/server/state` (snapshot) | < 200 ms | 1 s | §60.4 |
| `POST /api/v1/sequences/start` accept | < 50 ms | 200 ms | §38 |
| WebSocket event server → client (LAN, 1 KB JSON) | < 100 ms | 500 ms | §60.9 |
| WebSocket connect + resume replay (100 events) | < 1 s | 5 s | §60.9 |
| Idempotency dedup check (cached) | < 5 ms | 50 ms | §60.5 |

**Image / capture pipeline:**

| Operation | v0.0.1 target | Hard limit | Source / cross-ref |
|---|---|---|---|
| FITS atomic write (16 MP frame, USB 3.0 SSD) | < 200 ms | 1 s | §28.7 |
| Frame → default-stretch preview JPEG (16 MP) | < 500 ms | 2 s | §65 |
| Alt-stretch generation (one variant, 16 MP, Pi 4) | < 200 ms | 2 s | §65, §66.5 |
| Alt-stretch generation (Pi 5) | < 100 ms | 1 s | §65 |
| Diagnostics enrichment per frame (HFR + star count + roundness) | < 300 ms | 1 s | §51 |
| Live View loop cadence (one frame) | 2–4 s | 8 s | §64 |
| Stretch slider debounce (server round-trip) | 200 ms | 500 ms | §65 |

**Equipment + integration:**

| Operation | v0.0.1 target | Hard limit | Source / cross-ref |
|---|---|---|---|
| AlpacaBridge call (typical, LAN-local) | < 100 ms | 1 s | §68 |
| AlpacaBridge handshake on connect | < 200 ms | 2 s | §68.1 |
| PHD2 JSON-RPC roundtrip | < 50 ms | 500 ms | §63 |
| PHD2 connect + initial state sync | < 2 s | 10 s | §63.2 |
| Mount `AbortSlew()` round-trip (Stop Mount) | < 100 ms | 1 s | §57 |
| Camera `AbortExposure()` (Live View stop) | < 100 ms | 500 ms | §64 |
| ASTAP plate solve (1500×1000 px, typical sky) | < 3 s | 10 s | §18.I |
| ASTAP plate solve (full 6000×4000 px, dense field) | < 10 s | 30 s | §18.I |

**Polar alignment + autofocus:**

| Operation | v0.0.1 target | Hard limit | Source / cross-ref |
|---|---|---|---|
| Polar-align loop (capture + solve + display, Pi 5) | < 500 ms | 1 s | §45 |
| Polar-align loop (Pi 4) | < 800 ms | 1.5 s | §45 |
| Smart Focus AF (calibrated, normal seeing) | 30–90 s | 5 min | §59 |
| Classic AF fallback (9-step curve fit) | 3–5 min | 10 min | §59 |
| Smart Focus calibration (per profile, one-time) | ~5 min | 10 min | §59 |

**Server lifecycle:**

| Operation | v0.0.1 target | Hard limit | Source / cross-ref |
|---|---|---|---|
| Server cold boot to `/readyz` 200 | < 5 s | 30 s | §71 (AOT helps), §60.8 |
| EF Core migration (no pending migrations) | < 100 ms | 500 ms | §28.14 |
| EF Core migration (typical small migration) | 1–5 s | 30 s | §28.14 |
| Startup orphan scan (1 GB capture dir) | < 10 s | 60 s | §28.8 |
| Server graceful shutdown (SIGTERM → exit) | < 5 s | 30 s | §28.7, §34.7 |
| Sequence lock heartbeat interval | 30 s | n/a | §34.7 |
| systemd watchdog interval | 30 s ping / 60 s deadline | n/a | §13 |

**Memory / resource (steady-state on Pi 4 4 GB):**

| Metric | v0.0.1 target | Hard limit | Source / cross-ref |
|---|---|---|---|
| Server idle RSS | ~50 MB | 200 MB | §66.5, §71 |
| Server peak RSS (active capture + diagnostics + WS + backup) | ~600 MB | 1.5 GB | §66.5 |
| .deb installed size | ~50 MB | 200 MB | §71 |
| `/var/log/openastroara/` 30-day footprint | 200–500 MB | 3 GB | §29.9 |
| Image processor queue depth | ≤ 5 jobs | 10 jobs (then backpressure) | §66.2 |

**WILMA-side (desktop):**

| Operation | v0.0.1 target | Hard limit | Source / cross-ref |
|---|---|---|---|
| App cold start to "Servers" menu | < 2 s | 10 s | §25 |
| Connect to known server (mDNS cached) | < 1 s | 5 s | §30 |
| §61 ⌘K search response (typing → results) | < 50 ms | 200 ms | §61 |
| Settings panel paint after navigation | < 100 ms | 500 ms | §25 |
| Image library scroll (full thumbnails, 1000 frames) | 60 fps | 30 fps | §40 |

**Methodology:**

- All targets assume a healthy network (LAN < 5 ms RTT), no concurrent OS-level pressure (no `apt upgrade` mid-measurement), and Pi 4 with quality USB 3.0 SSD (per §29 mandatory USB)
- "Hard limit" exceeded → file an issue + add a regression test; treat as a bug
- "Target" exceeded but under hard limit → investigate when convenient; no PR-blocking gate
- Phase 14 testing strategy may add opt-in perf-recording in CI that posts measurements as PR comments without failing the build — gives contributors visibility without nag-blocking

**v0.1.0 path:**

- Hard CI gates on the highest-leverage targets (`/healthz`, capture accept, WS event latency, polar-align loop)
- Per-Pi-model SLO variants (Pi 5 gets tighter targets; Orange Pi 5 / Rock Pi sensible defaults)
- User-facing "performance health" view in §50 Stats showing rolling p50/p95/p99 against the table
- Telemetry-driven SLO adjustment based on field measurements from opted-in users (per §18.C: local-only by default; v0.1.0 may add opt-in upload)

**§61 search registry entries:**

- `monitoring.performance_targets` — keywords: `performance, slo, latency budget, perf target, expected latency, how fast`
- `monitoring.troubleshoot_slow` — keywords: `slow server, slow capture, latency, lag, performance problem, slow pi`

---

## 67. Security model

ARA's security model matches **ASCOM Alpaca and ZWO ASIAir**: trusted-LAN deployment, **no authentication on the API**, no transport encryption in v0.0.1. This is a deliberate choice that reflects how astrophotography software is actually used.

### 67.1 Threat model

ARA users image from:
- Their own backyard (private LAN, often a single device)
- Remote observatories they own (private LAN, possibly behind a VPN)
- Dark sky party fields (shared LAN with other astrophotographers)

In every case the people on the network are imaging peers, not adversaries. Star parties have happened for decades with ASIAir, KStars/EKOS, NINA-with-Alpaca-over-LAN, SharpCap remote, and SkyPortal — **none of which authenticate the API**. No one is hacking imaging rigs in the wild. The imaging community is small, trusts each other, and the on-network blast radius (mess with someone's exposure) is too low to motivate attacks.

ARA inherits this posture:

| Threat | v0.0.1 protection | Notes |
|---|---|---|
| Casual API access on shared LAN | None | Same as Alpaca + ASIAir. Single-client policy (§27) provides session ownership UX but isn't auth. |
| Network sniffing on shared Wi-Fi | None | Same as Alpaca + ASIAir. No TLS in v0.0.1 (§2.3). |
| MITM attacks | None | Same as Alpaca + ASIAir. |
| Accidental reformat / wipe by another user | Label-echo confirmation (§29.1.3) | Destructive ops require typing the drive's exact label — protects against accidents, not malice |
| Malicious binary push to overwrite the server | SHA-256 verification (§33.4) | Binary integrity check is independent of auth; an attacker would need to upload a correctly-hashed binary, which requires already having the binary |

### 67.2 What protections still exist (defense without auth)

Several mechanisms reduce risk regardless of authentication:

- **mDNS discovery is client-initiated** — WILMA discovers servers; the user explicitly picks one. An attacker cannot push unsolicited commands to a WILMA client
- **Single-client policy (§27)** — only one WILMA controls the rig at a time. A second connection prompts the existing user to grant takeover; an opportunistic attacker can't silently snipe the session
- **Confirmation UX on destructive operations** — drive reformat (§29.1.3), polar align changes, emergency stops, sequence aborts all require explicit user actions; an attacker can't trigger them without UI interaction
- **SHA-256 verification on binary updates (§33.4)** — a malicious binary push must produce the correct SHA-256, which requires either possessing the legitimate binary or breaking SHA-256
- **No remote access by default** — ARA binds to LAN interfaces only. Reaching ARA from outside the LAN requires the user to set up port forwarding or VPN themselves
- **Local-logs-only telemetry (§18.C)** — no network calls leave the Pi unprompted; nothing for an attacker to spy on en route

### 67.3 User responsibility (deployment recommendations)

DEPLOY.md (per §34.6) documents:

- **Home network**: trust your own LAN. Standard deployment.
- **Star party / shared LAN**: same posture as ASIAir. Standard deployment is fine. If you want isolation, run the Pi in AP mode (§32.6) so only your devices connect, or use the Pi's Ethernet interface with a dedicated cable.
- **Public Wi-Fi (coffee shop, airport, hotel)**: don't. Image on private networks.
- **Remote observatory access (over internet)**: out of scope for v0.0.1 — use a VPN. v0.1.0 may add an opt-in remote-access mode with TLS + token auth.

### 67.4 v0.1.0 remote-access mode (deferred)

When users want to image from a remote observatory over the internet, the open-LAN model breaks down — the internet has actual adversaries. v0.1.0 adds an opt-in **remote-access mode**:

- TLS termination (Let's Encrypt or self-signed)
- Token authentication (re-introduces the auth that v0.0.1 dropped)
- Rate limiting + optional IP allowlist
- Binds on a separate interface so internal LAN access stays unauthenticated

Remote-access mode is **opt-in** — users explicitly enable it in Settings. Default deployment stays unauthenticated.

### 67.5 What ARA does NOT do (anti-features in v0.0.1)

- **No authentication** — explicitly removed per §67. This is the whole point.
- **No transport encryption** — no TLS in v0.0.1 (§2.3); v0.1.0 with remote-access mode
- **No audit logging** — events go to log files but aren't designed for security forensics
- **No anti-virus or behavior detection** — out of scope
- **No security scanning of equipment drivers** — ASCOM Alpaca trusts drivers; ARA inherits that
- **No code signing in v0.0.1** (per §18.F) — ships unsigned for cost reasons

### 67.6 If a security issue is found

Responsible disclosure: private email rather than public GitHub issue. Channel TBD on the OpenAstro wiki (`security@open-astro.dev` placeholder). v0.0.1 has no formal bug bounty.

The §54 bug report flow's redaction list still scrubs hostnames, paths, internal IPs, and any tokens that may exist (in case v0.1.0 or future deployments do use auth and a user submits a v0.1.0 bug report from a remote-access-mode deployment) — defense in depth.

### 67.7 §61 search registry

- `security.deployment_recommendations` — keywords: `security, network safety, star party, public wifi, vpn, remote access, threat model, alpaca security, asiair security`
- `security.no_authentication` — keywords: `auth, authentication, token, password, login, why no password, secure server`
- `security.remote_access_v010` — keywords: `internet access, remote imaging, tls, auth, token, https, over internet, vpn alternative`

---

## 68. AlpacaBridge integration contract

ARA Core depends on [AlpacaBridge](https://github.com/AlpacaBridge) as the canonical equipment hub — it bridges ASCOM COM drivers (Windows) and direct USB drivers to standard ASCOM Alpaca, exposing devices over HTTP that ARA consumes. ARA does NOT re-document the ASCOM Alpaca protocol; for the authoritative protocol reference see [ascom-standards.org/api/](https://ascom-standards.org/api/) and the Alpaca DeviceAPI specification.

This section specs only the AlpacaBridge-specific assumptions ARA makes (minimum version, handshake, missing-bridge UX, upgrade path) — everything else is "whatever ASCOM Alpaca says."

### 68.1 Minimum version + handshake

**Minimum supported version:** `alpaca-bridge >= 1.2.0`. Pinned in the .deb's `Recommends` (per §34.2) and verified at runtime via handshake.

**Handshake on connect** (runs in §63.2-style state machine, every time ARA's equipment layer establishes a connection to AlpacaBridge):

1. **Discovery.** Either auto-discover via Alpaca's standard UDP broadcast on port 32227 (default) or use the static address user entered in wizard Screen 2 (§37.3).
2. **Probe `/version`.** ARA fetches `GET <bridge>/version` (AlpacaBridge-specific endpoint, not standard Alpaca). Expected response:
   ```json
   { "alpaca_bridge_version": "1.2.3", "alpaca_api_version": "1", "build": "..." }
   ```
3. **Version gate.**
   | Version | Behavior |
   |---|---|
   | `>= 1.5.0` | Accept; full feature support. |
   | `>= 1.2.0` and `< 1.5.0` | Accept with warning. WILMA's Equipment screen shows non-modal banner: "AlpacaBridge 1.x.x detected — version 1.5.0+ recommended. [Update]". Banner dismissible per-session. |
   | `< 1.2.0` | Refuse. Block the Equipment screen entirely; show modal: "AlpacaBridge version 1.x.x is too old. ARA requires 1.2.0 or newer. [How to update]". Equipment-dependent features (capture, mount control, focuser, etc.) all return 503 with `code: "alpaca_bridge_outdated"` until upgrade. |
   | `/version` unreachable / non-JSON / missing field | Treat as missing AlpacaBridge per §68.2. |
4. **Capability listing.** ARA calls standard Alpaca `GET /management/v1/configureddevices` to learn what devices the bridge exposes. ARA does NOT assume any specific devices are present — per-device capabilities are read from each device's standard Alpaca interface (`CanCool`, `CanSetCCDTemperature`, `CanPark`, etc.). All graceful-degrade behavior is per the ASCOM Alpaca spec, not ARA-specific.

The handshake completes in < 200 ms on a healthy Pi-local AlpacaBridge. Cached for the duration of the session; re-runs on every reconnect.

### 68.2 Missing AlpacaBridge UX

If AlpacaBridge isn't reachable at all (no `/version` response, no devices discoverable, connection refused):

- **Wizard Screen 2** (§37.3 — Connect to AlpacaBridge) shows the install command prominently:
  ```
  AlpacaBridge not detected.

  AlpacaBridge is ARA's equipment hub. It should have been installed
  alongside ARA Core via apt. If it wasn't, install it now:

      sudo apt install alpaca-bridge

  Then click [Retry detection].
  ```
- Wizard cannot advance past Screen 2 without a successful handshake. User can [Skip to manual entry] only if they need to point to a non-standard AlpacaBridge install (different host, custom port).
- After first-run setup, if AlpacaBridge goes missing (uninstalled, service stopped), WILMA's Equipment screen shows a critical notification with the same install command + `systemctl status alpaca-bridge` diagnostic snippet.

### 68.3 Upgrade path

**v0.0.1:** apt-managed. ARA Core's .deb lists `alpaca-bridge` in `Recommends`, so `apt install openastroara-server` pulls it in by default. Updates flow naturally via `sudo apt upgrade`. Subject to the apt-mid-session safety policy in §34.7 — AlpacaBridge restart during a capture is gated by the same lock file mechanism (the sequence.lock check covers any service that integrates with ARA mid-flight).

**v0.1.0:** WILMA-pushed updates per §33.6 — bundled AlpacaBridge binary streamable from WILMA, atomic-swap + rollback, no internet on the Pi required.

### 68.4 §61 search registry entries

- `equipment.alpacabridge.version` — keywords: `alpaca bridge version, equipment hub version, update alpaca bridge, alpaca bridge outdated`
- `equipment.alpacabridge.troubleshoot` — keywords: `alpaca bridge missing, equipment not found, alpaca bridge not detected, install alpaca bridge, equipment hub down`

### 68.5 §14.5 integration test cases (added)

- `alpacabridge_version_below_minimum_blocks_equipment` — start ARA with mocked AlpacaBridge returning `/version` 1.1.0 → assert Equipment endpoints return 503 with `code: "alpaca_bridge_outdated"`
- `alpacabridge_version_in_warn_band_emits_banner` — mocked AlpacaBridge 1.3.0 → assert connect succeeds + `equipment.alpaca_bridge_outdated_warn` notification queued
- `alpacabridge_missing_blocks_wizard_advance` — wizard Screen 2 with no AlpacaBridge running → assert [Next] is disabled + install instructions visible

### 68.6 Cross-references

- §2 — target stack lists AlpacaBridge as required dep
- §34.2 — .deb Recommends pulls AlpacaBridge in
- §37.3 — wizard Screen 2 wires the user-visible side of the handshake
- §52 — Alpaca-only commitment (architectural rationale for the dependency)
- §33.6 + §55 — v0.1.0 WILMA-push extension
- §34.7 — apt-mid-session safety covers AlpacaBridge restart during sequence

---

## 69. In-app contextual help — tooltip registry

§61 makes settings searchable; this section makes individual *controls* explainable. The two registries are deliberately parallel — same enforcement model, same discoverability pillar (§0.5), distinct domains: §61 answers *"how do I find the setting for X?"*; §69 answers *"what does this control actually do?"*

### 69.1 Scope — what gets a help entry

Help is opt-in per control. The default is **no tooltip** — most controls' labels are self-explanatory and adding ? icons to every field produces visual noise without information value. A help entry is appropriate when:

- The label can't carry the full meaning without becoming verbose ("Pulse guide duration ms" — what's a reasonable value? Why would you change it?)
- The control's effect is non-obvious or has hidden side effects (toggling Smart Focus changes the entire focus pipeline behavior, not just one knob)
- The setting interacts with other settings in ways that aren't visible in the panel
- A novice user would not know whether to enable it
- A power user might want to know the underlying algorithm

A help entry is **not** appropriate when:

- The label is self-explanatory ("Camera connection address" — the field name says it all)
- The setting's effect is fully captured by its current value (a slider showing "Exposure: 60 s" needs no help)
- The control is a display-only status indicator (use a `?`-icon-style affordance only if there's a meaningful explanation; otherwise nothing)

When in doubt: **omit the help entry**. The cost of a missing helpful tooltip is one user reading the wiki; the cost of a help icon on every control is the visual sprawl that NINA suffers from.

### 69.2 Help registry — `lib/help/registry.dart`

Parallel structure to §61's settings registry. Single source of truth for all in-app help content.

```dart
// client/openastroara_client/lib/help/registry.dart

class Help {
  final String key;            // unique identifier, dotted path (mirrors §61 setting IDs where applicable)
  final String title;          // short header for the tooltip + modal
  final String body;           // 1-3 paragraph explanation, Markdown supported
  final String? learnMoreUrl;  // optional deep link to wiki at openastro.net/wiki/...
  final List<String> relatedHelpKeys;  // cross-links to related entries
  final List<String> relatedSettings;  // cross-links into §61 registry by setting ID
}

const Map<String, Help> helpRegistry = {
  'guider.use_advanced_algorithms': Help(
    key: 'guider.use_advanced_algorithms',
    title: 'Advanced guiding algorithms',
    body: '''
When enabled, PHD2 uses the predictive PEC algorithm instead of
the default hysteresis controller. Predictive PEC tracks periodic
error in your mount's worm gear and feeds corrections forward,
reducing RA RMS by 30-60% on mounts with smooth, repeatable PE.

Recommended **on** for: belt-driven mounts, strain-wave mounts after
PEC training, GEMs with characterized periodic error.

Recommended **off** for: mounts you haven't trained, mounts with
stiction or sticking, debugging poor guiding (rule out the algorithm
before tuning it).
''',
    learnMoreUrl: 'wiki/guiding/predictive-pec',
    relatedHelpKeys: ['guider.aggressiveness', 'guider.min_move'],
    relatedSettings: ['guider.aggressiveness_ra', 'guider.aggressiveness_dec'],
  ),

  // ... more entries
};
```

### 69.3 Widget integration

Every Flutter control that carries help references it via `helpKey`:

```dart
SwitchListTile(
  title: const Text('Use advanced guiding algorithms'),
  value: profile.guider.useAdvanced,
  onChanged: (v) => ...,
  secondary: HelpIcon(helpKey: 'guider.use_advanced_algorithms'),
)
```

`HelpIcon` is a reusable widget:

- Renders as a small ⓘ glyph (consistent with §53 a11y's symbol-+-color convention — has a `Semantics(label: 'Help')` wrapper)
- On hover (desktop) or long-press (mobile/touch): shows tooltip with `Help.title` + first sentence of `body`
- On tap/click: opens a modal sheet with full `body` (Markdown rendered), [Learn more] button if `learnMoreUrl` is set, and a "Related" section listing `relatedHelpKeys` and `relatedSettings` as clickable chips (jump to that help entry or deep-link to the setting per §61)
- Modal sheet is dismissible; never blocks
- Modal width caps at 600 px to avoid line-length issues on wide screens

Controls without a `helpKey` render no icon — the default is silence.

### 69.4 Enforcement gate (mirrors COMMIT-PR-RULES.md §61 settings-registry gate)

Same defense-in-depth four layers, applied to the help registry:

**Layer 1 — Local pre-commit hook** (`.husky/pre-commit`):
```bash
node scripts/check-help-registry.mjs --staged
```

The script scans the diff for widgets that use `helpKey:` and verifies each referenced key exists in `lib/help/registry.dart` with non-empty `title` + non-empty `body`. Fails commit on:
- Widget references a `helpKey` that doesn't exist in the registry
- Registry entry has empty `title`, empty `body`, or `body` < 50 chars (too terse to be useful)
- Duplicate `key` across registry entries
- `learnMoreUrl` doesn't start with `wiki/` or `https://openastro.net/`

**Layer 2 — CI check** (GitHub Actions): same script runs against PR diff.

**Layer 3 — PR template checkbox** (`.github/PULL_REQUEST_TEMPLATE.md`):
```markdown
## Help registry (mandatory checkbox if applicable)

- [ ] This PR adds NO new controls that reference helpKey (skip remaining boxes)
- [ ] This PR adds controls with helpKey AND all referenced keys are registered in `lib/help/registry.dart` with meaningful title + body (50+ chars)
- [ ] Body text passes the "novice user reading this would know whether to use the setting" test
- [ ] Cross-links via `relatedHelpKeys` / `relatedSettings` added where applicable
```

**Layer 4 — CodeRabbit review focus** (`.coderabbit.yaml`):
```yaml
path_instructions:
  - path: "client/openastroara_client/lib/help/registry.dart"
    instructions: |
      Verify every Help entry has a body that would help a novice user decide
      whether to use the setting. Flag entries where body just restates the label
      or refers to "this setting" without explaining WHAT it does. Body should
      explain effect, recommendation (when to enable / when not to), and any
      surprising interactions with other settings.
  - path: "client/openastroara_client/lib/screens/**"
    instructions: |
      For widgets that use helpKey, verify the key exists in lib/help/registry.dart.
      For widgets that DON'T use helpKey but feel like they would benefit from one
      (non-obvious effect, novice would be confused), suggest adding a help entry.
      Don't suggest help entries for obvious labels (e.g., "Camera address" needs none).
```

### 69.5 Per-screen "Learn more" link (complementary to per-control help)

Independent of per-control help, every major screen header includes a single `[Learn more]` link in the top-right:

```
┌──────────────────────────────────────────────────┐
│ Sequencer                       [Learn more ↗]   │
├──────────────────────────────────────────────────┤
│ ... screen content ...                            │
```

This links to the relevant openastro.net wiki section at a per-screen anchor (e.g., `wiki/wilma/sequencer`). For *workflow* and *concept* explanations (what is a meridian flip? why dither? how does session metadata work?) that don't belong attached to any one control. Always opens in the user's default browser via `url_launcher`.

The per-screen link is unconditional and always present; the per-control help icons are opt-in per the §69.1 scope rules.

### 69.6 Coverage during the port itself

Same activation gate as the settings registry per COMMIT-PR-RULES.md:

- Phases 0.5–11 — no Flutter UI yet; rule does not apply
- **Phase 12 — gate is live.** Phase 12 sub-PRs that add controls must register any help they reference
- Phase 12 sub-PR 12h (Settings) is the natural home for the registry's initial bulk-population — every help entry referenced by earlier 12a-12g sub-PRs is consolidated in 12h alongside the §61 registry

### 69.7 §61 search registry entries

The help registry surfaces itself via §61 search (searching for "guiding" should surface both the *setting* and the *help entry*):

- `app.help.search` — keywords: `help, what does this do, explain, learn more, tooltip, how does this work`
- `app.help.disable_tooltips` — keywords: `hide tooltips, disable help icons, less visual clutter, stop popups`

(The `disable_tooltips` setting lets power users globally suppress the ⓘ icons app-wide once they've learned the UI. Persisted in user prefs; defaults to `show`.)

### 69.8 §14.2 widget test cases (added)

- `help_icon_renders_when_helpkey_present`
- `help_icon_absent_when_helpkey_unset`
- `help_modal_opens_on_tap_with_body_content`
- `help_modal_related_chips_navigate_to_settings` — chip click triggers §61 deep-link
- `disable_tooltips_setting_hides_all_icons_globally`

### 69.9 Cross-references

- §0.5 — discoverability pillar (search for settings via §61, explanations via §69, concepts via wiki)
- §25 — visual design (HelpIcon glyph + tooltip styling matches AraColors theme)
- §53 — a11y (HelpIcon has Semantics label; tooltip respects reduce-motion)
- §61 — settings search registry (parallel enforcement model)
- COMMIT-PR-RULES.md — help registry gate parallel to settings registry gate

---

## 70. Profile + sequence sharing between users

Distinct from §43 backup/restore (same-user disaster recovery) and from §38 NINA import (one-time format migration). This is the *"my friend has a similar rig and wants my settings"* flow — peer-to-peer file sharing of templates, not central infrastructure.

§38 already makes sequences shareable by construction (NINA-compatible JSON with no equipment-specific calibration baked in). §70 adds the profile side: an equipment-stripped export format + import flow that walks the recipient through wizard'ing their own gear into the donated template.

v0.0.1 ships file-based sharing only (email, USB stick, Discord attachments) — no central registry, no rating system, no curation. v0.1.0+ "OpenAstro Hub" is the bigger lift (see §55 roadmap).

### 70.1 What gets stripped — the share-vs-backup distinction

| Field | Backup (§43) | Share (§70) | Reason |
|---|---|---|---|
| Profile name | ✓ | ✓ | Donor's name is helpful context ("Bortle 4 wide-field setup") |
| Equipment **types** (camera model, focal length, filter wheel slot count, etc.) | ✓ | ✓ | Recipient needs to know what gear this template was designed for |
| Equipment **UUIDs / serial numbers / Alpaca device IDs** | ✓ | ✗ stripped | Donor's serial numbers don't match recipient's hardware |
| `calibration_state` block (per §30.7) | ✓ | ✗ stripped | Calibration is donor-rig-specific; reusing it on recipient's gear would be actively wrong |
| Filter offsets per-temperature curves | ✓ | ✗ stripped | Filter-stack-specific to donor; recipient must re-derive |
| PHD2 calibration vectors (per §63) | ✓ | ✗ stripped | Mount-specific |
| Dark library references | ✓ | ✗ stripped | Tied to donor's camera + sensor temp history |
| Sky data download manifest (§36 Data Manager state) | ✓ | ✗ stripped | Recipient picks their own surveys based on their FL |
| General settings (dither cadence, AF triggers, meridian-flip timing knobs, safety policy, diagnostic mode, etc.) | ✓ | ✓ | The interesting part of the donation — donor's tuning judgement |
| Sequence templates referenced by profile | ✓ | ✓ (bundled in share file) | Recipient gets the full picture |
| Custom keyboard shortcuts, WILMA UI prefs | ✓ | ✗ stripped | Per-user, not per-rig |
| Notes / comments fields | ✓ | ✓ optional (opt-out at export time) | Donor may have annotated rationale worth sharing |

### 70.2 Share file format — `profile-share-v1`

Bundled JSON with explicit schema versioning per §60-style conventions. Single self-contained file (not a zip — keeps email-attaching friction-free):

```json
{
  "schemaVersion": "profile-share-v1",
  "sharedAt": "2026-06-01T20:14:33Z",
  "sourceAraVersion": "0.0.1-ara.6",
  "donor": {
    "displayName": "Joey's C8 Setup",
    "comment": "Bortle 4 backyard, dew-prone summers"
  },
  "rigDescription": {
    "telescope_type": "SCT",
    "focal_length_mm": 2032,
    "aperture_mm": 203,
    "focal_ratio": 10.0,
    "reducer_in_use": true,
    "effective_focal_length_mm": 1280,
    "camera_make": "ZWO",
    "camera_model": "ASI2600MM Pro",
    "sensor_type": "mono",
    "pixel_size_um": 3.76,
    "has_cooler": true,
    "has_filter_wheel": true,
    "filter_wheel_slots": 7,
    "filter_names": ["L", "R", "G", "B", "Ha", "OIII", "SII"],
    "has_focuser": true,
    "focuser_type": "stepper",
    "has_guider": true,
    "guide_scope_focal_length_mm": 240,
    "mount_class": "GEM"
  },
  "settings": {
    "dither": { ... },
    "autofocus": { ... },
    "meridian_flip": { ... },
    "safety": { ... },
    "diagnostics": { ... }
  },
  "sequence_templates": [
    {
      "name": "LRGB DSO — donor's defaults",
      "schemaVersion": "openastroara-sequence-v1",
      ...
    }
  ]
}
```

Key shape choices:
- **`schemaVersion: "profile-share-v1"`** — distinct from `openastroara-sequence-v1` (§38) and from full-profile backup schema (§43). Import endpoint dispatches by schemaVersion.
- **`rigDescription`** uses *types* and *capabilities*, never serials or driver IDs. Tells the recipient "this template was designed for a 200mm SCT with a cooled mono CMOS" so they can judge applicability.
- **`settings`** is verbatim from the donor's profile, minus all calibration-state blocks (per §30.7's `calibration_state` schema). Everything that's purely user judgement passes through; everything derived from observation / hardware doesn't.
- **`sequence_templates`** bundles any sequence templates the profile references inline, so the recipient gets a complete working template without separate file shuffling. Each is full §38 schema.
- File extension: `.araprofile.json`. Magic prefix in the schemaVersion field makes it unambiguous on import.

### 70.3 Export flow

**WILMA UI:** Profile screen → `[...]` overflow menu → "Share profile (template)":

```
┌─ Share profile: "Joey's C8 Setup" ──────────────┐
│                                                  │
│ This creates a template that strips your        │
│ equipment-specific data so a friend can use it  │
│ as a starting point for their own setup.        │
│                                                  │
│ What's included:                                 │
│   ✓ General settings (dithering, autofocus,    │
│     meridian flip, safety, diagnostics)         │
│   ✓ Rig description (scope type, FL, camera    │
│     model — no serial numbers)                  │
│   ✓ 3 sequence templates                       │
│                                                  │
│ What's stripped:                                 │
│   ✗ Equipment serial numbers + Alpaca IDs      │
│   ✗ Calibration data (focus, guider, filter   │
│     offsets, dark library)                     │
│   ✗ Your sky data downloads                    │
│   ✗ WILMA UI preferences                       │
│                                                  │
│ Optional:                                        │
│   [✓] Include donor name + comment             │
│   Donor display name: [Joey's C8 Setup        ] │
│   Comment (optional):  [Bortle 4 backyard...  ] │
│                                                  │
│              [Cancel]  [Export to file...]      │
└──────────────────────────────────────────────────┘
```

`[Export to file...]` triggers a platform-native save dialog (Flutter `file_picker`) defaulting to `<profile_name>.araprofile.json`. File is written via `POST /api/v1/profiles/{id}/share-export` which returns the rendered JSON (server does the stripping; WILMA just hands it to the file picker). Server logs the export action (donor name + timestamp) at Info level so the donor has a record.

**The "include donor name" toggle:** opt-out for users who want to share anonymously. Defaults checked because attribution is socially useful, but users sharing to public forums may prefer to strip.

### 70.4 Import flow — "wizard the template"

**WILMA UI:** Profiles screen → `[+ New profile]` → option "From shared template":

```
┌─ Import shared profile ────────────────────────┐
│                                                 │
│ Select an .araprofile.json file...             │
│                          [Browse...]            │
└─────────────────────────────────────────────────┘
```

After file selection, the share is parsed + validated server-side via `POST /api/v1/profiles/share-import {file_contents}` which returns a preview:

```
┌─ Imported template: "Joey's C8 Setup" ─────────┐
│                                                 │
│ Shared by: Joey                                 │
│ Note: "Bortle 4 backyard, dew-prone summers"   │
│ Exported: 2026-06-01 from ARA 0.0.1-ara.6      │
│                                                 │
│ ⚠ This is a template, not a complete profile.  │
│   You'll need to wizard your own equipment     │
│   to make it usable.                            │
│                                                 │
│ Original rig:                                   │
│   • Telescope: SCT, 2032 mm @ f/10             │
│     (with reducer: 1280 mm)                     │
│   • Camera: ZWO ASI2600MM Pro (mono)            │
│   • Filter wheel: 7-slot mono (L R G B Ha OIII │
│     SII)                                        │
│   • Mount: GEM                                  │
│                                                 │
│ Your equipment vs. this template:               │
│   Telescope: SCT @ 2032mm vs. yours           │
│              [Compatible — same scope type ✓]  │
│   Camera:    Mono CMOS vs. yours              │
│              [Compatible — same sensor type ✓]  │
│                                                 │
│ Settings to import:                             │
│   ✓ Dither, autofocus, meridian flip, safety,  │
│     diagnostics tuning                          │
│   ✓ 3 sequence templates                       │
│                                                 │
│ Next steps after import:                        │
│   1. Wizard's "Use existing template" path     │
│   2. You fill in your equipment serial         │
│      numbers + Alpaca addresses                 │
│   3. Calibration runs on first session         │
│                                                 │
│           [Cancel]  [Import + start wizard]    │
└─────────────────────────────────────────────────┘
```

**Compatibility check (best-effort).** Server compares the template's `rigDescription` against equipment WILMA has previously seen (any profile this user has set up). For each category (telescope type, camera sensor type, filter wheel slot count, mount class), it emits one of:
- **Compatible ✓** — types match; template should apply cleanly
- **Different ⚠** — types differ but template is still usable; donor's settings may need re-tuning (e.g., SCT vs refractor — focus algorithm choice may differ)
- **Major mismatch ⛔** — fundamental incompatibility (e.g., template designed for mono w/ filter wheel; recipient has OSC and no filter wheel) — settings will import but several will need manual review

The check is informational, never blocking. User decides whether to import.

**`[Import + start wizard]` flow:**
1. Server creates a draft profile from the template's settings, leaves all equipment fields empty
2. WILMA jumps to wizard (§37) at Screen 1 (Welcome) with the draft profile pre-loaded
3. Wizard runs in "use existing template" mode — equipment screens (2–14) ask the user to wire up their gear; settings screens (15–18) show the donor's values pre-filled with a small "from template" badge next to each (user can change before continuing)
4. On wizard completion, profile is saved + calibration_state initialized empty (per §30.7) so first-session-with-this-profile triggers the equipment-change check and runs fresh calibration
5. Donor name + comment surface in the profile metadata sidebar ("Imported from Joey's C8 Setup template — 2026-06-01")

### 70.5 Sequence sharing — already covered, formalize the button

Sequences are portable as-is per §38 (NINA-compatible JSON, no equipment-specific calibration). v0.0.1 adds the explicit share affordance:

**Sequencer screen → `[...]` menu:**
- Save sequence
- Save as template
- **Share sequence (.araseq.json)** ← new

`POST /api/v1/sequences/{id}/share-export` returns a self-contained JSON bundling:
- The sequence itself (full §38 schema, `schemaVersion: "openastroara-sequence-v1"`)
- All referenced sequence templates (inlined)
- Donor name + comment (optional, same toggle as profile share)
- Source ARA version

File extension `.araseq.json`. Import goes through the existing §38.4 import endpoint, which already handles equipment-remap + unsupported-instruction warnings — no new flow needed beyond a [Browse for .araseq.json file] entry point in the Sequencer screen.

### 70.6 v0.1.0+ — OpenAstro Hub (deferred)

Per §55 roadmap, v0.1.0+ adds central infrastructure:

- **openastro.net/hub/profiles** — browseable catalog of community-shared profile templates with filters (rig class, FL range, sensor type, Bortle target)
- **openastro.net/hub/sequences** — same for sequence templates
- **WILMA browse + import in-app** — Settings → Hub → Browse Templates → preview + one-click import (which then runs the same wizard'ing flow as §70.4)
- **Contributor model** — submitter accounts on openastro.net, moderation, ratings, comment threads, "tested by" badges
- **Curated starter packs** — official "Beginner — small refractor + DSLR", "Intermediate — APO + cooled CMOS", "Advanced — RC + filter wheel + AO unit" templates published by the Open Astro maintainers
- **Hub-side validation** — submissions go through a `validate-share-file` server that enforces schemaVersion + scope-type-coherence + no-PII checks before publishing

The §70 v0.0.1 file format (`profile-share-v1`, `araseq.json`) is the on-disk wire format the Hub later wraps in catalog metadata — no breaking changes when the Hub ships.

### 70.7 API + endpoints

| Endpoint | Method | Purpose |
|---|---|---|
| `/api/v1/profiles/{id}/share-export` | POST | Body: `{ include_donor_name: bool, donor_display_name: string?, comment: string? }`. Returns the rendered share JSON (server strips equipment-specific fields). |
| `/api/v1/profiles/share-import` | POST | Body: `{ file_contents: string }`. Parses + validates + returns preview JSON (compatibility check + settings summary). Does NOT create the profile yet — that happens via `share-import/commit`. |
| `/api/v1/profiles/share-import/commit` | POST | Body: `{ preview_id: string }`. Creates the draft profile from the preview, returns the profile ID. WILMA then redirects to wizard. |
| `/api/v1/sequences/{id}/share-export` | POST | Same shape as profile-share, scoped to sequences + their referenced templates. |
| `/api/v1/sequences/share-import` | POST | Existing §38.4 endpoint accepts `.araseq.json` (recognized via schemaVersion field); no new endpoint needed. |

### 70.8 §61 search registry entries

- `profile.share.export` — keywords: `share profile, export template, send to friend, share my setup, donate template, profile template, share rig`
- `profile.share.import` — keywords: `import template, friend's profile, use someone's profile, shared template, .araprofile, import setup`
- `sequence.share.export` — keywords: `share sequence, export sequence, send sequence, .araseq, sequence template share`
- `sequence.share.import` — keywords: `import sequence, friend's sequence, sequence from someone`

### 70.9 §14 test cases

**§14.1 server integration tests:**
- `profile_share_export_strips_calibration_state`
- `profile_share_export_strips_equipment_uuids`
- `profile_share_export_includes_general_settings`
- `profile_share_import_rejects_wrong_schemaVersion`
- `profile_share_import_compatibility_check_flags_mono_to_osc_mismatch`
- `sequence_share_export_bundles_referenced_templates`

**§14.2 widget tests:**
- `share_profile_dialog_shows_strip_summary`
- `share_profile_donor_name_toggle_persists_setting`
- `import_share_preview_renders_compatibility_badges`
- `import_share_commit_redirects_to_wizard_with_draft_profile`

### 70.10 Cross-references

- §30.7 — `calibration_state` block is the canonical list of what gets stripped at export
- §37 — wizard's "use existing template" mode is the import flow's landing destination
- §38 — sequence file format is the basis for sequence sharing
- §43 — backup/restore (same-user disaster recovery — keep distinct from §70's peer sharing)
- §55 — v0.1.0+ OpenAstro Hub roadmap entry
- §61 + §69 — search + help registries surface the share/import affordances
- §67 — security model (no auth in v0.0.1; share files are user-mediated, no server-to-server trust assumed)

---

## 71. Native AOT compilation — server build mode

The .deb-installed ARA Core server publishes as **.NET Native AOT** (ahead-of-time native code, no JIT, no IL runtime). The decision was made for Pi 4 startup speed + memory headroom — a fully AOT-compiled binary boots in ~100 ms and idles at ~50 MB RSS vs the ~1-2 s startup + ~150-200 MB RSS of a plain JIT publish. On a 4 GB Pi 4 that runs ARA + openastro-phd2 + AlpacaBridge + system services, the headroom matters.

This decision propagates as a constraint discipline across every server-side library choice and every code pattern that touches runtime reflection.

### 71.1 What AOT requires

The `OpenAstroAra.Server.csproj` sets:

```xml
<PropertyGroup>
  <PublishAot>true</PublishAot>
  <InvariantGlobalization>true</InvariantGlobalization>
  <TrimmerSingleWarn>false</TrimmerSingleWarn>
  <SuppressTrimAnalysisWarnings>false</SuppressTrimAnalysisWarnings>
</PropertyGroup>
```

`InvariantGlobalization=true` drops the ICU library (~30 MB savings) — ARA is English-only per §18.E so locale-sensitive comparisons aren't needed. UTC throughout per §31.5 means no timezone math relies on ICU.

**No runtime reflection or code generation.** That rules out:
- `System.Reflection.Emit` (no dynamic IL)
- Reflection-based serialization (must use source-generated `JsonSerializerContext`)
- Reflection-based DI containers (Autofac, Castle.Windsor) — stick with `Microsoft.Extensions.DependencyInjection`
- Reflection-based ORMs that don't have source-gen variants
- Reflection-based validators (FluentValidation's reflection mode — must use compiled validators or hand-written)
- `Activator.CreateInstance(Type)` patterns without `[DynamicallyAccessedMembers]` annotations
- Dynamic assembly loading (rules out the v0.1.0 plugin SDK in its current sketched form — see §71.6)

### 71.2 JSON serialization — source generators everywhere

Every DTO that crosses an API boundary or WS event boundary needs a `[JsonSerializable]` entry in a `JsonSerializerContext`-derived class. ARA Core ships a single root context that aggregates all serializable types:

```csharp
namespace OpenAstroAra.Server.Serialization;

[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(FrameDto))]
[JsonSerializable(typeof(SessionDto))]
[JsonSerializable(typeof(ProfileDto))]
[JsonSerializable(typeof(EquipmentStateDto))]
[JsonSerializable(typeof(NotificationDto))]
[JsonSerializable(typeof(WsEventEnvelope))]
[JsonSerializable(typeof(ProblemDetails))]  // §60.1 errors
// ... every public DTO
public partial class AraJsonContext : JsonSerializerContext { }
```

Usage in endpoint handlers:

```csharp
app.MapGet("/api/v1/frames/{id}", async (string id, AraDb db) =>
{
    var frame = await db.Frames.FindAsync(id);
    return Results.Json(frame, AraJsonContext.Default.FrameDto);
});
```

Build-time source-gen emits all serialization code; no reflection at runtime. The settings-registry-gate-style enforcement: any DTO returned from an endpoint must have a `[JsonSerializable]` entry, or the build fails (compiler error). No runtime surprise.

**Discipline cost:** every new DTO type touches `AraJsonContext`. ~30 seconds per DTO. Pre-commit gate (per §14.4) scans endpoint return types + verifies they're in the context — fails build before PR if missed.

### 71.3 OpenAPI spec generation — Microsoft.AspNetCore.OpenApi, not Swashbuckle

Swashbuckle (current de-facto Swagger generator) uses runtime reflection over MVC controllers to discover endpoints — incompatible with AOT.

ARA uses **Microsoft.AspNetCore.OpenApi** (built into ASP.NET Core 8+, AOT-compatible) which generates the OpenAPI document at build time via source generators. Swagger UI is served via the lightweight `Scalar.AspNetCore` package (also AOT-friendly) rather than Swashbuckle.UI.

```csharp
builder.Services.AddOpenApi();
// ...
app.MapOpenApi();              // /openapi/v1.json at runtime (cached, AOT-friendly)
app.MapScalarApiReference();   // /api/v1/docs UI
```

Endpoint metadata (descriptions, tags, response shapes) declared inline via `.WithName("GetFrame")`, `.WithDescription("...")`, `.Produces<FrameDto>(200, "application/json")`. §49 Swagger UI conventions carry over; the renderer changes from `swagger-ui` to Scalar's renderer (visually similar, ASCOM-ecosystem-friendly).

**Phase 5 implication:** the OpenAPI generation approach is **code-first via endpoint metadata** — the source code is the source of truth; `openapi.yaml` is the generated artifact (committed for change tracking + Dart client generation, regenerated on every build, CI fails if regen produces a diff against committed version). Closes Tier-1 gap #3 from the fourth review pass.

### 71.4 EF Core migrations — compiled models + source-gen migrations

§28.14 specs EF Core for SQLite schema management. AOT requires:

```xml
<PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" />
<PackageReference Include="Microsoft.EntityFrameworkCore.Design" PrivateAssets="all" />
```

`AraDbContext` uses `[DynamicallyAccessedMembers]` annotations on entity types + a **compiled model** generated at build time:

```bash
# Generate compiled model (run after model changes; commit the output)
dotnet ef dbcontext optimize --output-dir CompiledModels --namespace OpenAstroAra.Data.CompiledModels
```

```csharp
public class AraDbContext : DbContext
{
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder
            .UseModel(AraDbContextModel.Instance)  // compiled-model entry point
            .UseSqlite(connectionString);
    }
}
```

Migrations are still generated via `dotnet ef migrations add` — the migration files are pure C# code (no reflection at runtime). `Database.MigrateAsync()` on startup works under AOT because EF Core's migration runner uses the compiled model + source-gen.

**One subtle gotcha:** EF Core's automatic SQL query translation uses some reflection. For complex LINQ queries, prefer raw SQL via `db.Database.ExecuteSqlRaw` or `db.Frames.FromSql(...)` when the query is hot-path or complex. Simple queries (`db.Frames.Where(f => f.SessionId == id)`) work fine — the AOT toolchain warns about anything it can't translate at compile time.

§28.14 §14.1 test cases extended:
- `migrations_apply_under_aot` — run `dotnet publish -c Release` then `./OpenAstroAra.Server` + verify all migrations apply
- `linq_query_aot_compatible` — every repository method's LINQ verified at compile time via trimmer warnings (CI fails on any new IL2026/IL2104/IL3050 warnings)

### 71.5 Dependency injection + middleware

The built-in `Microsoft.Extensions.DependencyInjection` container is AOT-compatible. No need for Autofac, Castle, SimpleInjector, etc.

Services registered explicitly (no auto-scan-and-register patterns):

```csharp
builder.Services.AddSingleton<IFrameRepository, FrameRepository>();
builder.Services.AddSingleton<ISessionService, SessionService>();
builder.Services.AddScoped<ICaptureOrchestrator, CaptureOrchestrator>();
// ... etc
```

Middleware works normally (AOT-compatible). Hosted services (background workers per §66) work normally.

### 71.6 Plugin SDK (v0.1.0) — AOT constraint propagates

§10 + §55.1 commit to a plugin SDK in v0.1.0. AOT rules out dynamic-load-of-arbitrary-DLLs-at-runtime — `Assembly.Load(string path)` doesn't work in a Native AOT executable.

The v0.1.0 design pass for plugins (already deferred) must pick an AOT-compatible model:
- **Option A:** plugins as separate processes communicating over a local UNIX socket / named pipe (process boundary; ARA Core stays pure AOT)
- **Option B:** plugins compiled into a per-build "with-plugins" variant — community-contributed plugins go through a curation + per-build-rebuild cycle
- **Option C:** drop dynamic plugins entirely; offer scripting hooks (Lua / Wasm) instead — Wasm runtimes are AOT-compatible and provide sandbox isolation

This decision is captured here as a constraint, deferred to the v0.1.0 plugin SDK design session per §55.1.

### 71.7 Build pipeline

The Phase 10 publish command (§13) becomes:

```bash
dotnet publish OpenAstroAra.Server \
  -c Release \
  -r linux-arm64 \
  -p:PublishAot=true \
  -p:InvariantGlobalization=true \
  -o ./publish/arm64
```

Cross-compilation from x64 to ARM64 works for AOT via the `Microsoft.DotNet.ILCompiler` cross-targeting package (auto-pulled by `-r linux-arm64`). CI matrix uses Linux ARM64 self-hosted runner OR x64 with QEMU + the cross-toolchain. Build time is ~3-5x longer than plain JIT publish (ILC + linker are slower than CSC) — acceptable given it runs per-tag, not per-commit.

**Pre-PR gate (§14.4)** adds an AOT-warning check: `dotnet publish -p:PublishAot=true` exit 0 + no IL2026/IL2104/IL3050 warnings. Suppressions require `[UnconditionalSuppressMessage]` with justification text — caught by CodeRabbit review (`.coderabbit.yaml` path instruction added).

### 71.8 §61 search registry entries

Operational surface for developers + curious users:

- `server.build.aot_mode` — keywords: `aot, native aot, ahead of time compilation, publish mode, build type, server runtime`
- `server.build.troubleshoot_trim_warnings` — keywords: `trim warning, aot warning, IL2026, IL3050, dynamicallyaccessedmembers, reflection error`

### 71.9 Cross-references — sections updated by this decision

- **§2** target stack — note "PublishAot=true" added to "Hosting" row
- **§5** Phase 1 — .NET 10 bump includes AOT-mode setup (Phase 0.5p / Phase 1 absorbed per §3)
- **§10 / §13** publish commands — add `-p:PublishAot=true -p:InvariantGlobalization=true`
- **§14.4** pre-PR gate — add AOT publish + warning check
- **§28.14** EF Core migrations — compiled-model requirement (subsection added per §71.4)
- **§49** Swagger UI — replace Swashbuckle with Microsoft.AspNetCore.OpenApi + Scalar.AspNetCore
- **§60** API conventions — OpenAPI generation mode (code-first via endpoint metadata; spec is a generated artifact)
- **§66** server concurrency — RSS budget revised: ~50 MB idle (was implicit ~150 MB), ~600 MB peak (was ~1 GB) — even more headroom on Pi 4
- **§10 / §55.1** plugin SDK — AOT constraints propagate (§71.6)

---

## 72. FITS library — P/Invoke wrap of cfitsio

ARA Core reads and writes FITS files via P/Invoke into **CFITSIO** ([heasarc.gsfc.nasa.gov/fitsio/](https://heasarc.gsfc.nasa.gov/fitsio/)), NASA's reference C implementation. Chosen for correctness — CFITSIO is the de-facto correct FITS implementation, used by every major astronomy tool. Tradeoffs: native dependency adds distribution + dev-setup steps; P/Invoke layer needs maintenance.

### 72.1 Why cfitsio over alternatives

| Option | Rejected because |
|---|---|
| In-house minimal writer | We'd own correctness for FITS header conventions, integer/float bytewidth handling, BSCALE/BZERO scaling, COMMENT/HISTORY card formatting. Downstream tools (PixInsight, Siril, Astrobin) have hard expectations; getting a corner case wrong silently corrupts data. CFITSIO has 30 years of validation against these. |
| nom.tam.fits port | 2-3 weeks of Phase 6/7 work for a comprehensive library where we'd use ~10%. Ongoing maintenance to track Java upstream. |
| CSharpFITS package | Last upstream commit ~2018; AOT compatibility unverified; .NET 10 compatibility unverified. Risk of discovering blockers late in Phase 6. |

### 72.2 Distribution

**Pi (.deb path):** add `libcfitsio10` to `Depends` in §34.2:

```
Depends: libc6, libgcc-s1, libstdc++6, libcfitsio10
```

`libcfitsio10` ships in Debian Trixie's repos — `apt install` pulls it transparently. No build step required on the Pi.

**Dev machines (Linux/macOS/Windows):**

- **Linux dev:** `sudo apt install libcfitsio-dev` (Debian/Ubuntu) or distro equivalent. CFITSIO version 4.x+.
- **macOS dev:** `brew install cfitsio` — installs to `/opt/homebrew/lib/libcfitsio.dylib` (Apple Silicon) or `/usr/local/lib/libcfitsio.dylib` (Intel).
- **Windows dev:** vcpkg recommended (`vcpkg install cfitsio:x64-windows`) or pre-built binary from heasarc. Path goes in `OPENASTROARA_CFITSIO_PATH` env var.

DEPLOY.md adds a "Development setup" section listing platform-specific install commands. README's developer-onboarding section links to it.

**CI matrix (per §14.3):** every runner (Linux x64, Linux ARM64, macOS, Windows) installs cfitsio in the setup step. Add to `.github/workflows/ci.yml`:

```yaml
- name: Install CFITSIO (Linux)
  if: runner.os == 'Linux'
  run: sudo apt-get install -y libcfitsio-dev
- name: Install CFITSIO (macOS)
  if: runner.os == 'macOS'
  run: brew install cfitsio
- name: Install CFITSIO (Windows)
  if: runner.os == 'Windows'
  run: vcpkg install cfitsio:x64-windows
```

### 72.3 P/Invoke wrapper layer

`src/OpenAstroAra.Image/Fits/` holds the wrapper. Uses `[LibraryImport]` source generators (AOT-compatible per §71):

```csharp
// src/OpenAstroAra.Image/Fits/CFitsIO.cs
using System.Runtime.InteropServices;

namespace OpenAstroAra.Image.Fits;

internal static partial class CFitsIO
{
    private const string LibraryName = "cfitsio";

    [LibraryImport(LibraryName, EntryPoint = "ffinit", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int CreateFile(out IntPtr fptr, string filename, out int status);

    [LibraryImport(LibraryName, EntryPoint = "ffopen", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int OpenFile(out IntPtr fptr, string filename, int mode, out int status);

    [LibraryImport(LibraryName, EntryPoint = "ffclos")]
    internal static partial int CloseFile(IntPtr fptr, out int status);

    [LibraryImport(LibraryName, EntryPoint = "ffcrim")]
    internal static partial int CreateImage(IntPtr fptr, int bitpix, int naxis, long[] naxes, out int status);

    [LibraryImport(LibraryName, EntryPoint = "ffppx")]
    internal static partial int WritePixels(IntPtr fptr, int datatype, long[] firstpix, long nelements, IntPtr buffer, out int status);

    [LibraryImport(LibraryName, EntryPoint = "ffpky", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int WriteKey(IntPtr fptr, int datatype, string keyname, IntPtr value, string comment, out int status);

    // ~20-25 functions total; the subset ARA actually uses
}
```

Platform-specific name resolution (handled automatically by .NET):
- Linux: `libcfitsio.so` → `libcfitsio.so.10`
- macOS: `libcfitsio.dylib`
- Windows: `cfitsio.dll`

If the OS can't find the library, ARA Core fails to start with a clear error: `LOG: Cannot load libcfitsio. Install via: sudo apt install libcfitsio10` (Linux) or platform-equivalent message. Error references the §72.2 install docs.

### 72.4 Managed wrapper layer

Above the raw P/Invoke, a typed managed API ARA code uses:

```csharp
// src/OpenAstroAra.Image/Fits/FitsImage.cs
namespace OpenAstroAra.Image.Fits;

public sealed class FitsImage : IDisposable
{
    public static FitsImage Create(string path, int width, int height, FitsBitDepth bitDepth) { ... }
    public static FitsImage Open(string path, FitsOpenMode mode = FitsOpenMode.ReadOnly) { ... }

    public IReadOnlyDictionary<string, FitsHeaderValue> Headers { get; }
    public void SetHeader(string key, object value, string? comment = null);

    public void WriteImageData(ReadOnlySpan<ushort> data);  // 16-bit data path
    public void WriteImageData(ReadOnlySpan<float> data);   // float path
    public ushort[] ReadImageData16();
    public float[] ReadImageDataFloat();

    public void Dispose();  // closes file handle
}
```

All ARA capture / library / diagnostic code uses `FitsImage` — never raw P/Invoke directly. Keeps the surface clean + testable + lets us swap the underlying library later if needed (e.g., move to in-house if cfitsio P/Invoke proves problematic).

### 72.5 Atomic write integration (§28.7)

`FitsImage.Create()` writes to `<path>.tmp` internally; `Dispose()` flushes + closes + atomically renames to the final path + fsyncs the directory. The §28.7 atomic-write pipeline is implemented inside `FitsImage` rather than scattered across call sites — guarantees no torn FITS under the real name even if callers forget the pattern.

```csharp
using (var fits = FitsImage.Create(framePath, w, h, FitsBitDepth.UnsignedInt16))
{
    fits.SetHeader("EXPOSURE", exposureSeconds, "Exposure time in seconds");
    fits.SetHeader("CCD-TEMP", sensorTemp, "CCD temperature (C)");
    fits.SetHeader("INSTRUME", cameraName, "Camera model");
    fits.WriteImageData(pixelBuffer);
}
// Dispose() runs atomic rename + fsync; if any step throws, .tmp is cleaned up
```

### 72.6 NINA-compatible header tags

ARA writes the same FITS header tags NINA writes, so downstream tools that recognize NINA's conventions (PixInsight scripts, Astrobin upload, Siril metadata extraction) work transparently with ARA-produced files. `AraFitsTags.cs` holds the canonical list inherited from NINA's `FitsHeader.cs` — equivalent to §17.2 fork hygiene (preserve NINA's externally-visible conventions for downstream tool compatibility).

### 72.7 §14 test cases

**§14.1 server integration tests:**
- `cfitsio_loads_on_startup` — assert `LoadLibrary` succeeds; fails with clear error on missing lib
- `fits_create_writes_valid_file_per_pixinsight_round_trip` — write FITS via ARA, read back via reference Python `astropy.io.fits`, assert all headers + pixel data match
- `fits_atomic_write_no_torn_file_on_crash` — start a FITS write, kill the process mid-write, assert no file appears under the real name (only `.tmp` remains, cleanup by §28.8 orphan scan)
- `fits_headers_preserve_nina_conventions` — fixture FITS from NINA, read via ARA, assert all standard tags (DATE-OBS, EXPOSURE, CCD-TEMP, INSTRUME, etc.) preserve verbatim

### 72.8 §61 search registry entries

- `image.fits.library` — keywords: `fits library, cfitsio, fits writer, fits reader, fits format`
- `image.fits.troubleshoot_missing_cfitsio` — keywords: `cfitsio not found, libcfitsio missing, cfitsio install, fits library not loaded`

### 72.9 Cross-references

- §17.2 — fork hygiene (NINA-compatible FITS conventions preserved for downstream tool compatibility)
- §28.7 — atomic FITS write pipeline (implemented inside `FitsImage`)
- §28.8 — startup orphan scan (cleans up stale `.tmp` files)
- §34.2 — .deb Depends adds `libcfitsio10`
- §40 — image library reads FITS via `FitsImage.Open`
- §51 — diagnostics enrichment writes additional header tags via `FitsImage.SetHeader`
- §65 — image stretching reads pixel data via `FitsImage.ReadImageData*`
- §71 — AOT compatibility maintained via `[LibraryImport]` source generators

---
