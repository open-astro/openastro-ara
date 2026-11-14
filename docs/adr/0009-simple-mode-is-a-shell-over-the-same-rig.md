# Simple Mode is a second shell over the same rig

Status: accepted

## Context

The full UI follows NINA's layout and exposes everything, which suits people who came from NINA. ASIAir-style imagers want to get from "rig on" to "imaging running" and watch the night, on one screen, half dark-adapted. That audience needs far fewer controls, but a night they start has to behave exactly like one started from the full UI: same safety policies, same sequencer, same profile.

## Decision

We decided (2026-11-01, [`design/briefs/simple-mode.md`](../../design/briefs/simple-mode.md)) that Simple Mode is a second shell inside the same Flutter client, over the same daemon, profile and safety system as the full UI. It hides options and never removes them. It is a per-client choice, off by default on desktop. Behaviour both shells need lands in the daemon or in shared client state, so the two can never disagree about what a night does.

## Rules

1. Simple Mode adds no daemon route and no profile field that the full UI cannot reach: a route is open to both shells, and a profile field Simple Mode reads is one the full UI can edit. Scope: `client/openastroara_client/lib/screens/simple/*.dart`, `OpenAstroAra.Server/Endpoints/*.cs`, `OpenAstroAra.Server/Contracts/ProfileDtos.cs`. Checked by: review (a daemon route or profile field added for Simple Mode that the full UI can neither call nor edit).
2. Simple Mode's plans are built by the shared `createImagingRun`, so autofocus, dither and flats follow the profile as they do in the full UI. Scope: `client/openastroara_client/lib/screens/simple/simple_plan_panel.dart`, `client/openastroara_client/lib/state/simple_run_state.dart`, `client/openastroara_client/lib/state/sequencer/create_imaging_run.dart`. Checked by: the Simple plan test "a given filter plan becomes a fresh run with one loop per filter".
3. Both shells show Emergency Stop through the shared `emergency_stop_button.dart`; stopping a run, a device or the Polar Align Routine is an ordinary command and does not go through it. Scope: `client/openastroara_client/lib/screens/simple/simple_shell.dart`, `client/openastroara_client/lib/screens/app_shell.dart`, `client/openastroara_client/lib/widgets/emergency_stop_button.dart`. Checked by: review (a call to the daemon's emergency-stop route from Simple Mode that does not go through the shared button).
4. Simple Mode is chosen per client and is off by default on desktop. Scope: `client/openastroara_client/lib/state/simple_mode_state.dart`, `client/openastroara_client/lib/services/simple_mode_prefs_service.dart`. Checked by: the Simple Mode feature test "the toggle is saved in its own file, off by default".

## Alternatives rejected

- **A separate app for beginners**: a clean slate, but two codebases to build, sign and keep in step, and users would have to reinstall to grow out of it.
- **Slim down the one UI for everybody**: one shell to maintain, but it takes away what NINA users came for.
- **A simplified daemon profile or "easy" sequencer**: the simple flow could be tuned on its own, but a night started from Simple Mode would then behave differently from the same night started from the full UI, and safety would have two paths.

## Consequences

- A person can move between the shells mid-night and the rig does not notice.
- Every new daemon capability is available to both shells at once; Simple Mode decides only whether to show it.
- The client carries two layouts, and shared state must suit both.
- A daemon change made for Simple Mode's sake also changes the full UI's nights.

## How to reverse

Splitting Simple Mode off means its own state layer, its own daemon routes or profile fields, and a migration for profiles and plans made in one shell and opened in the other. Dropping it means removing `lib/screens/simple/` and moving phones back to their own layout.

## Links

- [`design/briefs/simple-mode.md`](../../design/briefs/simple-mode.md)
- [`DESIGN.md`](../../DESIGN.md) ("Simple Mode: Image First")
