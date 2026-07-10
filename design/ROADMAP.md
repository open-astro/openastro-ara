# ROADMAP — the design path

> **What this is.** The single consolidated list of everything that remains to be built, verified,
> or decided in OpenAstro Ara — organized as a **design path** (by dependency and theme), not by
> release version. Created 2026-07-09 when version-based planning (v0.0.1 / v0.1.0 / v0.2.0
> buckets) was removed from the design docs (see `PORT_DECISIONS.md`, 2026-07-09 entry).
> The former playbook §55 roadmap, the non-✅ remainder of the playbook section checklist, the
> open half of `PORT_TODO.md`, and the deferred tails of the feature specs
> (`NEXTGEN_PLANNING.md`, `PHD2-GAP.md`, `TONIGHT_SKY.md`) all resolve into this one list.
>
> **How it's maintained.** Feature PRs strike or update their items here (same rhythm as the
> playbook checklist). Fine-grained review follow-ups keep being logged in `PORT_TODO.md` as they
> arise — the appendix (part 10) links there rather than duplicating its prose; when a PORT_TODO
> section fully closes, remove its appendix line here. § numbers refer to `PORT_PLAYBOOK.md`,
> as everywhere.
>
> **Ordering.** Parts 1–6 are sequenced by dependency (what unblocks what); part 7 is the
> feature backlog grouped by theme (order within a theme is not a commitment); parts 8–11 are
> verification passes, user-gated decisions, small follow-ups, and permanent non-goals.

---

## 1. In flight — hardware-fault surfaces epic (§42)

The epic currently being shipped, slice by slice (~~§42.4 slice 4c~~ shipped #793; ~~§42.5 fault
log + REST~~ shipped #795; ~~§42 WILMA surfaces~~ shipped #797/#798 — live fault chips w/ history
seeding, Imaging-tab fault feed, §42.6 per-session timeline + library badges):

- ~~**§42.6 `affected_frames` population**~~ — shipped: filled at resolve time (recovered action
  + reconnect hook), session-scoped exposure-window overlap against `[detected_at, resolved_at]`.
  **The §42 epic is complete.**
