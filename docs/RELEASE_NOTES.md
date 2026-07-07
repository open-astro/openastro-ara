# Release Notes

## v0.0.1-ara.1 — first pre-release

**Status:** pre-release. The daemon drives real ASCOM Alpaca equipment end-to-end — discover → connect → capture → FITS → catalog → preview — and executes saved sequences through the ported NINA engine. Guiding runs against the external `openastro-guider` daemon. A few subsystems remain placeholders (see below).

### Daemon (`OpenAstroAra.Server`)

**What works end-to-end:**
- **Real ASCOM Alpaca device drivers** (discover → connect → live status + control): Camera, Telescope/Mount, Focuser, FilterWheel, Rotator, Dome, Switch, SafetyMonitor, ObservingConditions, FlatDevice/CoverCalibrator. §32.4-cached runtime; §60.5 202-Accepted lifecycle. Each also serves the Sequencer's mediator interface (one singleton) so sequences drive the live hardware.
- **Real camera capture pipeline** (§14e): start an exposure over REST → the daemon drives the live camera, downloads the image, writes a §72 FITS file into your save directory, and registers it in the §28 catalog — frame list / preview / thumbnail / download serve it immediately. Cooler control + exposure abort drive the live device.
- **Real sequence execution** (§38): saved sequences (incl. NINA imports + the §38.7 starter templates) run through the ported NINA `Sequencer` — full container/condition/loop/trigger semantics — with §60.9 WS lifecycle events + §28 active-run checkpoint + §28.2 startup reconciliation. The re-ported `TakeExposure` instruction produces real catalog frames; sequences can drive mount/focuser/filterwheel/rotator/dome/switch/guider.
- **Real PHD2 guiding** (§63): ARA's re-ported PHD2 JSON-RPC client drives the external `openastro-guider` daemon — connect/status, start/stop guiding, dither, live state + rolling guiding RMS — over both REST and the sequencer's guide instructions.
- **Real cross-epoch astrometry** (§14e): SOFA/NOVAS natives build for Linux/macOS, so J2000↔JNOW slews are precession-corrected.
- §28 SQLite catalog (sessions + frames + notifications + diagnostic_events + app_config) persisting across daemon restart.
- §72 FITS I/O via CFITSIO P/Invoke with §28.7 atomic-write (temp + rename + dir fsync) + §28.8 startup orphan recovery.
- §65 stretch pipeline: 7 algorithms (auto_stf / linear / log / asinh / sqrt / equalized / manual), SkiaSharp JPEG encoding, disk-backed LRU variant cache, per-profile defaults, batch re-stretch, §65.7 WS events.
- §60.9 WebSocket lifecycle: real upgrade handler, X-Ara-WS-Version validation, resume protocol with replay buffer, 30s ping / 60s pong heartbeat.
- §37 profile round-trip — 12 sections persisted as `profile.json`; the live NINA profile hydrates from it so executing instructions read user-edited settings (§14e profile source-of-truth).
- §46.5 SQLite notifications log + preferences; §50 stats (8 chart views); §51 diagnostics state/history/mode; §40.7 hfr-analysis; §40.8 frame bulk ops; §13 systemd-driven `/server/restart` with §34.7 imminent-restart WS event.
- **Real Live View** (§64): short-exposure loop on the connected camera rendered to JPEG via the §65 stretch path (ephemeral — never catalogued), with OSC debayering to real colour; the client's Imaging-tab Live View toggle drives it.
- **§36 sky-data Data Manager**: downloads the real HYG star + OpenNGC deep-sky catalogs (commit-pinned, SHA-256-verified, atomic install), serving Tonight's Sky ranking + planetarium catalog overlays.
- **§43 configuration backup**: ZIP snapshots with retention pruning + restore (local or from another daemon's snapshot URL, SHA-256-gated).

**Still placeholders (real impl ahead):**
- Polar alignment (§45) — will drive the `openastro-guider` daemon's polar-align API.
- §44 backup stream.
- DSLR RAW decoding (libraw).

### Client (`OpenAstroAra.Client` Flutter)

- App shell + 7 main tabs (Imaging, Framing, Sequencer, Sky Atlas, Image Library, Stats, Settings).
- §37 wizard 18-screen flow + first-run handshake + mDNS discovery.
- All 12 settings panels editable + persisted via the server; §63 PHD2 settings panel.
- §52.2 Alpaca device chooser across all equipment panels; ⌘K Smart Settings Search + Help Registry.

### CI

- Cross-platform `client-test` matrix (Ubuntu / macOS / Windows).
- `server-build` cross-publish for linux-arm64, AOT-published Docker image, smoke gate that boots the daemon + probes WS upgrade + diagnostics + profile round-trip; arm64 Docker e2e via qemu.
- libcfitsio install on the Linux runner so the FITS tests exercise real entry points; pinned ASCOM OmniSim Alpaca simulators for live discovery/connect integration tests.

### Deployment

- Distributable `.deb` packaging in CI; releases are served from the **apt.openastro.net** APT repository (`sudo apt install openastroara-server` — see `docs/DEPLOY.md`).
- systemd `openastroara-server.service` unit (sample in §13). Guiding requires the sibling `openastro-guider` daemon on the Pi.
- Reference platform: Raspberry Pi 4 (4 GB+) or Pi 5 on 64-bit Debian Trixie.

### Known limitations

- DSLR RAW decoding (libraw) not yet integrated.
- Polar alignment (§45) not yet implemented.
- Dependency licenses audited release-safe (MPL-2.0-compatible; no GPL/AGPL/commercial in the daemon); `3rd-party-licenses.txt` at the repo root is generated from the full NuGet graph and kept fresh by CI.

See [`design/PORT_PROGRESS.md`](../design/PORT_PROGRESS.md) for the full sub-PR-by-sub-PR breakdown.
