# GAPS-ARA — Open Items Tracking

**Purpose:** comprehensive list of things discussed during planning that aren't yet in [`PORT_PLAYBOOK.md`](PORT_PLAYBOOK.md), or that are partially covered and need a dedicated section. As each is discussed and baked, it moves to **Resolved** with a pointer to the playbook section.

**Convention:** check the box when resolved. Status notes inline. New items added to the bottom of their tier.

---

## Tier 1 — Open, high-impact

These would actively bite real users on day one if missing. Discuss + bake before turning the AI loose on Phase 0.5.

- [x] ~~**Calibration frames management**~~ → **Resolved §39**. Light/Dark/Bias/Flat sequence types preserved; capture-only philosophy (no calibration at capture); rich FITS session metadata; session library API; **session-metadata-driven matching-flats workflow** (pick past session → server generates flat sequence matching exact equipment state); temp-mismatch handling; dark library auto-generation; calibration library browsing in WILMA.
- [x] ~~**Captured-image library workflow**~~ → **Resolved §40 + §41**. By-session organization with by-target rollups; two-tier preview JPEGs (thumb + full-res for pixel peep); rate/tag/notes; bulk operations; "Resume Target" workflow for multi-year project alignment (records plate-solve + rotator for reproducibility); auto-rating + HFR drift pattern detection ("clouds, not focus"); OS file-association handoff for FITS export. §41 adds mobile companion mode scope: phone/tablet does monitoring + library viewing + pinch-to-zoom + GPS push + emergency stop, NOT sequence editing or full sky atlas (desktop-only).
- [x] ~~**Sequence file format + NINA `.json` import**~~ → **Resolved §38**. NINA schema verbatim + `schemaVersion: "openastroara-sequence-v1"`; OpenAPI-documented; import endpoint with equipment-remap + unsupported-instruction handling; 4 bundled starter templates; sequence-template variable system.
- [x] ~~**Hardware fault recovery (per-equipment)**~~ → **Resolved §42**. Universal retry-then-action pattern (Continue/Notify/Pause/Abort+park), per-fault configurable in profile safety policies. 16-row fault matrix covering camera/mount/focuser/EFW/rotator/guider/plate-solve/ASTAP/dew-heater/switch-value-mismatch/dew-formation. Hot-reconnect with backoff schedule. Switch value tolerance for PWM/dimmable devices. Faults logged to session DB and visible per-frame in image library. NINA's retry semantics preserved; ARA adds value-tolerance + dew detection + unified retry pattern.
- [x] ~~**Backup / restore of profiles + sequences**~~ → **Resolved §29 + §43 + §44**. §29 updated to make USB drive MANDATORY (SD card OS-only — protects against SD wear from sustained FITS writes). Because everything (profiles, sequences, session DB, calibration, FITS, logs) lives on USB, the drive itself is portable — pull it out, plug into another Pi, ARA resumes exactly. §43 covers four backup layers: drive-to-drive clone (primary, user-managed), **real-time stream to desktop WILMA (§44 — pull-based, throttled, capture-aware backoff)**, server-generated lightweight ZIP (profiles + sequences + DB + calibration metadata), auto-snapshots on the USB. Restore flows for same-drive-new-Pi, fresh-USB-with-ZIP, and fresh-start.

## Tier 2 — Open, medium-impact

Important workflows but won't crash anything on night-one. Can be specced after Tier 1 or in parallel.

