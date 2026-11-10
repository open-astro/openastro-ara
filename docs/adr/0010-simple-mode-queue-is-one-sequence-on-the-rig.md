# Simple Mode's queue is one sequence on the rig

Status: accepted

## Context

Simple Mode lets a person line up several targets for the night and walk away. The client that built that line-up may sleep, roam off the Wi-Fi or close, and the next target still has to start. The daemon runs one sequence at a time and already knows how to run a multi-target sequence, persist it and recover it after a crash.

## Decision

We decided (2026-11-02, [`design/briefs/simple-mode.md`](../../design/briefs/simple-mode.md)) that Simple Mode's queue is one sequence on the rig holding one target block per queued target, in order. Add to queue appends a block to that sequence, Start now moves a block to the front, and the client's plan squares are a view of it. The client keeps no queue of its own that the rig depends on.

## Rules

1. Add to queue creates the run without starting it, and each later pick appends a block to that same sequence. Scope: `client/openastroara_client/lib/screens/simple/simple_plan_panel.dart`, `client/openastroara_client/lib/state/simple_run_state.dart`. Checked by: the Simple plan panel test "Add to queue creates the run without starting it and keeps the sheet up for the next pick; again it appends".
2. A target has one block in the queue; picking a queued target again moves its block to the front rather than adding a second one. Scope: `client/openastroara_client/lib/screens/simple/simple_plan_panel.dart`, `client/openastroara_client/lib/state/simple_run_state.dart`. Checked by: the Simple plan panel test "a target already in the queue is not queued twice; Start now moves it to the front instead".
3. The move from one queued target to the next is made by the daemon's sequencer, with no client call between targets. Scope: `client/openastroara_client/lib/screens/simple/simple_plan_panel.dart`, `client/openastroara_client/lib/state/simple_run_state.dart`. Checked by: review (a client timer, listener or call that starts the next queued target).

## Alternatives rejected

- **A queue held in the client**, starting each target when the last one ends: simple to build, but the night stops when the client sleeps, which is exactly when it is meant to run.
- **One sequence per target with a queue service on the daemon**: each target stays a plain sequence, but it needs new daemon state, new routes and a recovery path that the single sequence already has.

## Consequences

- The queue survives the client going away, and a second client reading the sequence sees the same queue.
- The rig finds a target's block by its name, so one name is one block.
- Live edits to the queue are edits to a running sequence, so they go through the sequencer's mid-run editing rules.
- The full UI sees Simple Mode's queue as an ordinary sequence.

## How to reverse

A queue outside the sequence needs a daemon queue service with its own persistence and crash recovery, a migration of queued runs, and new routes for append, reorder and delete.

## Links

- [`design/briefs/simple-mode.md`](../../design/briefs/simple-mode.md) ("Mode screens")
- [`design/PORT_PLAYBOOK.md`](../../design/PORT_PLAYBOOK.md) §28 (sequence durability), §38.9 (live mid-run editing)
- ADR 0009
