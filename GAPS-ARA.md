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
- [ ] **Notifications system** — beyond in-app WebSocket events: push notifications on mobile when sequence completes or errors; email integration (future); Discord/Slack webhook (future); notification preferences per event type.
- [ ] **Mosaic / multi-panel imaging** — NINA's framing-assistant mosaic panel grid; preserve through port; ensure Aladin Lite layer supports panel overlays.
- [ ] **Auto-flats / auto-darks** — sequence step types that automate calibration at dusk/dawn; flat panel coordination; sky-flats fallback if no panel.
- [ ] **API documentation serving** — Pi serves Swagger UI at `/api/v1/docs` from the OpenAPI spec; helps power users + plugin authors (v0.1.0).
- [ ] **Astrometry.net star index downloads** — separate from ASTAP star database; if Astrometry.net is kept as a solver option, the index files (4100-series, 4200-series) need a download/manage workflow. ~5-100 GB depending on index set.

## Tier 3 — Polish / deferred

- [ ] **Sequence templates** — ship starter templates: LRGB DSO, narrowband Ha-OIII-SII, lunar mosaic, planetary, comet wide-field; user can customize/duplicate. Faster onboarding.
- [ ] **Session analytics** — integration time per target across sessions; guide RMS trends; HFR-vs-temperature curves; star count + roundness over a night; "best frames" auto-sort.
- [ ] **Mount-specific behavior profiles** — per-mount quirks (EQMod, iOptron CEM, SiTech, OnStep); homing protocols; slew speed limits; cable-wrap detection; meridian-limit configuration. Mostly Alpaca abstracts this; mention preserved.
- [ ] **Multi-user / read-only spectators** — currently single-client per §27; v0.1.0 could add read-only "spectator" connections (e.g., remote-observatory client viewing without controlling).
- [ ] **Live stacking** — real-time integration of incoming frames into a stacked preview; SharpCap/ASIAir feature; v0.1.0.
- [ ] **Equipment scripting / custom hooks** — pre-sequence and post-frame hook scripts; v0.1.0.
- [ ] **Pi RTC hardware option** — battery-backed RTC modules (DS3231, etc.) avoid the time-sync waterfall in §31 entirely; document supported modules in DEPLOY.md / wiki.
- [ ] **Pre-built RPi OS image** — alternative to .deb install; pre-flashed SD card with everything ready. v0.1.0 / v0.2.0; CI image-build pipeline required.
- [ ] **AlpacaBridge + openastro-phd2 WILMA-push updates** — same pattern as §33 for ARA Core, extended to siblings. Mentioned in §33.6 as v0.1.0 scope.
- [ ] **AlpacaBridge enhancement: switch power levels for RPi devices** — outside ARA Core scope (AlpacaBridge-side work). Support PWM / value-based switches for RPi-attached devices like dew heaters with variable output, dimmable flat panels, voltage-controlled outputs. ARA Core (§42.4) already consumes the full Alpaca `ISwitch` interface so it's ready when AlpacaBridge supports it.
- [ ] **Bulk asteroid catalog** — currently targeted lookup only per §36.8; v0.1.0 adds smart-culled MPC asteroid layer.
- [ ] **Survey downloader polish** — parallel downloads with resume across app restarts; background download on mobile; incremental updates via `If-Modified-Since`. v0.1.0 (v0.0.1 ships single-threaded sequential).

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