- [x] ~~**Polar alignment routine**~~ → **Resolved §45**. TPPA dropped entirely (fragile + slow over Alpaca). Replaced with iPolar-style continuous-loop PA using main camera with optimizations: autofocus first, dark-frame caching, aggressive binning (3×3 or 4×4) for fast transfer, ~500 ms loop cadence, zooming bullseye UI in WILMA. Same math as iPolar (RA-axis vs pole offset from rotated plate solves). v0.1.0 adds native dedicated PA camera support.
- [x] ~~**Notifications system**~~ → **Resolved §46**. In-app only (no push/email/webhook for v0.0.1 — field users often have no internet). Full event catalog (~45 event kinds spanning sequence/equipment/safety/calibration/recovery/storage/time/meridian flip/PA/dew/etc.). Four severity levels with distinct UX (info → feed only; warning → toast; critical → sticky toast + chime; urgent → modal + alarm per §35.5). Per-event opt-in/out + severity override + quiet hours (urgent always delivers). Persistent SQLite log on Pi with 30-day prune for info/warning. Notification feed UI with severity icons, filter pills, action buttons. v0.1.0 paths noted: push, email, webhooks, scripting.
- [x] ~~**Mosaic / multi-panel imaging**~~ → **Resolved §47**. N×M grid built in Framing Assistant with Aladin Lite panel-overlay preview. Server computes per-panel RA/Dec via tangent-plane projection (10% default overlap, configurable). Interleaved scheduling (one frame per panel rotating per filter — balances airmass + sky conditions across all panels). Per-panel sub-targets in existing target system. Mosaic-aware Resume workflow: years-spanning projects supported — server tracks completion per panel per filter, new sessions roll up cleanly into the same mosaic. Image Library shows visual grid colored by completion. ARA captures + tags; stitching is post-processing (PixInsight/Siril) per user's choice.
- [x] ~~**Auto-flats / auto-darks**~~ → **Resolved §48**. Sequence-start prompt asks user: capture flats tonight (panel or sky) or capture them later via §39.5 session-matching workflow. User preference saved per profile ("ask each time" default, or "always panel", "always sky", "never"). `FlatPanelFlats` + `SkyFlats` instructions preserved from NINA. Dark library is NOT in the prompt (different scope — built separately by user as a multi-hour overnight task using `DarkLibraryInstruction`). Bias library similarly user-initiated.
- [x] ~~**API documentation serving**~~ → **Resolved §49**. Swagger UI v5 (matching ASCOM Alpaca's convention — both ecosystems use the same tool). Served open (no token) at `/api/v1/docs`. Raw OpenAPI 3.1 spec at `/api/v1/openapi.yaml` + `.json`. Swagger UI's "Authorize" lets power users paste their token to test auth'd endpoints. ASCOM-style CSS customizations.
- [x] ~~**Astrometry.net star index downloads**~~ → **Deferred to v0.1.0** (§18.I updated). ASTAP covers 99% of solving needs; astrometry.net adds another binary + index-file management workflow not worth v0.0.1 complexity. Phase 8 strips astrometry.net call sites from inherited NINA code. v0.1.0 may add it back if demand emerges, with a Survey-Manager-style UI for 4100/4200/5000-series index downloads.

## Tier 3 — Polish / deferred

- [x] ~~**Sequence templates**~~ → **Resolved §38.7**. v0.0.1 ships 4: `lrgb-dso.json`, `narrowband-shoo.json`, `lunar.json`, `planetary.json`. Additional templates (comet wide-field, mosaic-specific, etc.) added in v0.1.0+ as the community shapes preferences.
- [x] ~~**Session analytics**~~ → **Resolved §50**. Full analytics dashboard as a flagship differentiator vs NINA. Stats top-level tab with 11 sub-views: Overview tiles, Targets rollup + per-target detail, Focus & Temperature (HFR-vs-temp scatter + regression per filter), Guiding (RMS trends + altitude/wind correlations), Frame Quality (composite quality score + Best Frames auto-sort), Equipment Health (cooler-power trend, fault rates), Session Efficiency (time-breakdown), Conditions (weather/lunar correlations), Achievements/milestones, Calendar heatmap, Exports (CSV/PDF/Astrobin). Composite quality score formula specced (HFR + star count + roundness + ADU + eccentricity, profile-weighted). Materialized daily aggregates for performance. All local — no telemetry. v0.0.1 ships Overview, Targets, Focus&Temp, Guiding, Frame Quality, Best Frames, Composite Score, Calendar, CSV export, all API endpoints. Equipment Health, Session Efficiency, Conditions, Achievements, PDF/Astrobin exports → v0.0.2/v0.1.0.
- [ ] **Mount-specific behavior profiles** — per-mount quirks (EQMod, iOptron CEM, SiTech, OnStep); homing protocols; slew speed limits; cable-wrap detection; meridian-limit configuration. Mostly Alpaca abstracts this; mention preserved.
- [x] ~~**Multi-user / read-only spectators**~~ → **Deferred to v0.1.0** (§27.4 already notes this). Single-client policy is correct for v0.0.1; spectator mode adds complexity (auth roles, read-only API surface, UI mode toggle) that isn't needed yet.
- [x] ~~**Live stacking**~~ → **Committed v0.1.0 feature** (not optional — explicit roadmap commitment). Star registration + sigma-clipped running stack + live preview during integration. Provides EAA (Electronically Assisted Astronomy) and "is this target worth tonight" instant feedback. ASIAir/SharpCap parity. Engineering work: real-time star detection, frame alignment, calibration application, memory management on Pi.
- [ ] **Equipment scripting / custom hooks** — pre-sequence and post-frame hook scripts; v0.1.0.
- [x] ~~**Pi RTC hardware option**~~ → **DEPLOY.md documentation item.** Documented as optional hardware add-on (DS3231 / PiRTC modules); avoids the §31 time-sync waterfall when present. AI writes this into DEPLOY.md during the Phase 11 documentation pass.
- [x] ~~**Pre-built RPi OS image**~~ → **Deferred to v0.2.0** (per §34 — .deb on apt.openastro.net is v0.0.1 path; pre-flashed image is a polish-tier "make onboarding zero-friction" feature requiring CI image-build pipeline).
- [x] ~~**AlpacaBridge + openastro-phd2 WILMA-push updates**~~ → **Deferred to v0.1.0** (§33.6 already specs this). Same atomic-swap + rollback pattern; new endpoints per component; bundled binaries grow WILMA app size another ~50-100 MB combined.
- [ ] **AlpacaBridge enhancement: switch power levels for RPi devices** — outside ARA Core scope (AlpacaBridge-side work). Support PWM / value-based switches for RPi-attached devices like dew heaters with variable output, dimmable flat panels, voltage-controlled outputs. ARA Core (§42.4) already consumes the full Alpaca `ISwitch` interface so it's ready when AlpacaBridge supports it.
- [x] ~~**Bulk asteroid catalog**~~ → **Deferred to v0.1.0** (§36.8 already specs this). Targeted lookup ("Ceres", "433 Eros") works in v0.0.1; bulk MPC asteroid layer (~1.4M numbered asteroids) with smart-culling by magnitude/visibility is v0.1.0.
- [x] ~~**Survey downloader polish**~~ → **Deferred to v0.1.0** (§36 already calls this out). v0.0.1 ships functional-but-rough single-threaded sequential downloader; v0.1.0 adds parallel + resume-across-restarts + background mobile + `If-Modified-Since` incremental updates.

## Documentation — separate effort, post-Phase 15

- [ ] **README rewrite** (Open Astro front matter, lineage attribution, install quickstart, link to wiki + DEPLOY.md)
- [ ] **NOTICE.md full text** (snippet in §17.2; needs to become a real file at repo root with NINA + Aladin Lite + PI.N.S. credit + complete dep list)
- [ ] **DEPLOY.md** content (mostly drafted in §34.6 but not yet a real file)
- [ ] **User guide / "how to use ARA"** — wizard walkthrough, sky atlas tour, sequence builder basics, safety policy explainer, troubleshooting
- [ ] **CONTRIBUTING.md update** for the fork (current file is NINA's; needs fork-specific content)
- [ ] **`.github/ISSUE_TEMPLATE/`** + **`.github/PULL_REQUEST_TEMPLATE.md`** for the new repo
- [ ] **API_CONTRACT.md initial content** — currently scaffolded as a tracking file in §1; will fill as endpoints are designed in Phase 5

## Things confirmed resolved (for the record)

These came up in conversation and ARE in the playbook. Listed here so we don't accidentally re-litigate.

- ✅ System.Drawing on Linux → OpenCvSharp4 (§26)
- ✅ Multi-client behavior → single-client + popup transfer (§27)
- ✅ Sequence durability + crash recovery (§28)
- ✅ Disk-space policy + storage (§29, plus WILMA-side §29.0)
- ✅ First-run + launch flow (§30)
- ✅ Time + location sync waterfall — WILMA acronym defined (§31)
- ✅ Network resilience + reconnect modal (§32)
- ✅ Version compatibility + WILMA-pushed updates (§33)
- ✅ Distribution via apt.openastro.net + .deb structure (§34)
- ✅ Safety policies — user-configurable per profile (§35)
- ✅ Sky imagery + 21-survey downloader + Tonight's Sky planetarium (§36)
- ✅ Profile setup wizard (18 screens, 7 stages) (§37)
- ✅ Sequence file format + NINA `.json` import (§38)
- ✅ Calibration frames + session-metadata-driven matching flats (§39)
- ✅ Captured-image library workflow + "Resume Target" multi-year alignment + HFR drift detection (§40)
- ✅ Mobile companion mode scope — phones/tablets do monitor + library + GPS-push only, no editing (§41)
- ✅ Hardware fault recovery (per-equipment) + switch value tolerance + dew detection (§42)
- ✅ Mandatory USB storage + backup/restore + drive portability across Pis (§29 updated + §43)
- ✅ Real-time backup stream from Pi to desktop WILMA (§44 — protects against mid-session USB failure)
- ✅ Polar alignment — iPolar-style continuous loop with binned main camera (§45)
- ✅ Notifications system — in-app feed with 4 severity levels + quiet hours + per-event prefs (§46)
- ✅ Mosaic / multi-panel imaging — N×M grid in Aladin, interleaved scheduling, mosaic-aware Resume across years (§47)
- ✅ Auto-flats / auto-darks + sequence-start prompt offering "capture later via §39.5" (§48)
- ✅ API documentation serving — Swagger UI matching ASCOM Alpaca's convention (§49)
- ✅ Astrometry.net star index downloads → deferred to v0.1.0 (§18.I updated)
- ✅ Session analytics + Stats dashboard (§50 — flagship NINA differentiator)
- ✅ Real-time acquisition diagnostics + smart corrections (§51 — pattern-based cause diagnosis ["clouds vs focus drift vs trees"], auto-actions, user-policy aggression dial, per-frame FITS metadata enrichment, configurable from balanced default → aggressive/conservative/notify-only)
- ✅ Aladin Lite license boundary (GPLv3 via WebView process boundary) (§36.11)
- ✅ NINA-style UI clone with bitmap-asset placeholders (§25)
- ✅ Fork hygiene — naming, identifiers, MPL preservation (§17)
- ✅ Auto-approve safety rails (§19)
- ✅ Quota-resume protocol (§20)
- ✅ Pi Wi-Fi (AP vs Client mode) — punted to OpenAstro wiki (§32.6)
- ✅ Updater (in-app) → DROPPED; replaced by §33 WILMA push (§18.A)
- ✅ Plugin store → deferred to v0.1.0 (§18.B + §10)
- ✅ Telemetry → local logs only, no network (§18.C)
- ✅ Community/branding links → GitHub only (§18.D)
- ✅ Localization → English-only (§18.E)
- ✅ Code signing → ship unsigned, revisit when funded (§18.F)
- ✅ Distribution formats → .deb for server, Flutter handles client per platform (§18.G + §34)
- ✅ Branding assets → placeholders, user supplies (§18.H)
- ✅ Plate solving → ASTAP cross-platform, no PlateSolve2 (§18.I)
- ✅ Stefan-NINA config import on first launch → DROPPED (went headless, then Mac-primary)
- ✅ AvalonDock layout migration → N/A (Flutter, not Avalonia)
- ✅ Avalonia plugin compat shim → N/A (no Avalonia port; no plugin SDK in v0.0.1)
- ✅ Apple Developer ID → defer (§18.F)
- ✅ INDI client from scratch → REJECTED (Alpaca-only via AlpacaBridge)
- ✅ NOTICE.md snippet (§17.2; full text generation TBD per Docs section above)
- ✅ Stefan upstream sync → hard fork, no sync (§17 + §0)
- ✅ Stefan branding strip (§4.3)
- ✅ Two-tap emergency abort vs single-tap → single-tap (§35.3)
- ✅ Pre-built RPi image → rejected in favor of .deb (§34); v0.1.0 reconsider
- ✅ MPC bulk asteroid catalog → deferred to v0.1.0, targeted lookup only in v0.0.1 (§36.8)
- ✅ `safety_alarm.wav` audio source → AI uses public-domain or generated tone (§35.5)

## NINA features to preserve (verify during port; no new section needed unless gap)

These are NINA capabilities the port should preserve as-is from inherited code. The AI verifies during Phase 8 and per-area phases.

- Auto-meridian-flip
- Auto-focus on temperature change (temp comp + per-filter offsets)
- Auto-center after dither
- Sequencer's conditional instructions (`IF`, `WAIT`, `LOOP`)
- Sequencer's recovery instructions
- Sequencer's pause/resume/abort semantics
- Per-target framing + rotation
- HFR + star detection algorithms (Hocus Focus etc.)
- Autofocus curve fitting (parabolic, hyperbolic, trend-line)
- Coordinate transformations (epoch, refraction, parallax)
- FITS header conventions (NINA-specific tags downstream apps recognize)
- Dither cadence configurations
- Filter offsets per-temperature curves

If anything in this list turns out NOT to be preservable during the port, it moves to a Tier 1 or Tier 2 gap.

---

## How to use this file

- **Adding a new gap**: append to the relevant tier with a one-line description.
- **Resolving a gap**: change `[ ]` to `[x]`, move to the **Confirmed resolved** section, add the playbook section number.
- **Closing without baking** (intentional defer or won't-fix): move to Confirmed resolved with a brief reason.
- **Updates as we go**: whenever a discussion in chat covers an item here, capture the resolution in the playbook AND tick it off here.
