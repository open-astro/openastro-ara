# OpenAstro Ara

[![Server license: MPL 2.0](https://img.shields.io/badge/Server%20License-MPL%202.0-brightgreen.svg)](https://www.mozilla.org/en-US/MPL/2.0/) [![Client license: AGPL v3](https://img.shields.io/badge/Client%20License-AGPL%20v3-blue.svg)](https://www.gnu.org/licenses/agpl-3.0.html)

A headless ASP.NET Core daemon on .NET 10 (`OpenAstroAra.Server`) + a cross-platform Flutter client (`client/openastroara_client`) for deep-sky astrophotography. The daemon controls all equipment **exclusively over ASCOM Alpaca (no COM)** — which is what lets it run headless on Linux. It **deploys on ARM64 Linux** — Raspberry Pi 4/5 is the reference platform; other ARM64 SBCs (Orange Pi 5, Rock Pi, etc.) running Debian Trixie arm64 are best-effort per playbook §13.1. The client runs on **WILMA — Windows, iOS, Linux, macOS, and Android** — from one Flutter codebase, and discovers the daemon on the LAN via mDNS.

The product model is ASIAir-like: server runs the night, client is for planning and monitoring. Close the laptop, imaging keeps going.

## Status

**Pre-release** — **v0.0.1 is feature-complete and pending its first tagged release (`v0.0.1-ara.1`)**; v0.1.0 feature work is already underway. Ported from NINA (the WPF-based [Nighttime Imaging 'N' Astronomy](https://nighttime-imaging.eu/) software by Stefan Berg and contributors). See `design/PORT_PLAYBOOK.md` for the full port plan and `design/PORT_PROGRESS.md` for current status.

## Lineage

OpenAstro Ara is a hard fork of NINA `master` (3.2 line). All inherited code retains the original `Stefan Berg and the N.I.N.A. Contributors` copyright headers per the MPL-2.0 license. See `LICENSE.txt`, `COPYING`, and `AUTHORS` for the inherited attribution, and `NOTICE.md` for the full lineage attribution and the per-directory license split (MPL-2.0 server / AGPL-3.0 client).

## Repository

```text
openastro-ara/                       (this repo)
├── OpenAstroAra.Server/             ← ASP.NET Core daemon on .NET 10 (Phase 4+)
├── OpenAstroAra.Core / Astrometry / Profile / Image / Equipment / Sequencer / PlateSolving / Test
│                                    ← inherited from NINA, ported during Phase 0.5
├── OpenAstroAra.Fits / Stretch      ← FITS I/O + stretch/preview (with *.Tests projects)
│                                    ← plus OpenAstroAra.TestHarness (virtual-observatory bench)
├── client/openastroara_client/      ← WILMA: Flutter desktop + mobile client (Phase 11+)
└── design/                          ← port playbook + tracking docs (not shipped)
```

## Documentation

- [`docs/RUNNING.md`](docs/RUNNING.md) — build & run the daemon + client from source on Linux, macOS, or Windows.
- [`docs/DEPLOY.md`](docs/DEPLOY.md) — install a released build on a Raspberry Pi (production).
- [`docs/USER_GUIDE.md`](docs/USER_GUIDE.md) — using ARA once it's installed.
- [`docs/RELEASE_NOTES.md`](docs/RELEASE_NOTES.md) — per-release highlights (full change history in [`CHANGELOG.md`](CHANGELOG.md)).

## License

Split by directory (see `NOTICE.md` and `design/PORT_DECISIONS.md` 2026-07-01):

- **Server/daemon and everything else:** [Mozilla Public License 2.0](LICENSE.txt) — same as upstream NINA, whose derived files keep their MPL lineage.
- **WILMA client (`client/openastroara_client/`):** [GNU AGPL v3 or later](client/openastroara_client/LICENSE) — the client is wholly original work; AGPL keeps derived clients open even when served from a device rather than shipped.

The client and daemon talk over REST/WebSocket as separate programs, so the licenses apply independently.
