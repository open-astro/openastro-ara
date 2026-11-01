# Simple Mode: design brief

Shaped 2026-11-01 (`/impeccable shape simple-mode`). Paperclip: ALP-4810. Rendered reference: [`design/preview/ara-design-preview.html`](../preview/ara-design-preview.html), Simple Mode toggle. Visual rules live in `DESIGN.md` ("Simple Mode: Image First"), and product decisions live in `PRODUCT.md`. The two-rail layout and Preview are prototyped on `feat/simple-mode` behind an experimental toggle (approved 2026-11-01); the other modes are not built yet.

## Job and audience

- **Who:** ASIAir-style imagers who want the rig to just work. They are at the telescope or on the couch, at night, often half dark-adapted.
- **Job:** get from "rig on" to "imaging running", then watch the night.
- **Kind of screen:** an operational screen, so being clear at a glance matters more than visual flair.

## Outcome

- **Success:** a user who never opens the full UI can polar-align, find a target, focus, check a preview, start a simple plan and monitor it until morning.
- **What makes it Ara:** the same daemon, profile and safety system as the full UI. Simple Mode only hides options and never removes them.

## Direction

- **Image first:** the latest frame fills the screen.
- **Simpler than ASIAir, not a copy of it.**
- **Controls:** they float on translucent panels (the two-rail layout in `DESIGN.md`):
  - **top:** a status strip (device dots, Search, Night mode, Show all options, Settings), with the attention banner centred under it when needed;
  - **top-left:** the camera status pill, at most 300 px wide;
  - **right rail, 72 px wide and only as tall as its controls:** the mode name (tap for the five modes as a grid), the shutter, the settings chip (the current mode's options: "5 s · 100" in Preview, "Auto" or "Manual" in Focus);
  - **bottom right:** the compact Emergency Stop on its own, a 60 px STOP with a 2 s hold;
  - **left rail:** Sky, Info and Library; Info shows the last frame's facts as one line under the strip, aligned right;
  - **bottom row, 56 px tall, 16 px from the left edge and from Stop:** the guiding graph and the histogram, each once it has data; a lone one fills the row, both split it equally with guiding on the left;
  - **popovers** (the mode grid, settings, a mode's panel) open left of the right rail and never move it.
- **Fixed slots:** nothing shifts when state changes; a panel with no data is not drawn.
- **Immersive view:** clicking the image hides every panel, Emergency Stop included; the status pill and any alert stay. Clicking again brings them back.
- **Status strip:** only connected devices show.
- **Unchanged:** colours, status dots, Night-mode behaviour and Emergency Stop are as in the full UI.

## Scope

- **One screen:** a mode grid with Preview · Focus · Polar align · Target · Run.
- **Settings:** open from the strip's gear. "Show all options" switches to the full UI. In slice 1 Settings, the device dots and ⌘K also switch to the full UI on the matching Settings page; a settings sheet over Simple Mode comes later.
- **Getting in:** turn on Settings → System → Experimental → Simple Mode (off by default, per client), then use the frame icon in the full UI's top-right cluster, between Night mode and the gear.
- **Run:** a quick plan builder only. It sets exposure and frame count per filter for the current target, and autofocus, dither and flats follow the profile's defaults. The sequence editor stays in the full UI only.
- **Phones:** they run Simple Mode only, with no Sequence tab, once Simple Mode exists. Until then, PR #1474's phone layout stands.
- **Desktop default:** the full UI stays the default on desktop.
- **Full UI untouched:** the full UI keeps its current layout (PR #1474's shell). Every change this brief leads to lands in Simple Mode only.

## On the image during a run

| Where | What |
|---|---|
| Top | Status strip: device dots, Search, Night mode, Show all options, Settings |
| Top-left, under the strip | Camera status pill |
| Top-right, under the strip | Info line (when on) |
| Centre, under the strip | Attention banner, when something needs the person |
| Left rail | Sky, Info, Library |
| Right rail | Mode name, shutter, settings chip |
| Bottom right | Emergency Stop |
| Bottom row | Guiding graph (RMS, RA/Dec trace) and histogram |
| Centre, over the image | Run's plan sheet; Night done when the plan finishes |

## Interaction

- **Size:** targets are at least 44 px, with 14 px text.
- **⌘K search:** finds every setting, including ones Simple Mode hides, and opens them in a sheet over Simple Mode without leaving it.
- **Night mode:** every panel must still read through the red filter.
- **Shortcuts:** none for now; the immersive view toggles only by clicking the image.

## Mode screens (decided 2026-11-01)

- **Mode order:** Preview · Focus · Polar align · Target · Run. On the right rail Polar align reads "P.A." (the full name doesn't fit 60 px); the mode grid spells it out.
- **Mode panels:** every mode except Preview opens its panel to the left of the right rail, a floating card like the settings popover, level with the rail's top. Run uses a centred sheet instead (decided 2026-11-01).
- **Preview:** shoot and loop, from the right rail: the shutter and the settings chip (Exposure, Gain, Filter, Loop/Single). The last frame's facts (HFR, stars, exposure · filter, gain · offset, clipped) come from Info. Single saves the frame (the daemon registers every Take One); Loop is live view and saves nothing. No Save frame button.
- **Polar align:** the polar-align camera's frames fill the image while it runs (nothing drawn on them). The shutter starts the routine (its square stops a Loop; in Single each tap takes the next frame). The mode panel holds Exposure and Loop/Single, and while it runs the error, the zone (under 10′ green, under 60′ amber, otherwise red), the bullseye, the altitude and azimuth turns, Stop and Done. The rail reads "P.A."; the chip reads "1 s · Loop".
- **Focus:** a zoomed star with its HFR ring top-left; the mode panel shows the HFR, the focus curve, the position, Autofocus, and four nudge steps.
- **Target:** a full-screen planetarium. Tap an object, then Go to (slew and plate-solve centre). Search sits in the mode panel.
- **Run:** the quick plan builder as a sheet centred over the image, the rails still visible; Start imaging closes it.
- **Run finished:** a Night done summary, in the same centred sheet (frames per filter, integration, best HFR, issues, flats, mount parked, then Image Library and New plan).
- **No rig / no frame:** tonight's planetarium behind a Connect to your rig card (discovered rigs, Add by address). Device dots go grey, the mode name dims, and Emergency Stop shows disabled.
- **Always visible in both modes:** Emergency Stop (except in the immersive view), the attention banner, device status dots, Night mode and the Simple ↔ full UI switch.

## Errors, horizon and the way out (decided 2026-11-01)

- **Errors inside a mode** (plate solve failed, autofocus failed, slew failed): while the person works a mode, the error shows inside that mode's panel, naming the cause and the fix, with a Retry (for example "Too few stars in a 2 s frame", then "Retry with 10 s"). During an unattended run the same errors use the centred attention banner.
- **Below the horizon (including the custom horizon):** Go to is disabled with the reason ("Below your horizon until 22:40"), and "Go to at 22:40" waits, then slews and centres on its own.
- **Show all options** opens the matching full-UI tab: Preview, Polar align and Focus open Live; Target opens Planning; Run opens Live while imaging and Sequence when idle.

## Still open

- Exact copy for every state and error, written against the daemon's real failure reasons.
- Where the attention banner goes while the Info line is showing (both sit under the strip).
