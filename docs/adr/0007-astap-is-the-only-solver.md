# ASTAP is the only plate solver, and it runs on the rig

Status: accepted

## Context

NINA supports several plate solvers (ASTAP, astrometry.net, PlateSolve 2 and 3, All Sky Plate Solver, PinPoint, TheSkyX), most of them Windows-only. Each solver is another binary, another star-catalog download manager and another set of settings. Whether a frame solves depends mostly on a star database whose depth matches the rig's field of view, and those databases are hundreds of megabytes that the solving computer must read.

## Decision

We decided during the port ([`design/PORT_PLAYBOOK.md`](../../design/PORT_PLAYBOOK.md) §18.I) that ASTAP is the only plate solver Ara ships, configures or downloads databases for, and that the solver and its databases live on the rig. The daemon bundles OpenAstro's multi-core build of `astap_cli` at `/opt/openastroara/astap_cli`, migrates profiles that name a stock ASTAP binary to it, and downloads the star databases onto the rig's storage itself; the client only starts the download and shows its progress. When more than one database is installed the daemon picks one per solve from the rig's field of view. A failed hinted solve falls back to a blind ASTAP solve. Astrometry.net is deferred.

## Rules

1. The daemon's packaging ships `astap_cli` next to the daemon, and every default solver path names that bundled binary. Scope: `scripts/build-astap-cli.sh`, `OpenAstroAra.Server/Services/SolverPathMigration.cs`. Checked by: the ASTAP bundle guard tests in `scripts/tests/test_astap_cli_bundle_guards.py` and the solver path migration tests.
2. Profiles the daemon writes use the `astap` engine for both the primary and the blind solver. Scope: `OpenAstroAra.Server/Services/LegacyProfileBridge.cs`, `OpenAstroAra.Server/Services/ProfileSnapshotNormalizer.cs`, `OpenAstroAra.Server/Services/InMemoryProfileStore.cs`. Checked by: review (a profile default or bridge that writes an engine other than `astap`).
3. Star databases are downloaded by the daemon onto the rig and selected per solve from the field of view; the client never downloads them. Scope: `OpenAstroAra.PlateSolving/Solvers/AstapStarDatabase.cs`, `OpenAstroAra.PlateSolving/Solvers/ASTAPSolver.cs`. Checked by: the ASTAP star database tests and the ASTAP database package tests.

## Alternatives rejected

- **Astrometry.net as a second solver**: solves some frames ASTAP misses, but it is another binary and another index manager with 1 to 30 GB catalog series, for a gain the blind ASTAP retry mostly covers.
- **Solving on the client**: the client has more CPU, but every frame would cross the network before a centering slew, and the run would depend on the client staying awake.
- **Keep NINA's solver list**: no removal work, but most entries are Windows-only and cannot run on the Pi.

## Consequences

- One solver to tune, document and bundle; the setup wizard recommends databases from the rig's field of view.
- The databases share the rig's storage policy with the frames and profiles.
- NINA's other solver classes are still in `OpenAstroAra.PlateSolving/Solvers`, reachable only from a legacy profile value the daemon never writes.

## How to reverse

Adding a second solver means a second binary in the `.deb`, a second database manager on the rig, an engine choice in the profile and the wizard, and solver selection in the fallback path.

## Links

- [`design/PORT_PLAYBOOK.md`](../../design/PORT_PLAYBOOK.md) §18.I (plate solving), §36.10 (database recommendation)
- [#1094](https://github.com/open-astro/openastro-ara/issues/1094), [#1121](https://github.com/open-astro/openastro-ara/issues/1121), [#1136](https://github.com/open-astro/openastro-ara/issues/1136)
