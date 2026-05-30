# OpenAstro Ara

[![License: MPL 2.0](https://img.shields.io/badge/License-MPL%202.0-brightgreen.svg)](https://www.mozilla.org/en-US/MPL/2.0/)

A headless ASP.NET Core daemon (`OpenAstroAra.Server`) + cross-platform Flutter client (`OpenAstroAra.Client`) for deep-sky astrophotography. The server runs on **ARM64 Linux only** — Raspberry Pi 4/5 is the reference platform; other ARM64 SBCs (Orange Pi 5, Rock Pi, etc.) running Debian Trixie arm64 are best-effort per playbook §13.1. The client runs on macOS, iOS, Android, Windows, and Linux desktops from one Flutter codebase.

The product model is ASIAir-like: server runs the night, client is for planning and monitoring. Close the laptop, imaging keeps going.

## Status

**Pre-release** — actively being ported from NINA (the WPF-based [Nighttime Imaging 'N' Astronomy](https://nighttime-imaging.eu/) software by Stefan Berg and contributors). See `design/PORT_PLAYBOOK.md` for the full port plan and `design/PORT_PROGRESS.md` for current status. First release target: `v0.0.1-ara.1`.

## Lineage

OpenAstro Ara is a hard fork of NINA `master` (3.2 line). All inherited code retains the original `Stefan Berg and the N.I.N.A. Contributors` copyright headers per the MPL-2.0 license. See `LICENSE.txt`, `COPYING`, and `AUTHORS` for the inherited attribution; `NOTICE.md` with full lineage attribution will be added in Phase 15 per playbook §17.2.

## Repository

```text
openastro-ara/                       (this repo)
├── OpenAstroAra.Server/             ← .NET 10 headless daemon (Phase 4+)
├── OpenAstroAra.Core / Astrometry / Profile / Image / Equipment / Sequencer / PlateSolving / Test
│                                    ← inherited from NINA, ported during Phase 0.5
├── client/openastroara_client/      ← Flutter desktop+mobile client (Phase 11+)
└── design/                          ← port playbook + tracking docs (not shipped)
```

## License

[Mozilla Public License 2.0](LICENSE.txt) — same as upstream NINA.
