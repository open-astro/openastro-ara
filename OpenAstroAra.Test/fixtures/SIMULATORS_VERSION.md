# Alpaca simulators — version pin

Per playbook §14.5.1. The ASCOM Alpaca simulators (OmniSim) are a load-bearing
test dependency: integration + E2E tests run the daemon against them. The binaries
are **not committed** — they are downloaded on demand from the pinned GitHub release
and verified against the SHA-256 checksums below before use. The download target is
`OpenAstroAra.Test/fixtures/alpaca-simulators/` (gitignored).

Source: https://github.com/ASCOMInitiative/ASCOM.Alpaca.Simulators
Pinned release: v0.4.0
Pinned SHA: 012a5778b4335b17332b9bffd8f3a0c561c727d8

Downloaded artifacts:
  - ascom.alpaca.simulators.linux-x64.tar.xz      sha256: c004a13c869473f4e2f110a231c2bcadbaa43ccabc324deb47bb7f2e79a5d6a6
  - ascom.alpaca.simulators.linux-aarch64.tar.xz  sha256: a39950f525075d6aaaa40ec2c13bc8dcc765a0acfbd728799cce1a2addef9bc9
  - ascom.alpaca.simulators.macos-x64.zip         sha256: 9a09303e548970a36b3e46143653261647a4f075674c1e6d17598fab97744140

Last verified: 2026-06-09
License: MIT — ASCOM.Alpaca.Simulators © 2021-25 Daniel Van Noord. We do not
redistribute the binaries (CI-downloaded artifacts, never committed), so the repo
stays clean of upstream binaries.

Upgrade policy: a weekly workflow (`.github/workflows/check-alpaca-simulators.yml`,
landing in a follow-up) opens an auto-PR when ASCOMInitiative publishes a newer
release; the PR re-pins this file (tag + SHA + checksums) and runs the integration
suite against the new build as a regression report.
