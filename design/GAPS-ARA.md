# GAPS-ARA — Open Items Tracking

**Purpose:** comprehensive list of things discussed during planning that aren't yet in [`PORT_PLAYBOOK.md`](PORT_PLAYBOOK.md), or that are partially covered and need a dedicated section. As each is discussed and baked, it moves to **Resolved** with a pointer to the playbook section.

**Convention:** check the box when resolved. Status notes inline. New items added to the bottom of their tier.

---

## ⏸ Session checkpoint — pick up here next time (2026-05-21)

**Where we left off:** Closed out the AI/user/sleeping-rig safety arc (Stop Mount + meridian flip + unattended-failure shutdown), the autofocus + dither + collimation work (Smart Focus + Classic AF fallback), the PHD2 lifecycle integration (aligned with the actual openastro-phd2 fork), the discoverability foundation (§0.5 design principles + §61 smart settings search + the COMMIT-PR-RULES settings-registry gate), the §60 API conventions, and the §30.7 equipment-change check. Captured PHD2 RPC gaps in a dedicated `design/PHD2-GAP.md` for later action.

### Tier 1 items resolved this session (8)

| Item | Resolution |
|---|---|
| §60 API conventions | Baked §60 |
| Meridian flip workflow | Baked §58 (with §57 Stop Mount + slew safety as foundation) |
| Autofocus orchestration | Baked §59 (Smart Focus + Classic AF fallback + collimation detection) |
| Dither policy | Baked §62 (with §30.7 equipment-change check as spillover) |
| PHD2 / openastro-phd2 lifecycle | Baked §63 (aligned with existing openastro-phd2 fork; gaps captured in `PHD2-GAP.md`) |
| Design principles preamble | Baked §0.5 (three pillars) |
| Smart settings search | Baked §61 (with settings-registry gate enforced via `COMMIT-PR-RULES.md`) |
| Equipment-change check on profile load | Baked §30.7 (spillover from dither + PHD2 work) |

### Tier 1 items still to discuss next session (in suggested order)

