# Rig Occupancy is one register in the daemon

Status: proposed

## Context

The daemon asks "is something running on the rig?" in eight places, and the answers differ. Five storage routes, the stats catalog rebuild, filter-offset calibration and the Bahtinov readout ask the catalog run-session registry, which is empty for a run whose catalog session failed to open. Profile switch adds the sequencer's active runs, which leave out an aborting run. Restart when idle adds every run that is not terminal. Preflight's running list leaves out an aborting run. None of them knows about the Polar Align Routine or Center unless a site adds its own line. So a storage reformat checks less than a profile switch does, and a new routine is visible only where someone remembered to add it.

## Decision

We decided (2026-11-21, [#2141](https://github.com/open-astro/openastro-ara/issues/2141)) that Rig Occupancy, what is running on the rig and what each routine holds, is one register in the daemon (`RigOccupancy`). A routine registers once, in the composition root, with what it holds (the mount, the main camera, the Guide Camera, the focuser, the disk) and a reading of its own running state. Every busy question asks the register and names the holdings it cares about. One busy rule holds for a Session: a run that is not terminal is running, so starting, running, paused, awaiting the person and aborting all count. The catalog run-session registry stays as the Session's own bookkeeping and feeds the Session's reading. This slice registers the Session, the Polar Align Routine and Center; the stop ladders (#2142) and the remaining routines (#2143) follow.

## Rules

1. A busy question asks `RigOccupancy` and names the holdings it cares about; it reads no run-session registry, run list or set of run states of its own. Scope: `OpenAstroAra.Server/Endpoints/SystemEndpoints.cs`, `OpenAstroAra.Server/Endpoints/StatsEndpoints.cs`, `OpenAstroAra.Server/Endpoints/ProfilesEndpoints.cs`, `OpenAstroAra.Server/Services/PlaceholderServerStateService.cs`, `OpenAstroAra.Server/Services/FilterOffsetCalibrationService.cs`. Checked by: review (a `HasAny`, `ActiveRunIdsAsync`, `UnfinishedRunIdsAsync` or list of run states in a busy check in these files).
2. A routine registers once, in the composition root, with what it holds and a reading of its own running state, and the register keeps no copy of that state. Scope: `OpenAstroAra.Server/Services/RigOccupancy.cs`, `OpenAstroAra.Server/Program.cs`. Checked by: review (an enter or exit call on the register, or a field in it that stores whether a routine runs).
3. A Session's run counts as running from start to a terminal state: paused, awaiting the person and aborting count. Scope: `OpenAstroAra.Server/Services/RigOccupancy.cs`, `OpenAstroAra.Server/Services/SequencerService.cs`, `OpenAstroAra.Server/Services/PreflightChecklistService.cs`. Checked by: `PreflightChecklistTest.The_running_list_counts_every_run_that_is_not_terminal`, `RigOccupancyTest.The_session_holds_the_rig_for_a_run_with_no_catalog_session`.
4. The register records that a routine holds the Guide Camera and never hands it over or refuses it; Guide Camera Custody (record 0016) decides who has it. Scope: `OpenAstroAra.Server/Services/RigOccupancy.cs`. Checked by: review (a custody call or a lease call in the register).

## Alternatives rejected

- **Enter and exit tokens**: a routine takes a token when it starts and gives it back when it ends. It reads well, but it is a second copy of each routine's state: the Polar Align Routine sets and clears its running state in about eight places, and a missed exit leaves the rig busy for good.
- **Keep one check per site, aligned by review**: no new module, but the eight sites drifted apart in exactly this way, and a new routine still needs a line at every site.
- **Ask the sequencer only**: one source for a Session, but the Polar Align Routine and Center stay invisible to every busy question.

## Consequences

- A run whose catalog session failed to open now refuses the storage routes, the stats rebuild, filter-offset calibration and the Bahtinov readout.
- An aborting run now refuses a profile switch and shows in Preflight's running list.
- Restart when idle and profile switch also wait for a Polar Align Routine or a Center; filter-offset calibration and the Bahtinov readout are also refused while either holds the main camera.
- The Polar Align Routine is registered as holding the main camera and the Guide Camera, because the register reads only whether it runs, not which camera a run uses.
- A refusal says which routine holds the rig ("polar alignment is running"), so the storage and profile-switch texts changed from "a sequence run is active" to "a sequence is running".

## How to reverse

Put a check back at each busy question and delete `RigOccupancy`. The costly part is not the code: each site has to choose its own state set again, and every new routine has to be added at each one.

## Links

- [#2141](https://github.com/open-astro/openastro-ara/issues/2141), [#2142](https://github.com/open-astro/openastro-ara/issues/2142), [#2143](https://github.com/open-astro/openastro-ara/issues/2143)
- [#1951](https://github.com/open-astro/openastro-ara/issues/1951) (restart when idle)
- ADR 0016 (Guide Camera custody)
