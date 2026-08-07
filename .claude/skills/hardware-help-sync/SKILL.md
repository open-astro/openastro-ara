---
name: hardware-help-sync
description: Audit the §69 hardware-aware help (info buttons) against the live AlpacaBridge supported-drivers list. Run whenever gear/equipment code changes land (equipment panels, Alpaca integration, drivers), when new hardware support ships, or on request. Keeps driverNotes vendor coverage in lib/help/registry.dart complete and accurate.
---

# hardware-help-sync

The info buttons ARE the manual (no separate help file will ship). Gear help
entries carry `driverNotes` — per-vendor addenda the help sheet shows as
"For your <device>" by matching the connected Alpaca device's name. This skill
keeps those notes in lockstep with what the AlpacaBridge actually supports.

## When to run

- After any change touching `client/openastroara_client/lib/screens/settings/panels/equipment_*.dart`,
  the equipment state providers, or AlpacaBridge-facing server code.
- When the user mentions new hardware (a new camera/mount/focuser brand or model).
- Periodically before a release.

## Steps

1. **Fetch the source of truth**: WebFetch
   `https://www.openastro.net/docs/supported-drivers` and extract the full
   vendor + model list per device type (cameras, mounts, focusers, filter
   wheels, rotators, cover calibrators, switches, weather).

2. **Audit `client/openastroara_client/lib/help/registry.dart`**:
   - Every gear help entry with `driverNotes` (currently
     `eq.camera.readout_mode`, `eq.camera.cooler`, `eq.camera.cooler_target`,
     `eq.mount.tracking`, `eq.mount.goto`, `eq.focuser.move`,
     `eq.rotator.move`) must have a note for **every vendor** of that device
     type on the supported list. Vendor keys are substrings matched
     case-insensitively against the Alpaca device name — verify the key
     actually appears in the device names that vendor's driver reports
     (e.g. "ZWO", "ToupTek", "QHY", "Player One", "SVBONY", "iOptron",
     "Celestron", "Sky-Watcher", "Gemini", "Astroasis", "WandererAstro").
   - Flag notes for vendors that DROPPED off the supported list (keep the
     note — harmless — but mention it).
   - If a new device TYPE gained live controls in a panel (e.g. filter wheel
     goto, cover calibrator brightness), add an ⓘ HelpIcon with a base entry
     + driverNotes, wiring `device: <status>.name` from that panel's provider.

3. **Write notes with substance**: model-specific facts (which models are
   cooled vs not, harmonic vs worm drive, absolute vs relative focuser,
   backlash quirks, recommended settings). Never filler like "works well".
   Match the existing voice: plain language for astrophotography novices,
   concrete numbers where they exist.

4. **Verify wiring**: each panel's gear HelpIcons pass `device:` from the
   connected status (`s.name`, or `.value?.name` for AsyncValue providers).

5. **Gate**: `flutter analyze` clean + `flutter test` green, then commit with
   a `docs(help): hardware sync — <what changed>` message.

## Invariants

- `Help.noteFor()` does substring matching — keep vendor keys unambiguous
  (never a key that appears inside another vendor's device names).
- Base entry body stays brand-neutral; ALL brand-specific text lives in
  `driverNotes`.
- Unlisted models from supported brands generally work (per the docs page) —
  write vendor notes to degrade gracefully for models beyond the tested list.