1. **Live view / loop imaging** — small section; naturally pairs with the imaging-pipeline work already done. Easy win.
2. **§36 Sky Atlas rescope** — scope-honesty call. Important to decide before Phase 12 since the AI will overbuild otherwise. (21 surveys + Tonight's Sky planetarium + Aladin Lite all in v0.0.1 is unrealistic; v0.0.1 should be DSS2 + Mellinger + basic catalog browsing only.)
3. **Image stretching pipeline + preview API knobs** — what stretches WILMA offers (linear / log / MTF/STF / asinh / manual), server-side cache strategy when user requests alternative stretches.
4. **Power loss durability** — SQLite WAL settings (`journal_mode=WAL`, `synchronous=NORMAL` vs `FULL`), fsync policy after FITS writes.
5. **Concurrency model on Pi during a session** — what runs in parallel (capture, preview gen, WS broadcast, backup stream, SQLite, real-time diagnostics, mDNS), backpressure rules, bounded queues.
6. **Storage permissions bootstrap** — who chowns `/media/openastroara` to the `openastroara` user; who mounts the drive at the configured UUID; server-side helper vs fstab vs DEPLOY.md guidance.
7. **Threat model paragraph** — one paragraph stating ARA's assumed adversary (none on a trusted LAN), what token auth protects, what it doesn't (sniffing on shared Wi-Fi, MITM).
8. **§51 diagnostics default mode** — should v0.0.1 default to `notify_only` rather than `balanced` until per-user thresholds are calibrated? Decide before §51 is locked.
9. **Phase 12 split confirmation** — already captured in `COMMIT-PR-RULES.md` draft (12a–12h). Need to confirm the split shape and mark this gap resolved.

### Parked work in other docs to revisit

- **`COMMIT-PR-RULES.md`** — per-phase PR rhythm + sub-PR splitting approved in principle; still need to (1) decide the open items in the "Things still to decide" list and (2) bake into `PORT_PLAYBOOK.md` (§0 rule 9, §3 phase plan, §19.1 git safety, §22 final pass). The **settings-registry gate** within this doc IS settled and applies from Phase 12 onward.
- **`PHD2-GAP.md`** — 3 real gaps in openastro-phd2 (dark-library progress events, structured equipment-disconnect events, `get_version` RPC) + 7 nice-to-haves + 7 doc clarifications. Decide whether to add them to openastro-phd2 or have ARA work around them.

### Suggested re-entry prompt for next session

> "Continuing the ARA design pass from 2026-05-21. Read `design/GAPS-ARA.md` session checkpoint; let's pick up [item]."

---

---

## Tier 1 (late additions — from review pass, 2026-05-19)

Found during a final review of the playbook. Worth v0.0.1 attention because they're foundational hygiene the AI would otherwise skip.

- [x] ~~**Accessibility (a11y) commitment**~~ → **Resolved §53**. WCAG 2.1 AA-leaning baseline (not formally certified). Color + symbol on status indicators (covers color-blind item), high-contrast theme variant (toggle in Settings → Display), Flutter Semantic widgets on custom controls, font scaling honored, reduce-motion respected, keyboard navigation on desktop, visible focus indicators, 4.5:1 minimum text contrast verified across `AraColors` tokens, screen-reader smoke test in Phase 11. Benefits color-blind users, aging astrophotographers, low-vision users, glare/night-vision conditions, keyboard users, outreach orgs needing ADA/Section 508/EAA compliance.
- [x] ~~**Color-blind friendly status indicators**~~ → **Resolved §53.2** (folded into a11y section). Shape + color on every status indicator: ✓ check / ⚠ warning / ⛔ no-entry / ○ disabled / ⓘ info. Reusable `StatusIndicator` Flutter widget used everywhere.
- [x] ~~**PII redaction in bug reports**~~ → **Resolved §54**. Review-first submission, not auto-redact-first (GPS data is genuinely useful for debug). User sees what's in the zip before submitting, with sensitivity-detected items highlighted (GPS, serial numbers, hostnames, paths, internal IPs). Four sharing modes: include-everything (default, best debug), coarse-GPS-only (round to ~111 km), redact-all (placeholder substitution), let-me-edit-zip-first (power-user escape). Always-blacklisted regardless of mode: API tokens, secrets, SSH keys, files outside ARA data dirs. Bug template auto-records "sharing mode used" so maintainer knows redaction level.

## Tier 1 (late additions — from second review pass, 2026-05-21)

Found during a second end-to-end review of the playbook. These are gaps the AI will hit during the port and either skip ("TODO(port)") or guess at, because the playbook references the feature but doesn't decide the user-visible policy. Spec before turning the AI loose on Phase 0.5.

**Inherited-from-NINA workflows that need explicit policy specs (the AI cannot guess these without producing bad UX):**

- [x] ~~**Meridian flip workflow**~~ → **Resolved §58** (with foundational §57 + new §61 spilling out of the same discussion). §58 specs the three timing knobs (`pause_before_min`, `pause_after_min`, `max_wait_after_min`), rule-of-thumb table per rig class (C14 → 1-2 min; RedCat on GEM → 10-15 min; strain-wave → 45-75 min; never-flip alt-az/fork-on-wedge → `enabled: false`), post-flip recovery (slew → plate-solve+re-center → conditional re-focus → guider re-cal `auto_restore` default), side-of-pier verification (no brand-quirk DB per §52), failure-handling matrix, first-flip-confirmation safety net (catches "I set the wrong number" before damage), four-layer unattended flip safety (pre-flight check → watchdog → post-flip verification gate → safe rest state with mount park), severity escalation during unattended hours, pre-sleep checklist surfaced before astronomical dusk, **10-minute unattended-failure graceful shutdown** with cooler warmup before camera disconnect, WebSocket events, API endpoints. Spillovers: §57 (Stop Mount contextual button during all autonomous slews + safety-speed slews at 50% default + hardware-kill-switch DEPLOY.md recommendation; horizon profile / HA limits / no-go polygons deferred to v0.1.0 Mount Safety v2); §61 (smart settings search — committed because the meridian-flip + mount-safety + safety-policy + notification settings proliferation would be lost in a NINA-style tree without it); §0.5 design principles preamble (codifies "Better than NINA / AI-assisted design / Robust + friendly + discoverable" as values the whole playbook serves).
- [x] ~~**Autofocus orchestration**~~ → **Resolved §59 (Smart Focus + Classic AF fallback)**. Pivoted from "spec NINA's AF behavior" to "design a smart algorithm that obsoletes most of NINA's 17 knobs." **Smart Focus** is the primary mode: after one-time ~5 min Classic AF calibration per profile, every subsequent AF completes in 2-3 exposures (30-90 s vs NINA's 3-5 min) by extracting a feature vector (HFR, FWHM, donut outer/inner diameter, ring thickness, asymmetry coefficient, peak-to-bg) from a single image and looking up the predicted focuser offset+direction in the calibration table. Telescope-type-aware (Refractor/SCT/Maksutov/RC/Newtonian/Other) drives which features matter. Six triggers (sequence start, time interval 90 min, temp Δ 1.5°C, HFR drift 15%, post-meridian-flip per §58, first use of filter). Filter policy: `use_current_filter` only — no swap-to-luminance because non-parfocal filters break that pattern. **Three-layer backlash auto-discovery**: probe routine appended to calibration (~60-90 s) + passive refinement every AF run + equipment-change auto-invalidation via Alpaca DriverInfo. Zero backlash questions in the wizard. **Collimation health detection** as free byproduct of calibration — measures donut centroid offset (outer ring vs inner shadow), reports severity (good/warning/critical at 5%/15% thresholds) with clock-position direction; advisory only in v0.0.1 (prescriptive screw-turning guidance deferred to v0.1.0 community knowledge base). **Diagnostic-skip integration with §51** defers AF during clouds/dew/aperture-blocked states. Fallback to Classic AF (9-step parabolic-with-hyperbolic-fallback curve, HocusFocus star detection preserved verbatim) when Smart Focus diverges. Schema reduced from NINA's 17 visible settings to 6 (everything else hidden in `advanced` disclosure or derived from observation). Wizard's telescope screen reduced from NINA's multi-page form to 5 questions. v0.1.0 paths: ML feature extraction, temp-aware backlash, prescriptive collimation guidance, refractor collimation detection, star-test mode, tilt-aware focus.
- [x] ~~**Dither policy**~~ → **Resolved §62** (with spillover §30.7 equipment-change check). PHD2 handles the actual mount nudging via JSON-RPC; ARA decides when/how/whether with three "better than NINA" defaults: **auto-compute magnitude from pixel scale** (target 5 arcsec angular displacement → computed dither_pixels per rig, clamped [3, 15]), **auto-disable for short-exposure workflows** (< 60 s threshold catches lunar/planetary/lucky-imaging without manual toggle), **diagnostic-aware skip** (don't waste 10-15 s settling during clouds/dew/aperture-blocked per §51 integration). Defaults: RA+Dec random pattern, settle 1.5 px for 10 s, settle timeout 60 s. Cross-meridian-flip behavior automatic via PHD2 auto-restore (per §58.4). No-guider fallback in v0.0.1 = disabled with one-time notification (direct-mount-pulse dither deferred to v0.1.0). Auto-reduce cadence on persistent timeout failures (graceful degradation). Wizard reduced to 2 dither questions, both default "auto." Per-target-type overrides via sequence templates (DSO dithers, lunar/planetary don't). Schema: 4 visible settings; settle params + diagnostic-skip in advanced disclosure. v0.1.0 paths: adaptive magnitude from walking-noise detection, per-filter cadence overrides, dither-quality scoring in §50 Stats. **Spillover §30.7** — equipment-change check fires on every subsequent profile load asking "has any gear changed?" Two-tap path for 95% no-change case; Alpaca DriverInfo auto-detection pre-checks suspicious changes; user confirms or unchecks. Invalidation matrix covers camera (dither magnitude recomputed), telescope/focal length (dither + AF + framing), focuser (Smart Focus + backlash), filter wheel (offsets + flat library), mount + pier/tripod/dovetail (§58 first-flip-confirm reset), guider (PHD2 cal). Banner on main app shell shows what's stale; lazy recalibration on next use. Calibration state stored in profile's `calibration_state` block. NINA has no equivalent — users discover stale calibrations the hard way.

**Operational features referenced but unspec'd:**

- [ ] **Live view / loop imaging mode** — §25.5 mentions a "Live View toggle" in the Imaging tab; nothing else. Users need continuous capture-without-sequence for framing and focus checks. Need: endpoint (`POST /api/v1/imaging/live/start` etc.), frame cadence, frame discard policy (no FITS persistence), WS event shape, interaction with cooler / guider / sequence state.
- [ ] **Image stretching pipeline + preview API knobs** — §40.2 says "stretched per the user's profile-default stretch setting" and "user can request alternative stretches via API," but the API isn't there. Need: which stretches WILMA offers (linear / log / MTF/STF / asinh / manual blackpoint+gamma), server-side preview cache strategy when user requests alternative stretches, who computes stretch (server for previews, client for re-stretch on cached image?), default per-filter or per-profile.

**API conventions — bundled (one short section unblocks every endpoint spec):**

- [x] ~~**§60 API conventions**~~ → **Resolved §60**. RFC 7807 problem+json error bodies with ARA-specific extension fields + 422 validation `errors` map keyed by JSON-pointer. Cursor-based pagination (opaque token, `limit`/`cursor` query, `items`/`next_cursor`/`has_more` response, default 50/max 500, `total` on first page only where useful). Request size caps: 10 MB JSON / 100 MB multipart / 64 KB WS, env-var overrides for the first two. `GET /api/v1/server/state` defined as the UI-rehydrate snapshot with a `ws_resume_token` enabling 1-hour WS-event replay on reconnect. `Idempotency-Key` header on mutating endpoints (24h dedup, 409 on body mismatch); required on sequence start, emergency stop, polar-align, server update, backup restore. Rate limiting minimal in v0.0.1 (token-retry only); per-endpoint limits deferred to v0.1.0 when remote-access mode arrives.

**Pi-side architecture gaps:**

- [ ] **Concurrency model on Pi during a session** — capture, preview gen (OpenCvSharp4), WS broadcast, optional §44 backup stream, SQLite writes, log writes, real-time diagnostics (§51), session DB updates, mDNS responder, USB writes. §44.4 hints at "capture-aware backoff" but no coherent model. On Pi 4 (4-core, 4 GB) this contention matters. Need: which tasks share threads, which have dedicated executors, what bounds queue depths (so memory doesn't grow unbounded if WILMA disconnects mid-stream), backpressure rules.
- [ ] **Power loss durability** — §28 covers crash recovery but not the data-safety question. Need: SQLite WAL settings (`PRAGMA journal_mode=WAL`, `synchronous=NORMAL` vs `FULL`), fsync policy after FITS write (most expensive part), tradeoff between throughput and durability. Mandatory USB design helps but doesn't eliminate "in-flight FITS in OS page cache when power dies."
- [x] ~~**PHD2 / openastro-phd2 lifecycle**~~ → **Resolved §63**. User flagged mid-spec that openastro-phd2 is already a designed-and-implemented fork at `github.com/open-astro/openastro-phd2` with the systemd lifecycle, headless mode (`xvfb-run -a ... --headless --headless-auto-connect`), 80+ documented JSON-RPC methods, profile management RPCs, `build_dark_library` + `build_defect_map_darks`, equipment selection RPCs (Alpaca + INDI + ASCOM COM transports), Alpaca discovery RPCs, algorithm-param RPCs, and a transitional .deb path from the old `phd2-alpaca` name. §63 aligns ARA Core's integration with the actual fork rather than re-inventing it. **Key behaviors baked:** systemd unit owned by the openastro-phd2 .deb (not ARA's job to ship one); user `openastro-phd2` + working dir `/var/lib/openastro-phd2`; `RestartSec=3`; ARA layers crash detection + hung-process detection + auto-restart backoff (1/5/15/30/60/120 s) on top of systemd; per-ARA-profile to PHD2-profile mapping (`ara-<slug>`) via `create_profile` / `set_profile_by_name`; full profile push from wizard (camera + mount + FL + algorithm params) via `set_profile_setup` + `set_selected_*` + `set_algo_param`; **critical precondition** that PHD2 rejects setters while equipment connected — ARA wraps `set_connected(false)` → push → `set_connected(true)` as atomic operation; dark library + defect map build from wizard with cover-the-scope modal flow; event stream ingestion via persistent TCP connection (NOT log-file tailing — the JSON-RPC socket carries events); graceful degradation when openastro-phd2 not installed; equipment-change pipeline from §30.7 invokes the disconnect-update-reconnect sequence; PHD2 version detection on connect (fork vs upstream PHD2 warning + continue). **Moved from v0.1.0 to v0.0.1:** profile push + dark library + per-profile mapping (no longer "deferred" — all already implemented in the fork; ARA just calls the documented RPCs). v0.1.0 paths: WILMA-pushed openastro-phd2 binary updates (§33.6, §55.1), AI-driven calibration tuning, multi-guider support, advanced algorithm tuner UI.
- [ ] **Storage permissions bootstrap** — §29.1.1 has WILMA-driven UUID config but never explains who actually mounts the USB at `/media/openastroara` or chowns it to the `openastroara` user. The .deb postinst (§34.3) can't do this because the drive isn't configured at install time. Need: server-side mount helper (privileged via sudoers like §33.5), or fstab line written by config flow, or just clearly delegate to §29.7's manual DEPLOY.md path. Right now it's hand-wavy.

**Security:**

- [ ] **Explicit threat model paragraph (§9 or §17)** — token auth (§9) defends against what? On AP mode with weak/no Wi-Fi password the token is sniffable; same on home Wi-Fi shared with untrusted devices. v0.0.1 is LAN-trusted; worth one paragraph stating the assumed adversary (none on a trusted LAN), what's protected (casual API access, binary integrity per §33.4), what's not (network sniffing, MITM). Sets expectations for users on shared Wi-Fi.

**Scope rescoping (be honest before the AI commits):**

- [ ] **§36 Sky Atlas — honest v0.0.1 rescope** — 21 surveys, Aladin Lite WebView integration, Tonight's Sky planetarium with DE440 ephemerides, universal search, comet motion math, bundled ~1 GB asset payload. Single biggest scope risk in the playbook. Realistic v0.0.1 is probably: Aladin catalog view + bundled DSS2 + Mellinger + basic offline search; Tonight's Sky planetarium → v0.1.0; full survey-manager polish → v0.1.0. Worth deciding now rather than letting the AI ship a half-finished planetarium.
- [ ] **Phase 12 split (12a/12b/12c)** — §3 phase plan packs ~50% of client engineering into Phase 12: app shell + 7 tabs (Imaging/Framing/Sequencer/Sky Atlas/Image Library/Stats/Settings) + a11y baseline + disconnect modal + safety UI + bug report flow + mobile companion shell selection. As specified the AI cannot make this gate green in one pass. Split into 12a (shell + first-run + Imaging + Equipment), 12b (Sequencer + Framing + Settings), 12c (Sky Atlas + Image Library + Stats + mobile companion).
- [ ] **§51 real-time diagnostics default mode** — 12 rule-based patterns with hard-coded thresholds, no per-user calibration until v0.1.0 (§51.9). Default `balanced` mode (acts on critical signals) will fire false positives during v0.0.1 launch until thresholds settle. Recommend either (a) shipping `notify_only` as v0.0.1 default + opt-in to `balanced`, or (b) marking auto-actions as "preview feature, off unless enabled in wizard." Decide before §51 is locked.

---

## Tier 2 (late additions — from second review pass, 2026-05-21)

Smaller gaps and edge cases. Worth covering but won't crash the port if briefly described.

- [ ] **Flutter WebView on Linux desktop verification** — §36 Sky Atlas relies on Aladin Lite in a WebView. Flutter's desktop WebView story on Linux is patchy across packages (`webview_flutter`, `flutter_inappwebview`, `desktop_webview_window`). Could silently break the Sky Atlas tab on `flutter build linux`. Need a Phase 11 verification step: pick a WebView package, prove it loads a non-trivial HTML page on Linux x64, document the fallback if it doesn't work (e.g., Sky Atlas tab degrades to "open in external browser" on Linux desktop).
- [ ] **Camera cooler warmup at session end** — §28 covers cooler ramp-up on recovery; nothing on graceful ramp-down at session end. Modern CMOS cameras tolerate fast warmup but cooled CCDs and some user setups prefer a controlled ramp (typically 1°C/min back to ambient before disconnect) to prevent thermal-shock condensation. Need: profile setting ("warmup on session end: off / ramp / immediate") + post-sequence cooldown phase in the sequencer.
- [ ] **"Paused sequence" semantics** — §28, §35, §42, §44 all reference "pause" but never define what keeps running. Need one paragraph: cooler keeps running (yes — temp stability matters when resuming), guider keeps running (yes — keeps mount stable), mount keeps tracking (yes — target stays centered), filter wheel/focuser/rotator stay where they were, in-flight exposure is aborted (lost frame). Doc somewhere obvious; users will ask.
- [ ] **DST / timezone handling** — §31 says UTC internally and client displays local. Open questions: Pi system clock during DST transition (RTC-less Pi is fine because UTC; with NTP it's also fine; with manual entry user gives UTC). Filename templates with `$$DATE$$` and `$$DATEMINUS12$$` — what TZ? Astronomical-twilight calculation uses site lat/long, not TZ, so it's robust. Mostly already correct but worth one paragraph confirming the policy.
- [ ] **Mosaic across RA wrap (0h/24h)** — §47.3 hand-waves "Spherical-projection corrections for large mosaics near the poles are needed in practice; ARA uses standard tangent-plane projection per NINA's existing math." The math at the RA wrap is a known foot-gun: panel-center RA computed as `center_ra + dx' / cos(center_dec)` can produce negative or >24h values. Need: confirm NINA's math handles this (it probably does), add one-line note in §47.3 acknowledging the limitation if any.
- [ ] **§23 bulk-rename sed safety** — `sed -i.bak 's/NINA\./OpenAstroAra\./g'` will rewrite the string inside license headers, error messages, and comments. §17.2 sacred-files list isn't excluded from the find. Need: either (a) add explicit excludes to the find command (`-not -path '*/LICENSE*'`, etc.), or (b) replace with a Roslyn-based rename via `dotnet-format` or csharp-Ls (slower but safer). At minimum, warn in §23 about the risk and recommend a dry-run grep first.

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
- [x] ~~**Mount-specific behavior profiles**~~ → **Resolved §52 (and pivoted)**. The original framing was wrong — Alpaca's ConformU certification eliminates most mount quirks at the protocol level, so ARA doesn't need a brand-profile system. Instead §52 commits to: Alpaca-only-forever (permanent architectural decision; INDI/INDIGO never native, bridges required); feature detection via Alpaca capability flags (CanFindHome, AxisRates, SlewSettleTime, etc.); sensible generic defaults in wizard (no brand-specific code); optional first-connect conformance check that surfaces driver bugs to upstream rather than working around them in ARA. NINA's brand-specific mount code is stripped in Phase 8.
- [x] ~~**Multi-user / read-only spectators**~~ → **Deferred to v0.1.0** (§27.4 already notes this). Single-client policy is correct for v0.0.1; spectator mode adds complexity (auth roles, read-only API surface, UI mode toggle) that isn't needed yet.
- [x] ~~**Live stacking**~~ → **Committed v0.1.0 feature** (not optional — explicit roadmap commitment). Star registration + sigma-clipped running stack + live preview during integration. Provides EAA (Electronically Assisted Astronomy) and "is this target worth tonight" instant feedback. ASIAir/SharpCap parity. Engineering work: real-time star detection, frame alignment, calibration application, memory management on Pi.
- [x] ~~**Equipment scripting / custom hooks**~~ → **Deferred to v0.1.0+** (folds into the plugin SDK design from §10). Power-user feature for custom integrations (dome control via GPIO scripts, webhook firing on frame complete, ancillary-equipment automation). v0.0.1 users who need this run external automation (cron, separate scripts watching the filesystem for new FITS files). v0.1.0 design pass introduces hooks + plugin SDK together as a cohesive extensibility layer.
- [x] ~~**Pi RTC hardware option**~~ → **DEPLOY.md documentation item.** Documented as optional hardware add-on (DS3231 / PiRTC modules); avoids the §31 time-sync waterfall when present. AI writes this into DEPLOY.md during the Phase 11 documentation pass.
- [x] ~~**Pre-built RPi OS image**~~ → **Deferred to v0.2.0** (per §34 — .deb on apt.openastro.net is v0.0.1 path; pre-flashed image is a polish-tier "make onboarding zero-friction" feature requiring CI image-build pipeline).
- [x] ~~**AlpacaBridge + openastro-phd2 WILMA-push updates**~~ → **Deferred to v0.1.0** (§33.6 already specs this). Same atomic-swap + rollback pattern; new endpoints per component; bundled binaries grow WILMA app size another ~50-100 MB combined.
- [ ] **AlpacaBridge enhancement: switch power levels for RPi devices** — outside ARA Core scope (AlpacaBridge-side work). Support PWM / value-based switches for RPi-attached devices like dew heaters with variable output, dimmable flat panels, voltage-controlled outputs. ARA Core (§42.4) already consumes the full Alpaca `ISwitch` interface so it's ready when AlpacaBridge supports it.
- [x] ~~**Bulk asteroid catalog**~~ → **Deferred to v0.1.0** (§36.8 already specs this). Targeted lookup ("Ceres", "433 Eros") works in v0.0.1; bulk MPC asteroid layer (~1.4M numbered asteroids) with smart-culling by magnitude/visibility is v0.1.0.
- [x] ~~**Survey downloader polish**~~ → **Deferred to v0.1.0** (§36 already calls this out). v0.0.1 ships functional-but-rough single-threaded sequential downloader; v0.1.0 adds parallel + resume-across-restarts + background mobile + `If-Modified-Since` incremental updates.

## Tier 3 (late additions — deferred to v0.1.0+, from review pass)

- [ ] **Planetary / lunar imaging workflow specifics** — `lunar.json` + `planetary.json` templates ship in v0.0.1 (§38.7) but specific workflows are NOT detailed: high-frame-rate ROI capture, SER file format for planetary stacking pipelines, surface-feature tracking. DSO is v0.0.1 primary; planetary becomes a dedicated v0.1.0 design pass.
- [ ] **Comet motion tracking during exposure** — comets move appreciably during long exposures (minutes). Pro tools update RA/Dec per exposure from orbital elements. We have comet catalog data (§36.9) and motion math but no motion-aware tracking during capture. v0.1.0.
- [ ] **Multi-device WILMA settings sync** — user with Mac + iPad + phone configures each separately in v0.0.1. v0.1.0 could store WILMA UI preferences server-side and sync across devices on connect.
- [ ] **TLS / remote-internet access** — §2.3 explicitly says no TLS in v0.0.1, LAN-only. Some users want to image from remote observatories over internet. v0.1.0 path: TLS termination + optional remote-access mode (with appropriate warnings); v0.0.1 documented workaround is VPN.

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
- ✅ Alpaca-only commitment + feature-detection mount handling (§52 — INDI/INDIGO never native; bridges only)
- ✅ Equipment scripting / custom hooks → deferred to v0.1.0 (folds into plugin SDK design from §10)
- ✅ Accessibility (a11y) baseline + color-blind friendly status indicators (§53)
- ✅ Bug report submission + PII handling (§54 — review-first, four sharing modes, always-blacklisted secrets)
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
