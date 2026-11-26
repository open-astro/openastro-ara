# Move Admission is one module in the Telescope service

Status: proposed

## Context

Whether the mount may move is decided in several places, and each place checks a different set of rules. The REST GoTo endpoint checked "Polar Align moves the mount", "a run is active", the lowest altitude and the counterweight, and the Telescope service checked the coordinate range, the mount's clock and its position. The mediator GoTo that serves a Session, Center and the meridian flip checked the range, parked, the clock, the position and the motion owner, and its comment said the missing horizon check was on purpose. Target recenter and the resume refinement checked the horizon themselves before they started. Each new limit (#2044 highest altitude and meridian timing, #2065 the counterweight model, #1589 meridian clearance) had to be added per caller, and a caller that was missed sent the move.

## Decision

We decided (2026-11-21, [#2139](https://github.com/open-astro/openastro-ara/issues/2139); the four open points settled by the user 2026-11-25) that Move Admission, the rig's one answer to "may this asker move the mount there?", is one module in the Telescope service (`MoveAdmission`). Every slew to a target, park and home names its asker (a person, a Session, Center, the meridian flip, the shutdown ladder, a safety park) and asks the module which rules bind it, before anything reaches the mount. The asker is an explicit parameter: the mediator's slew, park and home take it, and the ported centering solver gets it in its `CenterSolveParameter`; there is no ambient asker and no default. Target recenter and the resume refinement ask the module up front ("would this move be allowed?") and skip their work, as before.

This slice is a pure move: the table holds what each asker was checked for before the module, so the person at the rig sees no change. Today's table:

| Asker | Slew | Park, Home |
| --- | --- | --- |
| Person (REST) | range, clock, position, motion owner, Polar Align moving, run active (`run_in_progress`, `force` skips it), horizon, counterweight | position, motion owner, Polar Align moving |
| Session, Center, meridian flip | range, parked, clock, position, motion owner | Session and flip: position, motion owner |
| Shutdown ladder | cannot ask (no row) | position, motion owner |
| Safety and fault park | cannot ask (no row) | park: position; cannot ask for a home |
| Target recenter, resume refinement | asked up front: horizon | cannot ask (no row) |

The "run active" rule is the sequencer's active runs, as before (not finished and not aborting). Moving it onto Rig Occupancy (record 0025) is #2140's work. The rule that #2140 is to make true, "safety moves are the only askers exempt from position limits", is the target and does not hold today: a Session's slew, Center and the flip are not checked for the horizon or the counterweight.

## Rules

1. Every slew to a target, park and home in the Telescope service reads `MoveAdmission.RulesFor` with its asker before anything reaches the mount, and the rules that bind each asker are only in that table. Scope: `OpenAstroAra.Server/Services/MoveAdmission.cs`, `OpenAstroAra.Server/Services/TelescopeService*.cs`. Checked by: `MoveAdmissionTableTest.The_move_is_refused_only_by_the_rules_its_asker_is_checked_for`, review (a pre-move check in a move path that does not ask `MoveAdmission.Binds`).
2. A mediator slew, park and home, and a centering solve, name their asker explicitly; there is no default asker. Scope: `OpenAstroAra.Equipment/Interfaces/Mediator/ITelescopeMediator.cs`, `OpenAstroAra.PlateSolving/CenterSolveParameter.cs`. Checked by: review (an optional or defaulted `MoveAsker` parameter, or an ambient asker).
3. The endpoints hold no move rule: they hand the person's move to the Telescope service. Scope: `OpenAstroAra.Server/Endpoints/EquipmentEndpoints.cs`. Checked by: review (`HorizonGuard`, `CounterweightGuard` or `ActiveRunIdsAsync` in this file).
4. Target recenter and the resume refinement ask `MoveAdmission.TargetTooLow` before they start, and skip with their "too low" notice when it answers. Scope: `OpenAstroAra.Server/Services/TargetRecenter.cs`, `OpenAstroAra.Server/Services/SequencerService.ResumeRefinement.cs`. Checked by: review (a `HorizonGuard` call in these files).

## Alternatives rejected

- **An ambient asker (`AsyncLocal`) read at the seam**: no interface change, but a caller that forgets to set it gets a default, which is the missed caller this record exists to stop.
- **One module of rule bodies that each caller calls at today's place**: the safest move, but a new move path can still skip it, and the per-asker table is not in one place.
- **Refusing below the horizon at the seam for target recenter and resume refinement**: the Center would start and then fail at its first slew, with a different outcome and notice than today.

## Consequences

- A new limit is one row change in `MoveAdmission.RulesFor` and its rule body in the module, with one table test.
- The REST endpoint's horizon, counterweight, run-active and Polar Align checks moved into the Telescope service. Their Refusals, the `run_in_progress` Problem and their order are as before.
- The mediator's `SlewToCoordinatesAsync`, `ParkTelescope` and `FindHome` take a `MoveAsker`; every caller says who it is.
- The Polar Align Routine no longer GoTos (its turns are MoveAxis), so it is not a slew asker; MoveAxis stays out of the module.

## How to reverse

Put each rule back at its caller and delete `MoveAdmission` and the asker parameters. The cost is the one the module removes: each new limit goes back to being added per caller.

## Links

- [#2139](https://github.com/open-astro/openastro-ara/issues/2139), [#2140](https://github.com/open-astro/openastro-ara/issues/2140)
- [#2044](https://github.com/open-astro/openastro-ara/issues/2044), [#2065](https://github.com/open-astro/openastro-ara/issues/2065), [#1589](https://github.com/open-astro/openastro-ara/issues/1589), [#2073](https://github.com/open-astro/openastro-ara/issues/2073), [#2066](https://github.com/open-astro/openastro-ara/issues/2066), [#2070](https://github.com/open-astro/openastro-ara/issues/2070), [#2071](https://github.com/open-astro/openastro-ara/issues/2071)
- ADR 0025 (Rig Occupancy)