- ~~**§42.2 matrix, remaining rows**~~ — shipped #800/#801/#802/#803: op-failure enforcement,
  switch re-command + solver exit codes, rotator drift watch, persistent-op-fault escalation
  (mount abort+park, camera pause). Every buildable audit item done (see PORT_TODO's audit list).
- ~~**§42.3 generalized hot-reconnect**~~ — shipped: the 0/5/15/30/60 s ladder landed with the
  reaction service; this slice adds the post-give-up linger re-adopt (device comes back mid-run
  → re-adopted without user surgery, paused runs resumed) + the per-mediator
  disconnect-mid-wait regression harness owed from the #800 arc.

## 2. First public release (release-blocking engineering + external gates)

The only remaining *engineering* that blocks the first public release, plus the gates that are
not code:

- **§34 apt.openastro.net publish pipeline** (PORT_TODO "§34 apt.openastro.net publish pipeline"):
  - Repo publish job — reprepro/aptly on tag push per §34.5, laying out
    `dists/stable/main/binary-arm64` + `pool/`.
  - Hosting + signing — stand up apt.openastro.net, generate the repo GPG signing key, publish
    the public half at `/gpg.key`.
  - Sibling packages — §34.2 Recommends (`alpaca-bridge`, guider daemon) need their `.deb`s in
    the same pool; coordinate with those repos.
- **§13/§34 RPi install + smoke test** — physical-Pi-gated.
- **First public release tag `v0.0.1-ara.1`** — user authority; tagged on master after the smoke test.
- **Hardware-gated validations parked with the release** (PORT_TODO "Parked blockers"):
  live guider integration tests (guider back on the LAN), real-sky polar-align validation
  (mount + sky), mount-dependent flows.

## 3. Autofocus completion arc (§59 → unlocks §58 and §38)

The highest-leverage internal dependency: building/validating the live AF sweep unlocks
**RunAutofocus**, **AutofocusAfterExposures**, and **refocus-after-flip** in one stroke.

- **Live V-curve sweep validation** — the sweep orchestration is built; live end-to-end
  validation on a real focuser is owed (focuser-hardware-gated).
- **§59.7 backlash auto-discovery.**
- **Smart Focus (§59.2–59.4)** — the headless build is essentially COMPLETE. Shipped: per-star
  feature vector, `FocusInverseMap`, donut geometry, obstruction depth, the calibration profile
  store, the Phase-2 one-frame Smart runner (`TrySmartFocusAsync` — 2-3 shots with the §59.11
  fallback ladder to Classic), ~~the §59.4 telescope-type extractor selector~~
  (`FocusFeatureProfile`: wire-string parse → per-type magnitude key + side-classifier feature
  sets, consulted by `FocusInverseMap.Build` and `FocusSideClassifier`; client picker in
  Settings → Autofocus + the wizard). Remaining: only the intra/extra-focal **asymmetry
  coefficient** (no clean single-frame definition yet — co-design with the user; the shipped
  `RadialProfileSkew` side classifier covers the direction read meanwhile).
- ~~**§59.10 collimation verdict**~~ — shipped server-side: `CollimationEvaluator` runs after
  each Classic sweep (donut-shadow centroid vector average → severity verdict → WS publish +
  notification). A dedicated WILMA visualization beyond the notification remains client debt.
- ~~**Sequencer instructions**~~ — COMPLETE: ~~port **Run Autofocus** and **Center (and
  Rotate)**~~ (both landed: `RunAutofocus` wraps `AutofocusSweepService` via
  `IAutofocusExecutor`; `CenterAndRotate` wraps `CenteringService` via `ICenteringExecutor`);
  ~~rotation fidelity~~ shipped #806 (`CenterAndRotateAsync` rotates to the plan's position
  angle before centring); ~~carrying the §36 framing position angle into the run~~ shipped —
  a dialed framing PA now emits a `CenterAndRotate` in place of the blind slew (untouched 0°
  dial keeps the slew so runs never require a solver the user didn't opt into).
- **§58 tail** — refocus-after-flip (lands once the AF sweep is validated); §58.6 profile schema
  gaps (`refocus_after_flip` / `guider_recal` mode enums, added together with the orchestration
  they configure); §58.12 client settings entries; §58.13 morning summary.

## 4. Guider daemon thread (externally gated on openastro-guider)

- **Adopt upstream openastro-guider#57 once it merges** (see `PHD2-GAP.md`):
  1. Progress bar in the calibration dialog from the `DarkLibraryBuild*` / `DefectMapBuild*` events (§63.6).
  2. Structured fault routing — `EquipmentDisconnected`/`EquipmentReconnected` → `equipment.fault`
     notifications (§46.3) instead of string-parsing `Alert`; feeds §42 fault recovery + §42.3.
  3. `get_version` handshake in the §63.2 connection state machine / §63.9 fork identification,
     replacing the wait-for-catch-up-event workaround. Mind the casing caveat: event keys are
     PascalCase (`Fork`), RPC keys snake_case (`fork`).
  - The 7 documented workarounds in `PHD2-GAP.md` stay as-is; promote any individually if field
    friction emerges.
- **§45 polar alignment, remaining phases** (gated on the upgraded daemon running):
  daemon FITS spike (verify `capture_single_frame` emits a solver-ready FITS; measure the
  per-solve error budget to pick 2-pt vs 3-pt) → `PolarAlignService` state machine replacing
  `PlaceholderPolarAlignService` (preflight/lease → 2-frame seed with Alpaca RA slews → live
  adjust loop → verify + hand-back restore) → §45.9 endpoints + WS events → the WILMA
  bullseye/arrows screen.
- **§63.4 profile lifecycle beyond connect-time** — `delete_profile` / `clone_profile` /
  `rename_profile` mapped onto ARA profile lifecycle hooks (which don't exist headless yet).
- **e-4b-2 leftover** — record `calibration_state.guider.dark_library` on build completion
  (waits on a server-side calibration-state store existing).
- ~~**Poll-failure visibility**~~ (#770 round-2) — shipped: `PollBuildProgressAsync` logs ONE
  Warning per build after 5 consecutive failed polls, plus a drain-time Warning when a whole
  build produced zero successful reads (covers builds shorter than the streak threshold); the
  per-tick swallow stays (a dropped frame is cosmetic) and the build is never disturbed.

## 5. Imaging pipeline tail (§26 / §2105 / §64)

- **libraw RAW decode + DSLR stubs** — `ExposureData.CreateRAWExposureData`,
  `BaseImageData.SaveTiff`, `BaseImageData.FromFile` (non-FITS/XISF),
  `ImageArrayExposureData.FromBitmapSource`.
- ~~**OSC/colour annotation**~~ — shipped with the real fix: detection on a super-pixel
  CFA-weighted luminance plane (same half-res grid as the colour output, one debayer pass) +
  `EncodeColorAnnotated` colour overlay; `Annotate` now works on both sensor types.
- **Repo-frame annotation seam** — `DetectStars(annotateImage:true)` on the repo path is a no-op;
  implement the `IStarAnnotator` DI seam. Related: `LoadImageDataAsync` leaves
  `starDetection`/`starAnnotator` `null!` — wire a DI-registered
  `IImageDataFactory.CreateBaseImageData` (needs headless `IPluggableBehaviorSelector<>`
  registrations) or guard with `NotSupportedException`.
- **OSC display wiring** — `BaseImageData.RenderBitmapSource` renders the raw grey CFA mosaic
  until the render path calls Debayer for OSC display.
- ~~**Live-view detection cost**~~ — shipped: the live loop split moves detection + render off
  the `_captureInFlight` gate (#809), and `StarDetector` honours the marker cap with a
  brightest-first early-out instead of measure-then-discard (#810).
- **Deep offline star catalogue** — bundle deeper Norder tiles (mag ~12–14) to replace the
  reverted online Gaia layer (bundled catalogue stops at mag 7).

## 6. Client platform + UX debt

- ~~**Canonical "active server" provider**~~ — shipped: every per-server API construction routes
  through `activeServerProvider` / `activeServerFutureProvider` (no call site touches
  `servers.last` anymore), and the underlying bug is fixed — `SavedServerService.add` moves a
  re-confirmed server to the end, so reconnecting an older rig makes it the active one (it used
  to be a silent no-op). A future §55.1 explicit switcher changes one definition.
- ~~**Tonight's Sky slice 4b**~~ — shipped: the panel's what-if dialog (tune button) sends
  `focalLengthMm`/`reducer`/`sensorW`/`sensorH`/`pixelUm` + `mosaicX`/`mosaicY` as per-request
  overrides; blank fields merge from the profile server-side, the icon tints while active,
  Reset restores the profile's rig. Session-scoped by design (a what-if, not a setting).
- ~~**Custom-horizon (terrain) integration**~~ — already shipped (stale entry, corrected
  2026-07-10): `TonightSkyService.Rank` takes the profile's `CustomHorizonDto` and, when
  `UseCustomHorizon` is on with a non-empty skyline, evaluates "above the horizon" per-azimuth
  via a 361-entry sin lookup; the endpoint passes `GetCustomHorizon()`.
- **On-device window-scan profiling** — confirm the ±12 h / 288-sample scan against the real
  installed OpenNGC catalog (<100 ms expected).
- ~~**Responsive dashboard tiles**~~ — shipped: `ResponsiveTileGrid` (LayoutBuilder → shared
  computed tile width, min 180 / max 280 / 12 spacing) replaces the fixed-200 `Wrap` for the
  Overview + Achievements tiles and the milestone badges.
- ~~**Wizard "clear field" affordance**~~ — shipped for the fields where a stale clone is
  actively harmful (ASTAP binary/database paths, save directory, filename template): a reset
  suffix on the field marks it in `ProfileDraft.clearedFields`, and the Save mappers write the
  section DEFAULT instead of keeping the cloned value; typing un-marks. Other sections' fields
  stay keep-on-blank (their defaults are benign); extend per-field if friction emerges.
- **§37.5 wizard safety/site extras (enforcement-first)** — per-weather granular actions,
  WILMA-offline auto-abort timer, alarm sound/vibrate, soft-warning altitude: add each field
  together with the engine/planner surface that consumes it. ~~Max-sequence-runtime cap~~ —
  shipped: `SiteSettingsDto.MaxSequenceRuntimeMin` (0 = no limit) + the sequencer's runtime-cap
  watchdog (graceful user-stop path + notification; cap re-read per tick so mid-run Settings
  changes apply) + Settings → Safety → Site row + wizard screen 16 field.
- **Logs + bug report streaming** — ~~§29.9 tail full-file scan → reverse byte-scan~~ (shipped:
  `LogService.TailAsync` reads chunks backwards from a length snapshot, splits on the newline
  byte with byte-level carry across chunk boundaries — UTF-8-safe — and early-exits once the
  window fills); a line index + continuation token stays deferred until the §54 panel
  live-streams or paginates. Still open: §54 daemon-log and bug-report ZIP downloads buffer
  whole files in memory → stream to path (needed before any mobile target ships).
- **Merged Planning-tab prose reconciliation** — the §36/§25.5 merged-tab plan (decided
  2026-06-15) is largely superseded by the #611 native planetarium; reconcile the prose across
  §36/§47/§25.5/§61 + COMMIT-PR-RULES and close or re-scope the PORT_TODO entry.

## 7. Feature backlog (by theme)

The former version-bucketed roadmap, regrouped. Source §s preserved; nothing dropped.

### 7.1 Imaging intelligence
| Feature | Source | Notes |
|---|---|---|
| **Live stacking** | GAPS-ARA Tier 3 | User explicit: "will do it for sure just later." Real-time integration preview; star registration + sigma-clipped running stack; EAA + "is this target worth tonight" feedback. ASIAir/SharpCap parity. |
| **Adaptive / runtime Glover exposure** | NEXTGEN §5 fork B | Measure actual sky background from live frames and set sub length during the run (tracks moon/transparency/twilight). ARA-only execution behavior — no NINA round-trip; sits naturally after the native sequence model. |
| **Native ARA sequence model + NINA-as-importer** | NEXTGEN §6 | Canonical `schemaVersion`'d ARA format; NINA JSON demotes to one import adapter. Three recorded sub-decisions open (engine-vs-format — strong lean keep NINA's engine; export policy; schema shape). Incremental: native model behind the existing editor, NINA import green throughout. |
| **Imaging campaigns / adaptive scheduling** | former §55.2 | Multi-target survey programs; "image whichever target is best right now" scheduler, beyond manual sequences. |

### 7.2 Ecosystem & extensibility
| Feature | Source | Notes |
|---|---|---|
| **Plugin SDK + equipment scripting hooks** | §10, GAPS-ARA T3 | Bundled design pass: pre-sequence/post-frame hook scripts, custom equipment control, community plugin ecosystem, fresh SDK schema. |
| **Plugin marketplace UI** | former §55.2 | In-app browsable plugin store once the SDK is stable (the §10 plugin browser ships pointing at an empty manifest). |
| **OpenAPI-generated SDKs** | §49.7 | Auto-generated Python/JS/Go clients from the spec, for community integrations. |
| **Generated docs, multi-spec selector** | §49.7 | Swagger UI selector across published API spec revisions. |
| **Sequence-templates expansion + registry** | GAPS-ARA T3 | Beyond the 3 built-in templates (LRGB, SHO, comet); community-contributed registry for DSO + comet workflows. |
| **§70 profile + sequence sharing** | §70 | Replace `PlaceholderProfileShareService`; the `profile-share-v1` / `.araseq.json` wire formats already shipped — no breaking changes. |
| **OpenAstro Hub** | §70.6 | Central catalog at openastro.net/hub; WILMA browse-and-import; curated starter packs per scope class. Builds on §70. |

### 7.3 Multi-device & remote
| Feature | Source | Notes |
|---|---|---|
| **WILMA mobile builds (iOS + Android)** | §18.G, §41 | App Store + Play listings: Apple Developer ($99/yr) + Play ($25), review/signing/privacy-manifest upkeep; TestFlight / Play open-testing as beta staging. Server API surface already spec'd — no server changes needed. |
| **TLS / remote-internet access** | GAPS-ARA T3 late | TLS termination + remote-access mode with warnings. Documented workaround today is VPN. |
| **Read-only multi-client / spectator mode** | §27.4 | Beyond single-client: spectator connections (remote-observatory viewer). |
| **Concurrent multi-server (observatory mode)** | §30.8 | One WILMA managing N Pis: concurrent WS, tabbed UI, cross-rig stats/notifications/emergency-stop, optional cross-rig orchestration. Engineering touch is per-server state forking throughout the shell. |
| **Multi-device WILMA settings sync** | GAPS-ARA T3 late | Server-side storage of WILMA UI prefs, synced across the user's devices on connect. |
| **AlpacaBridge + guider-daemon WILMA-push updates** | §33.6 | Same atomic-swap + rollback as ARA Core's WILMA push, extended to siblings (~50–100 MB app growth). |
| **Apt-pushed server updates** | §33 | Server self-update via the apt channel (the §34 pipeline is the prerequisite). |
| **Multi-target stream backup** | §44.11 | Mirror frames to two desktop WILMAs simultaneously. |
| **Cloud streaming backup** | §44.11 | rclone-based push to S3 / Google Drive / etc. for off-site backup. |

### 7.4 Equipment breadth
| Feature | Source | Notes |
|---|---|---|
| **§47 mosaic imaging** | §47 | Replace `PlaceholderMosaicService`; panel math + sequencer integration. |
| **Dedicated polar-align cameras** | §45.14 | Native iPolar / PoleMaster / Alpaca-tagged "PolarAlignCamera" devices; same UI + math, smaller frames. |
| **Astrometry.net solver support** | §18.I | If demand emerges; Survey-Manager-style UI for index downloads. (ASTAP backend packaging — building headless `astap_command_line` from the fork, `.deb`, star DB, `ASTAPLocation` — is ops work already possible with no ARA code change.) |
| **First-connect conformance check, default on** | §52.5 | Currently optional + off; flip the default once compliance testing matures. |
| **Driver-version-awareness registry** | §52.7 | Community registry of "driver X vN has bug Y, fixed in Z." |
| **Community-curated MOUNT_TIPS.md** | §52.7 | User-contributed mount-specific tips as documentation, not hardcoded behavior. |
| **Comet motion tracking during exposure** | GAPS-ARA T3 late | Update RA/Dec per exposure from orbital elements for moving targets. |
| **Bulk asteroid catalog** | §36.8 | Smart-culled MPC layer (~1.4M numbered asteroids) with visibility/magnitude filtering; today is targeted-lookup only. |
| **Multi-instance equipment generalization** | PORT_TODO (execution-engine) | Switch was the pilot: multi-switch remember + auto-connect (`EquipmentSelectionStore` is one-device-per-type), generalize `{n}` to other device types per §10.6 as real rigs need it, equipment `switch.*` WS events (no device emits equipment WS events yet — cross-cutting), friendlier device/port picker in the sequence editor. |

### 7.5 Analytics & notifications
| Feature | Source | Notes |
|---|---|---|
| **Stats: Equipment Health view** | §50.19 | Cooler-power trend, fault-rate analytics, mechanical-drift detection. |
| **Stats: Session Efficiency view** | §50.19 | Time-breakdown (light vs AF vs slewing vs faults); needs sequencer instrumentation. |
| **Stats: Conditions correlation view** | §50.19 | Quality vs weather + lunar; needs reliable weather data. |
| **Stats: Achievements / milestones** | §50.19 | Light gamification (streaks, records, badges) — see also part 9 (points layer is user-parked). |
| **Stats exports: PDF + Astrobin** | §50.19 | Per-target PDF reports; Astrobin-ready JSON. |
| **Per-user diagnostic threshold calibration** | §51.9 | Learn the user's normal HFR/star-count baselines; adjust thresholds vs. global defaults. |
| **ML pattern detection for diagnostics** | §51.9 | Small on-device model on user-labeled events; opt-in. |
| **Predictive alerts** | §51.9 | Proactive ("you usually hit dew ~03:30") vs reactive. |
| **Notification channels: push, email, Discord/Slack webhooks** | §46.9 | Outbound integrations beyond the in-app feed; needs FCM/APNs or SMTP. |
| **Notification scripting** | §46.9 | User-defined IFTTT-style "when X do Y" rules. |

### 7.6 Platform & product
| Feature | Source | Notes |
|---|---|---|
| **§21 localization / i18n** | §21 | English-only today (non-English stripped in 0.5e/f); full i18n pass. |
| **§75 signed / store client packaging** | §75 | Flutter builds exist for all platforms; signing + store distribution remain. |
| **§71 Native AOT revisit** | §71 | Paused for §38 Newtonsoft (`<PublishAot>` off); revisit via `[JsonPolymorphic]`. |
| **§74 full contributor onboarding doc** | §74 | README + §23.1 exist; the full onboarding doc is pending. |
| **Survey downloader polish** | §36 | Parallel downloads with resume across restarts; background download on mobile; incremental `If-Modified-Since` updates. |
| **Pre-built RPi OS image** | former §55.2 | Flashable image, everything pre-configured; needs a CI image-build pipeline. ASIAir-level zero-friction install. |
| **WCAG 2.1 AA formal certification** | former §55.2 | From AA-leaning baseline (§53) to formal third-party-audited compliance — only if observatory/outreach use justifies it. |
| **Light-mode theme variant** | former §55.2 | Daytime planning + outreach demo contexts. |
| **Web UI option** | former §55.2 | Web frontend reusing the OpenAPI client + API surface. |
| **General command palette** | §61.10 | Expand the ⌘K settings search into a full command palette (actions, navigation, equipment ops) — explicitly committed expansion. |
| **Client distribution channels** | §75.3, §75.7 | Homebrew cask, Chocolatey, AUR PKGBUILD, Windows `.msix`, optional Flatpak; in-app updater (+ its settings registrations) when auto-update ships. Mobile stores are the §41 row above. |
| **NINA database importer** | §56.2.2 | Import NINA's `data.db` session/frame/calibration history (profiles + sequences already import per §56.4); only if user demand emerges. |
| **Mount Safety v2** | §57.9 | The deferred slew-safety expansion pass beyond lean §57, once the panic-button baseline proves out in the field. |
| **In-app equipment database / curated gear registry** | former §55.2 | Curated defaults surfaced in the §37 wizard. Strict scope guard retained: defaults-pre-fill for owned gear only, never a recommendation engine; community wiki proves the format first. |
| ~~Native Flutter sky-renderer~~ | former §55.2 | **Superseded** — Aladin/CEF were removed and replaced by the native Stellarium-based atlas (#611/#649). Kept for the record. |

### 7.7 Per-section expansion-path index

The playbook keeps per-section "Future expansion paths" / "What's deferred" / "Out of initial
scope" subsections. Every **committed** item from them already has a row in 7.1–7.6 above (or a
part-3–6 workstream entry); this index catches the **speculative tails** so nothing scattered in
the playbook is lost. One line per subsection; the playbook text is the reference:

- **§27.4 / §28.5 / §44.11 / §53.5 / §66.8** — "out of initial scope" boundary lists (spectator +
  admin override; mid-instruction resume, durability-mode knob, UPS GPIO, FITS checksum scrubbing;
  multi-target/cloud backup; formal WCAG; runtime pool/queue tuning). Committed pieces have rows above.
- **§28.14** — migration follow-ups: down-migrations reconsideration, encrypted backups
  (`PRAGMA key`), the `restore-from-backup` endpoint (today a 501 pointer).
- **§33.6** — sibling WILMA-push updates (row in 7.3) + Ed25519 signature verification addition.
- **§45.14 / §46.9 / §47.13 / §48.9 / §49.7 / §51.9 / §52.7 / §59.18 / §62.16 / §63.16 / §64.17 /
  §65.10 / §70.6 / §71.6** — the per-feature expansion subsections behind the 7.1–7.6 rows
  (PA cameras; notification channels/scripting; mosaic; flats; API SDKs/docs; diagnostics
  learning; driver registry/tips; Smart-Focus extensions incl. ML feature extraction + tilt-aware
  focus; dither variants; guider profile lifecycle; Live View extensions incl. stacking preview;
  channel-independent stretching; OpenAstro Hub; plugin-SDK AOT constraint).
- **§56.2.2** — NINA database importer (row in 7.6).
- **§57.9 / §58.17** — Mount Safety v2 (row in 7.6); flip hook scripts (fold into plugin SDK),
  mount-driven trigger mode, lock-screen push (fold into notification channels).
- **§60.7.1 / §60.8.1** — CORS tightening and a first `/api/v2` surface, only when auth/breaking
  changes actually accumulate.
- **§66.7 / §66.10** — perf-metrics endpoint + Stats panel; promoting SLO targets to CI gates.
- **§67.4** — remote-access mode (row in 7.3: TLS / remote access; re-adds tokens, rate limiting,
  4001 WS close code).
- **Inline notes** — one-liner "a future release may…" ideas stay in place throughout the playbook
  (e.g. §29.5 storage rotation policies, §29.9 log-rotation knobs, §30.8 remote-wake surface,
  §36 CDN hosting); grep `future release` to enumerate them. They graduate to rows here only when
  actually committed.

## 8. Verify / audit passes

Deliberate confirmation passes, not new features (the checklist's "= verify" entries):

- **§31** time + location sync — full waterfall verification.
- **§57** Stop Mount — full slew-safety policy verification.
- **§68** AlpacaBridge — full bridge contract verification. (The playbook §68 prose was
  reconciled 2026-07-09 to drop the removed minimum-version gate.)
- **§14** integration tests gated on sims/hardware — run the gated suites when rigs are available.
- **§53** accessibility — WCAG audit, ongoing.
- **FITS keyword-convention audit** — `GAIN`/`OFFSET` aren't in the core FITS dictionary; revisit
  only as a deliberate full header-convention audit.
- **§38 daemon schema check** — verify the daemon accepts a promoted plain-array `Items` wrapper
  (untested cross-boundary assumption).

## 9. Parked pending Joey's decision

Explicitly user-gated calls — blocked on a decision, not on engineering:

- **±score nudge for filter/emission advice** — advice-only today; the gentle ±3 soft nudge
  (never a hard filter) needs the weights call (TONIGHT_SKY / NEXTGEN; recorded in PORT_TODO).
- **Moon as a score input** — advisory display shipped; weighting it needs the user's call, per
  advise-don't-dictate (TONIGHT_SKY).
- **Framing-fit chip revival** — built + verified, shipped as PR #618, closed at user request to
  hold; intact on branch `feat-tonight-sky-framing-fit`. Reopen once the cutoffs (fills = 0.50,
  small = 0.33 of short side) are confirmed.
- **Points / achievements / gamification layer** — back-burnered by the user 2026-06-28; the 0–100
  transparent score is the non-gamified core it could later build on.
- **REST calibration-frame capture** — manual capture is LIGHT-only by design (`ExposureRequestDto`
  has no `ImageType`); add an optional `ImageType` (default LIGHT) only if a REST calibration
  affordance is wanted.

## 10. Appendix — accepted watch-items & small follow-ups

Complete at time of writing; one line each. Details live in `PORT_TODO.md` (open half) under the
named section — this appendix indexes, it does not duplicate.

**Sequencer / engine** (PORT_TODO "Execution-engine TODOs"):
- Shutdown completed-vs-stopped race (#319) — accepted; not worth the machinery.
- §28 checkpoint writes synchronous per progress tick (#667) — measure on the Pi; debounce if it shows.
- `ITelescopeMediator.Sync` takes no CancellationToken (#757) — hung driver can delay centering
  cancel up to `MountOpHardTimeout`; deferred interface churn.
- Globalization-invariant gotcha — inherited NINA code constructing named cultures must use the
  `SafeCulture` fallback or it crashes the AOT daemon (standing reference).
- Headless mediator stubs — real Alpaca wiring swaps in device by device; dome-following
  (`IDomeFollower`, §38k-21) stays stubbed; telescope `DestinationSideOfPier`/topocentric-slew/
  MeridianFlip members stubbed (no headless consumer yet).

**Guider** (PORT_TODO "§63.3 watch-items" / "guider-d additional deferrals" / "§63.5 follow-ups" / "§63.4"):
- `_recovering` spans the auto-reconnect grace window — split the pass token if field behavior warrants.
- 2 s per-call ping connection churn while guiding — keepalive RPC socket if it shows against the real daemon.
- `_recovering` stale-flag race (ultra-narrow) — accepted, low priority.
- `inactive` nudge fights a deliberate `systemctl stop` — add a guard only if it bites in the field.
- `RequestRestart` permission-failure is silent (fire-and-forget) — await + log if a rejection signal is wanted.
- Per-axis `MinimumMove` (RA/Dec split) — deferred until a concrete ask.
- `DisconnectPHD2Equipment()` has no CancellationToken — thread one through if prompt cancel matters.
- Profile copy-source latent — if a caller ever sets `copy_from`/`copy_from_id`, enforce at-most-one.

**Bench / CI** (PORT_TODO "Virtual-observatory bench"):
- bench Dockerfile restore-cache split needs BuildKit `COPY --parents` — revisit on a BuildKit runner.
- `ForwardAsync` response-direction header forwarding (only Content-Type today) — extend if a scenario needs it.
- Guider connect getters hard-fail on bare results — make each independently best-effort.
- Wire the arm64 Linux bench lane into CI when a hosted arm64 runner exists.
- Flaky-test guidance: keep workflow steps pinned-to-SHA + `persist-credentials: false` (ci-harden).

**Backup / stats** (PORT_TODO "§43 backup — §43-2 deferrals"):
- Restore sha-256 gate bypassable by deleting the manifest — require the manifest when remote restore lands.
- No disk-space pre-flight on backup create — pre-flight or 507 mapping.
- Async zip packaging + `backup.*` progress WS — move create onto a worker like restore (202 contract already in place); interim 120 s client read timeout.
- Stats index residuals — `SUM(CASE …)` scans; `(focuser_position, captured_utc)` covering index if the catalog grows.
- §50.4 focuser position narrowed `(int)GetInt64` — widen to `long` end-to-end if ever needed.
- Best Frames: validate `frame_id` rather than degrading to `''` — tighten with per-frame drill-down.

**API / product scope** (PORT_TODO "§43" tail — cross-cutting, user-authoritative):
- Daemon-wide API auth unaddressed — trusted-LAN model (`ListenAnyIP :5555`, no auth middleware);
  must become a cross-cutting middleware decision before any non-LAN exposure. Includes the
  `BackupSourceUrl` outbound-GET (SSRF/reachability-oracle) surface accepted within that model.

**Client polish** (PORT_TODO, various):
- §54 `LogsApi.tail` silently yields empty on a non-array body — branch on response shape if it bites.
- §64 Live View reports pre-encode frame dims (encoder may downscale) — report post-encode dims if a consumer needs truth.
- §36 catalog serve endpoint re-parses `catalog.csv` per request — memoize per package if load lands.
- §70 import `DroppedFields` can drift from export strip logic — derive both from one source when §70 is next touched.
- §36 multi-target: duplicate same-name blocks removed in list order; NINA "End" containers not
  recognized as session-end when appending — handle if imported-sequence appending matters.
- §36 planning horizon: geographic-pole degeneracy out of scope (flag or true constant-altitude
  horizon only if polar sites are ever supported).
- §25.5.5 camera daemon DTO gaps (lower-value audit): `sensortype` explicit, `exposureresolution`,
  `hasshutter`, `gains[]`/`offsets[]` value-list mode, `heatsinktemperature`.
- §38 editor forward-looking guards: release-mode throw for non-String-keyed catalog defaults;
  container `build()` populating an empty `Items` wrapper; controlled text fields when undo/reset lands.
- Linux client packaging: declare GStreamer runtime Depends (`libgstreamer1.0-0`,
  `libgstreamer-plugins-base1.0-0`) when a WILMA Linux package exists (`audioplayers_linux`).
- Auto-resume pointing refinement — re-slew/re-center to the checkpointed target on resume
  (needs per-run target coords surfaced to the engine); today the notification says verify pointing.
- Flats probe bounds per filter (#754 round-2) — generated flat leaves keep default [0.01,10] s
  probe bounds; a narrowband panel needing >10 s fails honestly; add per-filter bounds if field use shows the need.
- Blind-solver fallthrough (#363) — AstrometryNet-configured profile silently gets ASTAP as both
  primary + blind; drop the option from profile/wizard or log the substitution.
- Plate-solve slices (low priority): REST centering trigger (`POST /platesolve/center`, needs the
  202-Accepted long-running-op pattern + progress surface); #756 frame-solve header-reuse micro-opt.
- §39 `ListSessionsAsync` O(N) queries per page + integer-OFFSET cursor — batch + keyset
  pagination when catalog sizes warrant (same shape in `SqliteDarkLibraryService`).
- Playbook prose sweep: ~a dozen bare `DEPLOY.md`/`RUNNING.md` mentions missing the `docs/`
  prefix (#730) — next deliberate playbook pass.

## 11. Out of scope permanently

Deliberately on no path (guard against scope-creep pull):

- **Native INDI / INDIGO protocol support** — committed Alpaca-only forever per §52; bridges only.
- **In-app FITS post-processing** (stacking, integration, gradient removal) — PixInsight/Siril/APP
  territory. ARA captures + organizes; processing is its own tool category.
- **Solar imaging specifics** (filter detection, prominence tracking) — solar imagers can use ARA;
  ARA won't specialize for them.
- **Mount homing mechanical-knob automation** — requires hardware; ARA guides the human.
- **Astrometric measurement tools** (MPC astrometry submission, supernova-search workflows) —
  research-grade, out of scope for the imaging tool.
- **Planetary / lunar lucky-imaging** — architecturally blocked: Alpaca has no video API (§52
  Alpaca-only commitment), so the workflow primitive doesn't exist. Per §18.J this is permanent,
  not deferred. FireCapture / SharpCap / AstroDMx are the right tools.

### What's NOT on this list (and why)

If something seems missing it's likely: (1) **already shipped** — check the playbook TOC and
section checklist; (2) **a speculative per-section expansion idea** — indexed in part 7.7, with
the playbook subsection as the reference; (3) **AI-handled during the port** — docs, NINA-parity
verification, NOTICE.md, README; (4) **a user-policy knob, not a feature** — anything already
configurable via settings; or (5) **outside ARA's product scope** — see part 11 above.
