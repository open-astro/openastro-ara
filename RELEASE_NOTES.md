# Release Notes

## v0.0.1-ara.1 — first pre-release

**Status:** pre-release placeholder. The daemon serves the full v0.0.1 §60.x endpoint surface backed by the §28 SQLite catalog + §72 FITS file storage; equipment drivers and the §38 sequence orchestrator are still placeholders.

### Daemon (`OpenAstroAra.Server`)

**What works end-to-end:**
- §28 SQLite catalog (sessions + frames + notifications + diagnostic_events + app_config) persisting across daemon restart
- §72 FITS I/O via CFITSIO P/Invoke with §28.7 atomic-write (temp + rename + dir fsync)
- §65 stretch pipeline: 7 algorithms (auto_stf / linear / log / asinh / sqrt / equalized / manual), SkiaSharp JPEG encoding, disk-backed LRU variant cache, per-profile defaults, batch re-stretch jobs, §65.7 WS lifecycle events
- §60.9 WebSocket lifecycle: real upgrade handler, X-Ara-WS-Version validation, resume protocol with replay buffer, 30s ping / 60s pong heartbeat enforced via `KeepAliveTimeout`
- §37 profile round-trip — 12 sections (imaging-defaults, storage, notifications, site, filenames, safety-policies, autofocus, plate-solve, diagnostics-mode, PHD2, equipment-connection, stretch-defaults) persisted as `profile.json`
- §46.5 SQLite-backed notifications log + preferences
- §50 stats over the catalog (8 chart views)
- §51 diagnostics state + history + persisted mode
- §40.7 hfr-analysis aggregation
- §40.8 frame bulk ops (rate / tag / delete)
- §28.8 startup orphan FITS recovery
- §13 systemd-driven `/server/restart` with §34.7 `server.restart_imminent` WS event

**Placeholders (real impl still ahead):**
- §38 sequence orchestrator (CRUD wire shapes only; no actual capture loop yet)
- Real ASCOM Alpaca drivers per device (Camera / Mount / Focuser / etc. — discovery works; per-device actions return 202 placeholder)
- §51 monitor worker (the *reader* side is wired; the writer that produces real diagnostic events runs alongside §38)
- §44 backup stream + §43 ZIP backup
- §36 sky-data Data Manager

### Client (`OpenAstroAra.Client` Flutter)

- App shell + 7 main tabs (Imaging, Framing, Sequencer, Sky Atlas, Image Library, Stats, Settings)
- §37 wizard 18-screen flow + first-run handshake + mDNS discovery
- All 12 settings panels editable + persisted via the server
- §63 PHD2 settings panel
- §52.2 Alpaca device chooser across all 8 equipment panels
- ⌘K Smart Settings Search + Help Registry

### CI

- Cross-platform `client-test` matrix (Ubuntu / macOS / Windows)
- `server-build` cross-publish for linux-arm64, AOT-published Docker image, smoke gate that boots the daemon + probes WS upgrade + diagnostics + profile round-trip
- arm64 Docker e2e via qemu emulation
- libcfitsio install on Linux runner so `OpenAstroAra.Fits.Tests` exercises real entry points

### Deployment

- Distributable `.deb` packaging in CI (artifact-only; apt.openastro.net is post-v0.0.1)
- systemd `openastroara-server.service` unit (sample in §13)
- Reference platform: Raspberry Pi 4 (4 GB+) or Pi 5 on 64-bit Debian Trixie

### Known limitations

- Sequencer project (`OpenAstroAra.Sequencer`) still has 96 compile errors from NINA.WPF.Base references — out of the v0.0.1 daemon's transitive graph but unbuildable on its own. Targeted port pass tracked in `design/PORT_TODO.md`.
- No real cameras driven yet; placeholder Alpaca services return wire-shape-correct 202s
- No live FITS captures yet — sample frames seeded into the catalog give WILMA something to render but `/frames/{id}/download` returns 404 until §38 writes real frames

See [`design/PORT_PROGRESS.md`](design/PORT_PROGRESS.md) for the full sub-PR-by-sub-PR breakdown.
