# OpenAstro Ara

#### [0.0.1] - 2026-11-19 · [Changelog](CHANGELOG.md)

[![Server license: MPL 2.0](https://img.shields.io/badge/Server%20License-MPL%202.0-brightgreen.svg)](https://www.mozilla.org/en-US/MPL/2.0/) [![Client license: AGPL v3](https://img.shields.io/badge/Client%20License-AGPL%20v3-blue.svg)](https://www.gnu.org/licenses/agpl-3.0.html)

A headless ASP.NET Core daemon on .NET 10 (`OpenAstroAra.Server`) + a cross-platform Flutter client (`client/openastroara_client`) for deep-sky astrophotography. The daemon controls all equipment **exclusively over ASCOM Alpaca (no COM)** — which is what lets it run headless on Linux. It **deploys on ARM64 Linux** — Raspberry Pi 4/5 is the reference platform; other ARM64 SBCs (Orange Pi 5, Rock Pi, etc.) running Debian Trixie arm64 are best-effort per playbook §13.1. The client runs on **Windows, iOS, Linux, macOS, and Android** from one Flutter codebase, and discovers the daemon on the LAN via mDNS.

The product model is ASIAir-like: server runs the night, client is for planning and monitoring. Close the laptop, imaging keeps going.

## Status

**Pre-release.** No version has been tagged yet. The first release will be **`v1.0.0-beta.1`**. Releases are **source only**: a tag, plain-language notes and a source tarball, with no installer, `.deb` or app download. You build the daemon and the app yourself; see [`docs/RUNNING.md`](docs/RUNNING.md) and [`docs/RELEASES.md`](docs/RELEASES.md). Ported from NINA (the WPF-based [Nighttime Imaging 'N' Astronomy](https://nighttime-imaging.eu/) software by Stefan Berg and contributors). See `design/PORT_PLAYBOOK.md` for the product spec; open work is tracked as GitHub issues labelled `P1`–`P5`.

> **Contributions are paused.** Outside pull requests, issues and comments are temporarily disabled.

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
├── client/openastroara_client/      ← Flutter desktop + mobile client (Phase 11+)
└── design/                          ← port playbook + tracking docs (not shipped)
```

## Documentation

- [`docs/RUNNING.md`](docs/RUNNING.md) — build & run the daemon + client from source on Linux, macOS, or Windows.
- [`docs/DEPLOY.md`](docs/DEPLOY.md) — set up a daemon you built from source on a Raspberry Pi (production).
- [`docs/RELEASES.md`](docs/RELEASES.md) — versions, betas, stable branches and how to build a release.
- [`docs/USER_GUIDE.md`](docs/USER_GUIDE.md) — using ARA once it's installed.
- [`docs/releases/`](docs/releases/README.md) — plain-language notes for each release (full change history in [`CHANGELOG.md`](CHANGELOG.md)).

## License

Split by directory (see `NOTICE.md`; the 2026-07-01 decision is `PORT_DECISIONS 2026-07-01`, resolved via `design/README.md`):

- **Server/daemon and everything else:** [Mozilla Public License 2.0](LICENSE.txt) — same as upstream NINA, whose derived files keep their MPL lineage.
- **Client (`client/openastroara_client/`):** [GNU AGPL v3 or later](client/openastroara_client/LICENSE) — the client is wholly original work; AGPL keeps derived clients open even when served from a device rather than shipped.

The client and daemon talk over REST/WebSocket as separate programs, so the licenses apply independently.
