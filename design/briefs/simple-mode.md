# Simple Mode: design brief

Shaped 2026-11-01 (`/impeccable shape simple-mode`). Paperclip: ALP-4810. Rendered reference: [`design/preview/ara-design-preview.html`](../preview/ara-design-preview.html), Simple Mode toggle. Visual rules live in `DESIGN.md` ("Simple Mode: Image First"), and product decisions live in `PRODUCT.md`. Nothing here is implemented yet.

## Job and audience

- **Who:** ASIAir-style imagers who want the rig to just work. They are at the telescope or on the couch, at night, often half dark-adapted.
- **Job:** get from "rig on" to "imaging running", then watch the night.
- **Kind of screen:** an operational screen, so being clear at a glance matters more than visual flair.

## Outcome

- **Success:** a user who never opens the full UI can polar-align, find a target, focus, check a preview, start a simple plan and monitor it until morning.
- **What makes it Ara:** the same daemon, profile and safety system as the full UI. Simple Mode only hides options and never removes them.

## Direction

- **Image first:** the latest frame fills the screen.
- **Controls:** they float on translucent panels (layout C in `DESIGN.md`):
  - **top:** a status strip, with the attention banner centred under it when needed;
  - **right column, 168 px wide:** the mode's panel (in Preview: controls on top, the last frame's facts at the bottom), then the mode list, then Emergency Stop;
  - **bottom row, 56 px tall:** stats, the guiding graph and the histogram, each once it has data.
- **Clean view:** clicking the image hides every panel, Emergency Stop included; any alert stays. Clicking again brings them back.
- **Status strip:** only connected devices show.
- **Unchanged:** colours, status dots, Night-mode behaviour and Emergency Stop are as in the full UI.

## Scope

- **One screen:** a mode list with Preview · Focus · Polar align · Target · Run.
- **Settings:** open from the gear. "Show all options" switches to the full UI. In slice 1 the gear, the device dots and ⌘K also switch to the full UI on the matching Settings page; a settings sheet over Simple Mode comes later.
- **Getting in:** a Simple Mode icon in the full UI's top-right cluster, between Night mode and the gear.
- **Run:** a quick plan builder only. It sets exposure and frame count per filter for the current target, and autofocus, dither and flats follow the profile's defaults. The sequence editor stays in the full UI only.
- **Phones:** they run Simple Mode only, with no Sequence tab, once Simple Mode exists. Until then, PR #1474's phone layout stands.
- **Desktop default:** the full UI stays the default on desktop.
- **Full UI untouched:** the full UI keeps its current layout (PR #1474's shell). Every change this brief leads to lands in Simple Mode only.

## On the image during a run

| Where | What |
|---|---|
| Top | Status strip: device dots, target and state, Show all options, search, Night mode, gear |
| Centre, under the strip | Attention banner, when something needs the person |
| Right column | The mode's panel; the mode list; Emergency Stop |
| Bottom row | Stats: guiding graph (RMS, RA/Dec trace) and histogram |

The histogram is left out.

## Interaction

- **Size:** targets are at least 44 px, with 14 px text.
- **⌘K search:** finds every setting, including ones Simple Mode hides, and opens them in a sheet over Simple Mode without leaving it.
- **Night mode:** every panel must still read through the red filter.
- **Shortcuts:** none for now; the clean view toggles only by clicking the image.

## Mode screens (decided 2026-11-01)

- **Mode order:** Preview · Focus · Polar align · Target · Run.
- **Preview:** shoot and loop. One panel: Exposure, Filter, a capture shutter and Loop/Single on top; after a frame, its facts (HFR, stars, exposure, gain · offset, clipped) at the bottom. Single saves the frame (the daemon registers every Take One); Loop is live view and saves nothing. No Save frame button.
- **Polar align:** a target drawn on the image (crosshair, 10′ ring, current pole, arrow). The right column shows the error, the zone (under 10′ green, under 60′ amber, otherwise red), the altitude and azimuth corrections, and Stop.
- **Focus:** a zoomed star with its HFR ring, the HFR, the focus curve, the position, Autofocus, and four nudge steps.
- **Target:** a full-screen planetarium. Tap an object, then Go to (slew and plate-solve centre). Search sits under the status strip.
- **Run finished:** a Night done summary (frames per filter, integration, best HFR, issues, flats, mount parked, then Image Library and New plan).
- **No rig / no frame:** tonight's planetarium behind a Connect to your rig card (discovered rigs, Add by address). Device dots go grey, the switcher dims, and Emergency Stop shows disabled.
- **Always visible in both modes:** Emergency Stop, the attention banner, device status dots, Night mode and the Simple ↔ full UI switch.

## Errors, horizon and the way out (decided 2026-11-01)

- **Errors inside a mode** (plate solve failed, autofocus failed, slew failed): while the person works a mode, the error shows inside that mode's panel, naming the cause and the fix, with a Retry (for example "Too few stars in a 2 s frame", then "Retry with 10 s"). During an unattended run the same errors use the centred attention banner.
- **Below the horizon (including the custom horizon):** Go to is disabled with the reason ("Below your horizon until 22:40"), and "Go to at 22:40" waits, then slews and centres on its own.
- **Show all options** opens the matching full-UI tab: Preview, Polar align and Focus open Live; Target opens Planning; Run opens Live while imaging and Sequence when idle.

## Still open

- Exact copy for every state and error, written against the daemon's real failure reasons.
