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

- **One screen:** a mode switcher with Polar align · Target · Focus · Preview · Run.
- **Settings:** open from the gear. "Show all options" switches to the full UI.
- **Run:** a quick plan builder only. It sets exposure and frame count per filter for the current target, and autofocus, dither and flats follow the profile's defaults. The sequence editor stays in the full UI only.
- **Phones:** they run Simple Mode only, with no Sequence tab, once Simple Mode exists. Until then, PR #1474's phone layout stands.
- **Desktop default:** the full UI stays the default on desktop.

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

## Open: a builder must not invent these

- What each mode's own screen shows: Polar align, Target, Focus and Preview each need a pass.
- The order of the modes. The current order follows the night's workflow.
- Which controls stay visible in both modes.
- The empty state: no rig connected, no frame yet.
- What Run shows after the plan finishes.
