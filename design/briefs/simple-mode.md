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
  - **right column, 168 px wide:** Next frame, then controls (Exposure, Filter, shutter, Gain), then Emergency Stop;
  - **bottom row, 56 px tall:** the guiding bar, the mode switcher and Stop.
- **Clean view:** clicking the image hides every panel except Emergency Stop and any alert. Clicking again brings them back.
- **Unchanged:** colours, status dots, Night-mode behaviour and Emergency Stop are as in the full UI.

## Scope

- **One screen:** a mode switcher with Preview · Polar align · Focus · Target · Run.
- **Settings:** open from the gear. "Show all options" switches to the full UI.
- **Run:** a quick plan builder only. It sets exposure and frame count per filter for the current target, and autofocus, dither and flats follow the profile's defaults. The sequence editor stays in the full UI only.
- **Phones:** they run Simple Mode only, with no Sequence tab, once Simple Mode exists. Until then, PR #1474's phone layout stands.
- **Desktop default:** the full UI stays the default on desktop.
- **Full UI untouched:** the full UI keeps its current layout (PR #1474's shell). Every change this brief leads to lands in Simple Mode only.

## On the image during a run

| Where | What |
|---|---|
| Top | Status strip: device dots, target and state, Show all options, search, Night mode, gear |
| Centre, under the strip | Attention banner, when something needs the person |
| Right column | Next frame (countdown, frame x/y, progress, HFR, sensor temperature); controls; Emergency Stop |
| Bottom-left | Guiding bar (RMS and RA/Dec trace) |
| Bottom-centre | Mode switcher |

The histogram is left out.

## Interaction

- **Size:** targets are at least 44 px, with 14 px text.
- **⌘K search:** finds every setting, including ones Simple Mode hides, and opens them in a sheet over Simple Mode without leaving it.
- **Night mode:** every panel must still read through the red filter.
- **Shortcuts:** none for now; the clean view toggles only by clicking the image.

## Mode screens (decided 2026-11-01)

- **Mode order:** Preview · Polar align · Focus · Target · Run.
- **Preview:** shoot and loop. The right column holds last-frame facts (HFR, stars, exposure, saved), then Exposure, Filter, a capture shutter, Loop/Single and Save frame. Nothing is saved unless the person taps Save.
- **Polar align:** a target drawn on the image (crosshair, 10′ ring, current pole, arrow). The right column shows the error, the zone (under 10′ green, under 60′ amber, otherwise red), the altitude and azimuth corrections, and Stop.
- **Focus:** a zoomed star with its HFR ring, the HFR, the focus curve, the position, Autofocus, and four nudge steps.
- **Target:** a full-screen planetarium. Tap an object, then Go to (slew and plate-solve centre). Search sits under the status strip.
- **Run finished:** a Night done summary (frames per filter, integration, best HFR, issues, flats, mount parked, then Image Library and New plan).
- **No rig / no frame:** tonight's planetarium behind a Connect to your rig card (discovered rigs, Add by address). Device dots go grey, the switcher dims, and Emergency Stop shows disabled.
- **Always visible in both modes:** Emergency Stop, the attention banner, device status dots, Night mode and the Simple ↔ full UI switch.

## Still open

- Exact copy for each state, and the error states inside each mode (solve failed, autofocus failed, slew blocked by the horizon).
- How Simple Mode's "Show all options" lands in the full UI (which tab opens).
