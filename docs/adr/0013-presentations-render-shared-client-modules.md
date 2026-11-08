# Presentations render shared client modules

Status: proposed

## Context

ADR 0009 says that behaviour both presentations need lands in the daemon or in shared client state. Three concepts have not followed it. The rules for commanding a device (ask first, in flight, confirmed, refused) live in widget State and are written per device in Simple Mode and again in the Full UI's filter picker. The Polar Align Routine is written twice, and the two copies have drifted: only the Full UI coalesces frame loads. The Guider's equipment choice is held by three surfaces, and the rule that the Guide Camera's UniqueID travels with the pick lives in a review comment. None of these can be tested without pumping a widget.

## Decision

We decided (2026-11-08, [#1698](https://github.com/open-astro/openastro-ara/issues/1698)) that each of these concepts is one module in client state, and that the Full UI and Simple Mode only render it. The device-command module owns ask first, intent in flight, stall detection and the refused or busy outcome. Both presentations ask before a park, a rotator move, a filter change, a flat panel change or a dome move while a plan runs; Emergency Stop and halts never ask. A move with no motion seen in the device's first stall time fails, and motion seen arms a longer backstop (10 s and 45 s for the filter wheel, which pilots the module). The Polar Align Routine module owns hydration, settings with rollback, coalesced frame loads and busy and error state. The Guider's equipment choice module owns discovery, friendly names, the UniqueID and the pick written to settings, for all three surfaces; its "can this be pushed" verdict stays a client mirror of the daemon's rule, so a picker can answer with no rig.

## Rules

1. A device window or picker renders the device-command module's phase and holds no timer, pending target or ask-first state of its own. Scope: `client/openastroara_client/lib/screens/simple/simple_device_windows.dart`, `client/openastroara_client/lib/widgets/imaging/exposure_controls_panel.dart`. Checked by: review (a migrated device whose widget State holds a stall timer, a pending target or an asking flag).
2. In both presentations a park, rotator move, filter change, flat panel change or dome move asks first while a plan runs, and Emergency Stop and halts never ask. Scope: `client/openastroara_client/lib/screens/simple/simple_device_windows.dart`, `client/openastroara_client/lib/widgets/imaging/exposure_controls_panel.dart`, `client/openastroara_client/lib/widgets/emergency_stop_button.dart`. Checked by: review (a listed move on a migrated device with no ask, or a stop or halt that asks).
3. The Polar Align Routine's hydration, settings, frame loads and busy and error state live in one module, and its frame loads are coalesced in both presentations. Scope: `client/openastroara_client/lib/widgets/imaging/polar_align_panel.dart`, `client/openastroara_client/lib/screens/simple/simple_polar_panel.dart`, `client/openastroara_client/lib/state/polar_align/polar_align_state.dart`. Checked by: review (a presentation that calls the polar align client directly or loads a frame itself).
4. A Guider equipment pick writes the Guide Camera's UniqueID on all three surfaces, through the one module. Scope: `client/openastroara_client/lib/widgets/guider/guider_setup_wizard.dart`, `client/openastroara_client/lib/screens/settings/panels/equipment_guider_panel.dart`, `client/openastroara_client/lib/screens/wizard/screens/screen_guiding.dart`, `client/openastroara_client/lib/screens/wizard/wizard_save.dart`. Checked by: review (a surface that holds its own discovered names or writes a pick by hand).

## Alternatives rejected

- **Ask first only in Simple Mode**: leaves the Full UI as it is, but a move that disturbs a plan is the same risk in either presentation.
- **Ask first on every device command**: also covers slews, focuser moves and cooler changes, but interrupts a screen built for direct control.
- **Keep ask first out of the module**: a smaller module, but five copies of the rule stay.
- **One 10 s stall timer**: simpler, but a slow wheel reports a failed change while it is still turning.
- **Ask the daemon for the "can this be pushed" verdict**: one rule and no mirror, but a new route, and a picker could not answer with no rig.

## Consequences

- The rules are tested with a fake device or client and no widget.
- The Full UI gains a question it never asked, which also goes beyond the Simple Mode brief's line that other changes land in Simple Mode only.
- Simple Mode gains coalesced frame loads, and first-run Guider setup gains the UniqueID pin.
- Simple Mode stops importing a Full UI panel file for the routine's helpers.

## How to reverse

Move each module's state back into its widgets, one copy per presentation or surface, and rewrite the tests as widget tests. Removing the Full UI's question is separate and cheap.

## Links

- [#1698](https://github.com/open-astro/openastro-ara/issues/1698)
- [#1308](https://github.com/open-astro/openastro-ara/issues/1308) (the Guide Camera pinned by UniqueID)
- ADR 0009
