# Planning runs in the client; the rig runs the night

Status: accepted

## Context

Planning a night (ranking tonight's targets, the optimal sub-exposure, framing and the field of view, filter advice) needs the site, the optics and the catalogs, but no equipment. People plan in the afternoon, indoors, often with the rig packed away. The Pi is the scarce computer: its CPU is busy with captures, plate solving and guiding while a run is on.

## Decision

We decided (2026-07-15, the retired `PORT_DECISIONS.md`) that planning compute belongs in the client, which must work with no rig, and that the rig keeps execution. Target ranking, optimal-sub math, framing and filter advice run in the client. A new daemon planning endpoint needs a stated reason, such as feeding the execution engine directly: the sky-model calibration under `/api/v1/planning/sky-model` is the one that exists, because the sequencer reads the fitted model.

## Rules

1. With no rig connected, the client ranks Tonight's Sky from its cached site and computes optimal sub-exposures locally. Scope: `client/openastroara_client/lib/state/sky_atlas/tonight_sky_state.dart`, `client/openastroara_client/lib/util/optimal_sub_local.dart`. Checked by: the tonight sky offline test "no server + cached site → ranks locally" and the optimal sub local tests.
2. A new daemon route that ranks targets, sizes sub-exposures, frames a target or advises filters states in the PR body which part of the execution engine reads its result. Scope: `OpenAstroAra.Server/Endpoints/*.cs`. Checked by: review (a new planning-shaped route with no execution consumer named in the PR body).

## Alternatives rejected

- **Planning on the rig** (NINA's model, where everything is one process): one implementation and one source of truth, but no planning without the rig powered and reachable, and planning load on the Pi during a run.
- **Both sides, the client only as a fallback**: works offline, but two implementations of the same math can drift apart.

## Consequences

- The planner works in the afternoon, on the couch, with the rig in its case.
- The client carries the catalogs and the math, so planning changes ship as client releases.
- The old daemon `GET /planning/optimal-sub` and `GET /planning/tonight` were removed; the client services named after them keep only the wire types.

## How to reverse

Moving planning back to the rig means daemon routes for each calculation, an offline fallback or no planning without the rig, and moving the tests with the math.

## Links

- [`design/README.md`](../../design/README.md) (citation keys for `PORT_DECISIONS 2026-07-15`)
- [`design/PORT_PLAYBOOK.md`](../../design/PORT_PLAYBOOK.md) §2 (the client works with no Pi)
- [#1140](https://github.com/open-astro/openastro-ara/issues/1140)
