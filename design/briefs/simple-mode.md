# Simple Mode: design brief

Shaped 2026-11-01 (`/impeccable shape simple-mode`). Paperclip: ALP-4810. Rendered reference: [`design/preview/ara-design-preview.html`](../preview/ara-design-preview.html), Simple Mode toggle. Visual rules live in `DESIGN.md` ("Simple Mode: Image First"), and product decisions live in `PRODUCT.md`. The two-rail layout and all five modes are prototyped on `feat/simple-mode` behind an experimental toggle (layout approved 2026-11-01).

## Job and audience

- **Who:** ASIAir-style imagers who want the rig to just work. They are at the telescope or on the couch, at night, often half dark-adapted.
- **Job:** get from "rig on" to "imaging running", then watch the night.
- **Kind of screen:** an operational screen, so being clear at a glance matters more than visual flair.

## Outcome

- **Success:** a user who never opens the full UI can polar-align, find a target, focus, check a preview, start a simple plan and monitor it until morning.
- **What makes it Ara:** decided in ADR 0009.

## Direction

- **Image first:** the latest frame fills the screen.
- **Simpler than ASIAir, not a copy of it.**
- **Controls:** they float on translucent panels (the two-rail layout in `DESIGN.md`):
  - **top:** a status strip (device dots, Night mode, Settings), with the attention banner centred under it when needed;
  - **top-left:** the message pills (results, errors, questions), up to 420 px; see `simple-mode-pills.md`;
  - **top-right:** the camera status pill, at most 300 px wide (half the row on a narrow window), sharing the row with the message pills;
  - **right rail, 72 px wide and only as tall as its controls:** the mode name (tap for the five modes as a grid), the shutter, the settings chip (the current mode's options under the settings icon: "5 s · 100" in Preview, "Auto" or "Manual" in Focus);
  - **bottom right:** the compact Emergency Stop on its own, a 60 px STOP with a 2 s hold;
  - **left rail:** Sky, Info and Library; Info shows the last frame's facts as fact tiles in the bottom-left corner;
  - **bottom row, 56 px tall, 16 px from the left edge and from Stop:** the guiding graph and the histogram, each once it has data; a lone one fills the row, both split it equally with guiding on the left;
  - **popovers** (the mode grid, settings, a mode's panel) open left of the right rail and never move it.
- **Fixed slots:** nothing shifts when state changes; a panel with no data is not drawn.
- **Immersive view:** clicking the image hides every panel, Emergency Stop included; the status pill and any alert stay. Clicking again brings them back.
- **Status strip:** only connected devices show.
- **Unchanged:** colours, status dots and Night-mode behaviour are as in the full UI. Emergency Stop does the same thing with the same 2 s hold, but Simple Mode's copy is its own: a compact STOP square with a ring, filled #D32F2F so its label reads at 4.5:1 (WCAG 2.2 AA), and keyboards press it twice instead of getting the confirm dialog.

## Scope

- **One screen:** a mode grid with Autopilot · Preview · Focus · Polar align · Plan.
- **Settings:** the strip's gear opens a menu: System (the server's figures: storage free, CPU, memory, uptime), the rig and profile with Change profile and Change rig, and All settings, which opens Settings over Simple Mode. Device dots and ⌘, open Settings over Simple Mode too. The strip's Full UI button, between Night mode and Settings, is the one way to the full UI (#1677, 2026-11-09).
- **Getting in:** turn on Settings → System → Experimental → Simple Mode (off by default, per client), then use the frame icon in the full UI's top-right cluster, between Night mode and the gear.
- **Plan** (named Run until 2026-11-02): a quick plan builder only. It sets exposure and frame count per filter for the current target, and autofocus, dither and flats follow the profile's defaults. The sequence editor stays in the full UI only.
- **Phones:** they run Simple Mode only, with no Sequence tab, once Simple Mode exists. Until then, PR #1474's phone layout stands.
- **Desktop default:** decided in ADR 0009.
- **Full UI layout untouched:** the full UI keeps its current layout (PR #1474's shell), and its look stays as on master. Approved exceptions that reach it: the Emergency Stop's 2 s hold and its cooler-off rung (ALP-4812), the Simple Mode icon in the top-right cluster, and the daemon-side plan changes both shells share (cooling while slewing, focus before centring, plain step names). Everything else lands in Simple Mode only.

## On the image during a run

| Where | What |
|---|---|
| Top | Status strip: device dots, Night mode, Settings (menu: System, rig and profile, All settings) |
| Top-left, under the strip | Message pills (results, errors, questions) |
| Top-right, under the strip | Camera status pill |
| Bottom-right, above the bottom row | Plan squares (the running plan in the corner, queued plans to its left) |
| Bottom-left, above the bottom row | Info and System fact tiles (when on) |
| Centre, under the strip | Attention banner, when something needs the person |
| Left rail | Sky, Info, Library |
| Right rail | Mode name, shutter, settings chip |
| Bottom right | Emergency Stop |
| Bottom row | Guiding graph (RMS, RA/Dec trace) and histogram |
| Centre, over the image | Plan's sheet; Night done when the plan finishes |

## Interaction

- **Size:** targets are at least 44 px, with 14 px text.
- **⌘K search:** finds every setting, including ones Simple Mode hides, and opens them in a sheet over Simple Mode without leaving it.
- **Night mode:** every panel must still read through the red filter.
- **Shortcuts:** ⌘, / Ctrl+, opens Settings; ⌘. / Ctrl+. asks, then stops a running plan on a second press within 3 s; Tab reaches the rail, the shutter and the plan squares, and Enter or Space presses them. The immersive view toggles only by clicking the image.

## Mode screens (decided 2026-11-01)

- **Mode order:** Autopilot · Preview · Focus · Polar align · Plan (Target folded in, Run renamed Plan, Autopilot added, 2026-11-02). Autopilot walks Focus → Polar align → Plan with a step strip, each step handing over to the next; Preview offers it while nothing is done tonight. On the right rail Polar align reads "P.A." and Autopilot "Auto" (the full names don't fit 60 px); the mode grid spells them out. Autopilot marks a skipped step amber and a done one green; its chip names the step whose settings it shows.
- **Mode panels:** every mode except Preview opens its panel to the left of the right rail, a floating card like the settings popover, level with the rail's top. Plan uses a centred sheet instead (decided 2026-11-01).
- **Preview:** shoot and loop, from the right rail: the shutter and the settings chip (Exposure, Gain, Filter, Loop/Single). The last frame's facts (HFR, stars, exposure · filter, gain · offset, clipped) come from Info. Single saves the frame (the daemon registers every Take One); Loop is live view and saves nothing. No Save frame button.
- **Polar align:** the polar-align camera's frames fill the image while it runs (nothing drawn on them). The shutter starts the routine and its red square stops it (Loop or Single). The mode panel holds Exposure and Loop/Single, and while it runs the error, the zone (under 10′ green, under 60′ amber, otherwise red), the bullseye, the altitude and azimuth turns, Take frame (Single) and a green Done. The rail reads "P.A."; the chip reads "45 s" over "Single" (two lines under the settings icon). A bolt turned the wrong way gets an amber line saying so.
- **Focus:** a zoomed star with its HFR ring top-left; the mode panel shows the HFR, the focus curve, the position and four nudge steps. The shutter follows the chip: Auto starts autofocus (its red square cancels), Manual takes frames (decided 2026-11-02). The Bahtinov helper is backlog (ALP-4874).
- **Plan:** the quick plan builder as a sheet centred over the image between the status pill and the bottom row, the rails still visible (on a wide window tonight's best sits in a column beside the plan): tonight's best, the target (a search, or Pick in Sky; Sky's card offers Image this), a row per filter (frames split the target's dark hours), then Add to queue and Create and start (Start now once a queue exists). Add to queue keeps the sheet up for the next pick. The queue is decided in ADR 0010; plan squares in the bottom-right corner line up right to left as far as there is room (then a "+N" square), each with the target's catalog tag, frames of its total, filter and sub, and time; any square opens the queue panel beside the rail, where a tap edits a target's rows, × deletes one that hasn't started, and Add target opens the sheet. Info and System are fact tiles in the bottom-left corner. The shutter starts the plan (play, white ring), pauses it after the frame in hand (pause bars, a ring filling with the exposure), resumes it (play, amber ring); a 2 s hold stops it (decided 2026-11-02: the shutter always starts).
- **Plan finished:** a Night done summary, in the same centred sheet: times, duration, how it ended and where it stopped, frames per filter for each target, then New plan (integration, best HFR, issues, flats and mount parked still to come).
- **No rig / no frame:** the stars-only wallpaper behind a Connect to your rig card (Sky shows tonight's planetarium) (discovered rigs, Add by address). Device dots go grey, the mode name dims, and Emergency Stop shows disabled.
- **Always visible in both modes:** Emergency Stop (except in the immersive view), the attention banner, device status dots, Night mode and the Simple ↔ full UI switch.

## Errors, horizon and the way out (decided 2026-11-01)

- **Errors inside a mode** (plate solve failed, autofocus failed, slew failed): while the person works a mode, the error shows inside that mode's panel, naming the cause and the fix, with a Retry (for example "Too few stars in a 2 s frame", then "Retry with 10 s"). During an unattended run the same errors use the centred attention banner.
- **Below the horizon (including the custom horizon):** Go to is disabled with the reason ("Below your horizon until 22:40"), and "Go to at 22:40" waits, then slews and centres on its own. In Plan such a target can be queued but not started: its square reads "rises 22:40" and the shutter waits for it (the app holds the start; the daemon waiting itself is ALP-4857).

## Still open

### Missing for a full night (2026-11-02, Paperclip)

What still keeps a whole night out of Simple Mode once the parity gaps and pills (ALP-4825) are done. 1, 2, 9 and 4 make a first full night possible; the rest make nights better.

1. **Connect to the rig** without the full UI: the Connect to your rig card, auto-connect from the profile, profile pick (ALP-4858).
2. **Wait for dark, stop at dawn, park:** the daemon first (ALP-4857), then Plan (ALP-4859).
3. **A real queue:** several plan squares, live append to the running plan, dawn ends the queue (ALP-4860). The app side is built (2026-11-02); dawn ending the queue waits on ALP-4857, and reordering beyond Start now isn't built.
4. **First-time rig setup:** site, camera details, guider pick and calibration, once per rig (ALP-4861).
5. **Calibration frames:** darks and bias beyond the auto-flats prompt (ALP-4862).
6. **Camera cooling and dew:** a camera tile and a dew pill (ALP-4863).
7. **Remember tonight's progress** across a relaunch, and a permanent pier in the rig's profile (ALP-4864).
8. **Phone layout** (ALP-4865).
9. **A night on the rig** to verify everything built so far (ALP-4866).
10. **Bahtinov mask helper** in Focus on Manual, as the full UI's Smart Focus has (ALP-4874).

- Exact copy for every state and error, written against the daemon's real failure reasons.
- Where the attention banner goes while the Info line is showing (both sit under the strip).
