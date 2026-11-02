---
name: OpenAstro Ara
description: Dark observatory console for running a headless astrophotography rig through the night.
colors:
  bg-primary: "#1A1A1A"
  bg-panel: "#262626"
  bg-panel-alt: "#2E2E2E"
  bg-input: "#333333"
  border: "#404040"
  text-primary: "#E0E0E0"
  text-secondary: "#A0A0A0"
  text-disabled: "#606060"
  accent-connected: "#4CAF50"
  accent-busy: "#FFB300"
  accent-warning: "#FFB300"
  accent-error: "#E53935"
  accent-info: "#42A5F5"
  accent-disconnected: "#606060"
  selection-bg: "#1976D2"
  selection-fg: "#FFFFFF"
  overlay-scrim: "rgba(26, 26, 26, 0.90)"
  error-text: "#FF8A80"
  selection-tint: "rgba(25, 118, 210, 0.25)"
  button-primary: "#1565C0"
  button-secondary: "#424242"
typography:
  title:
    fontFamily: "system-ui, -apple-system, Segoe UI, Roboto, sans-serif"
    fontSize: "15px"
    fontWeight: 600
  section:
    fontFamily: "system-ui, -apple-system, Segoe UI, Roboto, sans-serif"
    fontSize: "11px"
    fontWeight: 600
    letterSpacing: "0.6px"
  body:
    fontFamily: "system-ui, -apple-system, Segoe UI, Roboto, sans-serif"
    fontSize: "13px"
    fontWeight: 400
  caption:
    fontFamily: "system-ui, -apple-system, Segoe UI, Roboto, sans-serif"
    fontSize: "11.5px"
    fontWeight: 400
  numeric:
    fontFamily: "system-ui, -apple-system, Segoe UI, Roboto, sans-serif"
    fontSize: "13px"
    fontWeight: 400
    fontFeature: "tnum"
  label:
    fontFamily: "system-ui, -apple-system, Segoe UI, Roboto, sans-serif"
    fontSize: "11px"
    fontWeight: 500
    letterSpacing: "0.5px"
  body-simple:
    fontFamily: "system-ui, -apple-system, Segoe UI, Roboto, sans-serif"
    fontSize: "14px"
    fontWeight: 400
  mono:
    fontFamily: "ui-monospace, SF Mono, Menlo, monospace"
    fontSize: "11px"
rounded:
  xs: "4px"
  sm: "6px"
  md: "8px"
  pill: "12px"
  simple: "10px"
spacing:
  s4: "4px"
  s8: "8px"
  s12: "12px"
  s16: "16px"
  s24: "24px"
components:
  button-primary:
    backgroundColor: "{colors.button-primary}"
    textColor: "{colors.selection-fg}"
  button-secondary:
    backgroundColor: "{colors.button-secondary}"
    textColor: "{colors.text-primary}"
  button-emergency-stop:
    backgroundColor: "{colors.accent-error}"
    textColor: "{colors.selection-fg}"
    padding: "0 8px"
  button-primary-simple:
    backgroundColor: "{colors.button-primary}"
    textColor: "{colors.selection-fg}"
    typography: "{typography.body-simple}"
    rounded: "{rounded.simple}"
    height: "44px"
  card:
    backgroundColor: "{colors.bg-panel}"
    rounded: "{rounded.sm}"
  input:
    backgroundColor: "{colors.bg-input}"
    textColor: "{colors.text-primary}"
    rounded: "{rounded.xs}"
  equipment-chip:
    textColor: "{colors.text-secondary}"
    typography: "{typography.label}"
    rounded: "{rounded.sm}"
    padding: "6px 8px"
    width: "76px"
  status-indicator:
    textColor: "{colors.text-primary}"
    rounded: "{rounded.pill}"
    padding: "6px 10px"
  nav-rail:
    backgroundColor: "{colors.bg-panel}"
    textColor: "{colors.text-secondary}"
  nav-rail-selected:
    backgroundColor: "{colors.selection-bg}"
    textColor: "{colors.selection-fg}"
  top-bar:
    backgroundColor: "{colors.bg-panel}"
    height: "64px"
  bottom-status-bar:
    backgroundColor: "{colors.bg-panel}"
    height: "40px"
---

# Design System: OpenAstro Ara

## Overview

**Creative North Star: "The Observatory Console"**

Ara looks like the dark instrument panel at the telescope. Surfaces are neutral greys stepped a few points apart. Information is dense, in the NINA tradition: small type, tight spacing, panels separated by hairline borders. Colour belongs to the equipment and the session. A green dot means connected, amber means busy or needs a look, red means fault or stop. Nothing else is coloured. At rest the screen is quiet, so a change of state is the loudest thing on it.

One idea is borrowed from a cockpit. When something needs the person (often checking in at 3 a.m.), it must be impossible to miss: icon, colour and words together, never colour alone. Emergency Stop is the heaviest control on every screen. Controls behave like labelled hardware: switches name their states, readouts carry a status dot, and numbers use tabular figures so they don't jitter while they update.

The app runs on Material 3 (dark) with the Ara tokens below, on desktop and mobile, so the UI must survive Night mode. Night mode folds every pixel's luminance into the red channel, and the interface then reads by brightness alone.

**Key Characteristics:**
- Neutral grey surfaces with tonal steps and 1 px borders; no shadows.
- Colour only through semantic status roles, never as decoration.
- Dense, small type ramp (11–15 px) on one spacing scale (4 / 8 / 12 / 16 / 24).
- Attention states are unmissable: icon + colour + text.
- Emergency Stop: filled red, always visible, on every tab.
- Instrument-like controls: named switch states, status dots, tabular numbers.
- Two densities: the dense full UI, and a roomy, image-first Simple Mode (the frame fills the screen; controls float on translucent overlays; 44 px targets, 10 px corners, 14 px type).

## Colors

A near-black neutral ramp carries everything; saturated status accents carry meaning.

### Primary
- **Console Blue** (`button-primary`): the primary filled action ("Start sequence", "Save"). Used sparingly; most actions are secondary or text buttons.
- **Selection Blue** (`selection-bg`): the selected navigation-rail destination, selected segments and selected rows, with white (`selection-fg`) on top.

### Secondary
- **Sky Info Blue** (`accent-info`): informational state, links and the active Night-mode toggle. Also the theme's `secondary`.

### Status
- **Connected Green** (`accent-connected`): device connected, step done, healthy readout. A device that is connected and working (exposing, slewing, moving) blinks its dot in this green (the `active` status).
- **Dew Amber** (`accent-busy`, `accent-warning`): busy, working, needs a look. The two tokens share a value but stay separate roles. Busy is a state dot; warning is an inline icon + colour + text surface.
- **Fault Red** (`accent-error`): faults, failures, destructive actions and Emergency Stop. The theme's `error`.
- **Unplugged Grey** (`accent-disconnected`): disconnected or off. The same value as `text-disabled`, deliberately: "not there" reads as absent, not as an alarm.

### Neutral
- **Night Floor** (`bg-primary`): the app background and scaffold.
- **Panel Grey** (`bg-panel`): side panels, top bar, bottom status bar, cards, navigation rail.
- **Panel Alt** (`bg-panel-alt`): alternate rows, hover and the highest surface container.
- **Input Well** (`bg-input`): text fields, dropdowns, progress tracks.
- **Hairline** (`border`): panel borders, dividers and every control outline (pinned to `outlineVariant`, so chips and outlined controls match).
- **Starlight Text** (`text-primary`): primary text and icons.
- **Dim Text** (`text-secondary`): labels, captions, secondary lines. The most-used colour in the app.
- **Faint Text** (`text-disabled`): disabled text and empty values.

### Named Rules
**The State-Only Colour Rule.** An accent appears only through a semantic role: a state dot, a state chip, a category hue or a destructive action. If it doesn't mean something, it is grey.

**The Brightness-First Rule.** Every state must still read in Night mode, where hue is gone and only luminance remains. A connected dot must stay brighter than a border. Never tell two states apart by hue alone.

## Typography

**Body Font:** the platform's system UI font (Material default: SF on Apple, Segoe UI on Windows, Roboto elsewhere)
**Mono Font:** the platform monospace, for file paths, FITS headers and histogram labels only

**Character:** small, neutral and legible in the dark. Hierarchy comes from weight and dim-versus-bright, not from size jumps.

### Hierarchy
- **Title** (600, 15px): pane and dialog titles.
- **Section** (600, 11px, 0.6px tracking, uppercase): group headers such as palette categories and inspector sections.
- **Body** (400, 13px): default row and body text.
- **Caption** (400, 11.5px, Dim Text): the secondary line under a body row.
- **Numeric** (400, 13px, tabular figures): timers, counts, temperatures, HFR, RMS. Anything that updates live.
- **Label** (500, 11px, 0.5px tracking, Material label-small): device names under the equipment-button icons, in plain case ("Filter Wheel"), scaled down to fit rather than widening the button.

### Named Rules
**The Steady Digits Rule.** Every number that updates live uses tabular figures. A countdown never shifts its neighbours.

**The One Ramp Rule.** New and redesigned surfaces use the `AraText` ramp above, not per-widget 11/12/13 px styles. The ramp is being adopted incrementally, and older widgets still carry ad-hoc sizes (12 px is common).

## Layout

The shell (reference: PR #1474, open as of 2026-11-01) follows NINA's arrangement:
- **Top bar (64 px):** device buttons first, then search, Night mode and a Settings gear at the right edge. The buttons run in order of use: Camera, Mount, Guider, Focuser, Filter Wheel, Rotator, Flat Panel, Switch, Weather, Safety, Dome.
- **Navigation rail (left):** the night's workflow only, Planning → Checklist → Sequence → Live. Settings is not on the rail; it opens from the gear, which lights in Sky Info Blue while Settings shows.
- **Centre:** the active tab.
- **Bottom status bar (40 px):** the connection indicator, then Emergency Stop, the backup-stream pulse, and the launchers right-anchored.

Below 600 px of window width (a phone in portrait) the same shell folds:
- the tabs move to a bottom navigation bar, under the status bar;
- the device buttons stand in a column down the left edge;
- the top bar shrinks to 48 px and keeps search, Night mode and the gear.

Wherever the eleven named device buttons don't fit (shortest side under 600 px, an iPad in portrait, a narrowed window) they show icons only, with the name as a tooltip, so none is pushed off screen. Safe-area margins (notch, status bar, home indicator) take the Panel Grey of the bars instead of black.

Spacing uses one scale: 4 / 8 / 12 / 16 / 24 px.

Responsive behaviour is keyed to the window, not the device:
- Under 820 px of window height, a pane goes compact: tighter padding, secondary lines dropped.
- Instrument panes sit side by side (picture | readout) from 760 px of pane width, and stack below that.
- In a short window they stay side by side down to 600 px, because stacking would scroll.

Phones run Companion Mode (see PRODUCT.md). PR #1474's narrow layout is the shell phones get today.

### Named Rules
**The Two Densities Rule.** The full UI is dense: 11–15 px type and 4–8 px corners. Simple Mode is roomy and image-first: the frame fills the window, controls float on translucent overlays, every interactive target is at least 44 px tall, corners are 10 px, and body text is 14 px. Both densities share the same colours, the same named rules and the same Emergency Stop. Phones run Companion Mode, which builds on Simple Mode, so phones always get the roomy density. Simple Mode exists only as a prototype behind an experimental toggle (`feat/simple-mode`); this is its target.

## Elevation & Depth

Flat. Depth comes from tonal layering: Night Floor, then Panel Grey, then Panel Alt. Hairline borders separate surfaces. App bars render at elevation 0, cards are bordered rather than lifted, and no surface casts a shadow. Material dialogs and snackbars keep their framework defaults.

### Named Rules
**The Start-Stops Rule.** Whatever starts something also stops it: the shutter that starts a shot, a loop or a routine turns into a red square that stops it, and an Autofocus button turns into Cancel. No second Stop button sits beside it. Simple Mode's Plan is the one variant: a plan runs a whole night, so its shutter starts the plan (a play triangle in a green ring), then pauses it (pause bars), resumes it (a play triangle in an amber ring), and a 2 s hold stops it. Done, wherever it shows, is green (Connected Green), as the one completing action.

**The No-Glow Rule.** Surfaces don't cast shadows or glow. A panel is separated by a 1 px Hairline and a tonal step, nothing more. The one exception is Simple Mode's overlays: they float over the image on a translucent scrim, still with no shadow.

## Shapes

Gently rounded and small, matching the dense layout:
- 4 px on fields and small controls;
- 6 px on cards, equipment chips and ink highlights (the most common radius);
- 8 px on larger containers;
- a 12 px pill on status indicators and state chips.

Status dots are 10 px circles. On an equipment chip the dot carries a 2 px ring in the panel colour, so it reads as a badge sitting on the icon.

## Components

Controls behave like labelled hardware: each one names its state and shows it with a dot or a fill.

### Buttons
- **Shape:** Material 3 filled and text buttons on the theme radius.
- **Primary:** Console Blue fill, white text. Reserved for the main action of a pane.
- **Secondary:** text or outlined buttons, with Hairline outlines. The `button-secondary` grey exists but is rarely used.
- **Destructive:** Fault Red fill (the theme's `error`), with the action named in the label.

### Emergency Stop (signature)
A filled Fault Red button with a report icon and the label "Emergency Stop", in the bottom status bar on every tab, right after the connection indicator. While a stop is in flight it shows a spinner and "Stopping…". It confirms by holding: pressing reads "Hold to confirm" while a lighter fill sweeps across, and 2 s of holding sends the stop; letting go early sends nothing. Keyboard and screen-reader activation, which can't hold, get a dialog ("Stop everything now?") whose confirm button is filled red and labelled EMERGENCY STOP (decided 2026-11-01). It carries a ring: a 2 px dark gap, then 1 px of Fault Red (ALP-4812). It is the heaviest control on any screen: no other control at rest is filled red.

### Equipment Chip (signature)
A fixed 76 px-wide tile in the top bar (48 px when compact), so the icons sit evenly spaced. It has a 28 px device icon in Starlight Text, a 10 px status dot at its top-right corner (2 px Panel Grey ring), and the device name underneath in label-small Dim Text. Mount and Filter Wheel use hand-drawn glyphs (a telescope on a tripod, a five-slot filter wheel) because Material Icons has neither. The dot blinks green (700 ms) while the device is connected and working; amber stays for states that need a look. Tapping opens the device's Settings page, and long-press is supported. When compact the name is dropped and lives on as the tooltip.

### Status Indicator / State Chip
A pill (12 px radius) with a 10 px status dot followed by a label in body-small. The label truncates with an ellipsis in tight rows. The design rule: every state (idle, offline, draft, running, paused, attention, failed, done) has a designed look; nothing falls back to plain grey text.

### Switches and Segmented Controls
Material segmented buttons and choice chips. Selected segments use Selection Blue with white text, and outlines use Hairline. Segments name their states ("Cooler On" / "Off"), never a bare toggle.

### Simple Mode: Image First (two rails)
Simple Mode (see PRODUCT.md) puts the latest frame first. It is simpler than ASIAir, not a copy of it. The image fills the window edge to edge, and the controls float over it on translucent panels. The layout below is the prototype on `feat/simple-mode`, approved on 2026-11-01; it sits behind Settings → System → Experimental → Simple Mode (off by default, per client), and the system status bar is hidden while it shows.
- **Panels:** every panel uses `overlay-scrim` (Night Floor at 90 %, so panel text reads at 4.5:1 or better over a dark sky, a grey frame or a white flat; raised from 72 % on 2026-11-02), 10 px corners and no border, and sits 16 px from the frame edge and from its neighbours. Error words on a panel use `error-text` (#FF8A80), Fault Red lightened for the scrim; Fault Red itself stays for fills and the E-stop. A selected or current item on a panel uses `selection-tint`.
- **Fixed slots:** nothing moves when state changes. A panel with nothing to show is not drawn, and the panels around it keep their places.
- **Top:** a full-width status strip, 56 px tall: the device dots (connected gear only; a fault still shows), then Search settings, Night mode, Show all options and Settings, in the full UI's top-bar order. Nothing hides under a ⋯ menu.
- **Camera status:** a pill at the top-left, 16 px under the strip and at most 300 px wide, like a camera's REC light: "Taking a frame · 3 s left" with a thin bar, Looping, Stopping…, Frame ready, or why it failed; nothing while idle. It stays in the immersive view and moves up into the corner.
- **Right rail (72 px wide, only as tall as its controls, centred between the strip and Stop), from the top:**
  - the mode name. Polar align shows as "P.A." and Autopilot as "Auto" here, since the full names don't fit 60 px; the grid and screen readers keep the full names. A tap opens the five modes as a grid beside the rail: Autopilot, Preview, Focus, Polar align, Plan;
  - the shutter (60 px). In Preview, Single takes a saved frame, as Take One does today, and Loop runs live view, whose frames are not saved. In the other modes it starts that mode's action. While it works it shows a square that cancels;
  - the settings chip, which speaks for the current mode. In Preview it reads "5 s · 100" and its popover holds Exposure and Gain side by side (preset lists trimmed to the camera's limits), Filter, and Loop / Single; there is no Save frame button. In another mode it reads that mode's summary (Focus: "Auto" or "Manual", the method Smart Focus would use; Polar align: "1 s" over "Loop"; Plan: the total imaging time, "2 h" over "45 min") and shows or hides that mode's panel. The chip is 72 px tall in every mode, the settings icon over up to two lines, so the rail is the same height whatever the mode (decided 2026-11-02).
- **Popovers** (the mode grid, settings, and a mode's panel) open to the left of the right rail, level with its top, 16 px from it. Opening one never moves the rail. A tap on the image closes an open popover.
- **Stop:** the compact Emergency Stop, a 60 px square reading STOP, on its own at the bottom right under the rail. It fires after a 2 s hold and reads HOLD while held; keyboard and screen reader users get the confirm dialog instead.
- **Left rail (72 px wide, centred between the strip and the bottom):** Sky (the planetarium at any time, over a frame; an object tapped there shows a card under it with its name, type and altitude and Image this, which opens Plan's sheet with it), Info (toggles the last frame's facts), System (toggles the server's figures) and Library (the Image Library screen). Each is an icon over a caption.
- **Info:** a row of fact tiles (72 × 56, label over value) hugging the bottom-left corner, just above the bottom row, HFR nearest the corner, then Stars, Exposure · filter, Gain · offset, Clipped. The top-right corner belongs to the plan squares.
- **System:** a row of the same tiles, in the bottom-left corner alone or 8 px above Info's row while Info shows: Storage (free on the save drive, or Unplugged), CPU, Memory and Up, re-read every 5 s while it shows. CPU and Memory are Ara's own process until the server reports the whole Pi. CPU temperature, under-voltage or throttling and disk I/O are next; they need the server to report them.
- **Bottom row (56 px tall, 16 px from the left edge and 16 px from Stop):** the guiding graph ("Guiding", the RMS, the RA/Dec trace) and the frame's histogram (plotted over the range the frame uses), each only once it has data. A lone one fills the row; both split it equally, guiding on the left.
- **Centre:** the attention banner, centred 16 px under the status strip, only when something needs the person.
- **Immersive view:** a tap on the image, with no popover open, fades every panel out, Emergency Stop included (220 ms ease-out, instant with reduced motion), so the frame shows fullscreen; the camera status pill and any attention banner stay. A tap brings the panels back. There is no keyboard shortcut for now.
- **No frame yet:** tonight's planetarium (sky only, no in-page controls) fills the window behind the panels, with "Tap the shutter to take a frame" centred.
- **Leaving:** the frame icon in the full UI's top bar enters Simple Mode; Settings, ⌘K, a device dot and Show all options return to the full UI for now.

**Modes.** The mode grid reads Autopilot · Preview · Focus · Polar align · Plan (Plan was named Run until 2026-11-02). Targets are picked in Plan's sheet or in Sky; there is no Target mode (decided 2026-11-02), so Go to, Solve and Sync without a run stay in the full UI. Each mode other than Preview opens its own panel to the left of the right rail when it is picked (a floating card like the settings popover, pinned beside the rail and level with its top), and the settings chip shows or hides it; where needed the mode also draws on the image. The rails, the strip and Stop never move. All five modes are built.
- **Preview:** the right rail itself: shutter, settings chip, Loop or Single. The last frame's facts come from Info.
- **Polar align:** the rail reads "P.A." and the chip shows the routine's setup ("45 s" over "Single"). The shutter starts the routine, and while it runs its red square stops it, in Loop and Single alike. While the routine runs, the image area shows the polar-align camera's latest frame. Nothing is drawn on the frame: the server reports the altitude and azimuth errors, not where the pole sits in the picture. The mode panel holds Exposure and Loop / Single (the profile's polar-align settings, applied to the next frame). While the routine runs it also shows the phase, the total error with its zone dot (green under 10′, amber under 60′, red beyond, matching the server's thresholds), the full UI's bullseye, the altitude and azimuth turns as arrow + word ("↓ Lower 3.2′", "← Move west 14.1′"), failed solves, then Take frame (Single only; Loop shoots on its own) and a green Done (once inside the profile's tolerance). The panel has no Start or Stop: the shutter does both. When one axis got worse after a turn while the other held, an amber line says "Altitude got worse. Turn that bolt back past where you started." (the full UI's bolt-direction hint, cut to two lines).
- **Focus:** a zoomed star with its HFR ring sits top-left under the status pill. The mode panel shows the HFR, the focus curve as it builds, the focuser position and four nudge buttons (in large, in small, out small, out large). The shutter follows the chip: on Auto it starts autofocus and its red square cancels it; on Manual it takes frames to focus by, as in Preview (decided 2026-11-02). The full UI's Bahtinov helper isn't in Simple Mode yet (ALP-4874).
- **Autopilot:** the night's steps in order, Focus → Polar align → Plan (focus first: polar alignment plate-solves and fails on a soft focus), each on its usual panel, with a step strip centred under the status strip (the steps behind the current one carry a green tick when done tonight or an amber skip mark when skipped; the current one is lit; Back and Skip). The shutter starts the step in hand. A finished autofocus moves on to Polar align, which starts its routine on entry (an Autopilot-only exception to the shutter starting things; its red square still stops it); Polar align's green Done reads "Done · Next: Plan"; starting the plan ends Autopilot and leaves you in Plan. The rail reads "Auto"; it starts from the mode grid or from Preview's offer. The chip names the step whose settings it shows ("Focus" over "Auto", "P.A." over "45 s", "Plan" over "2 h"). Steps already done tonight are skipped (focus: an autofocus finished this session; polar align: inside tolerance this session), and a permanent pier (Settings → Experimental, saved per client for now) skips Polar align. Preview offers it, centred under the strip, while nothing is done yet tonight: "Nothing done yet tonight", a green **Start Autopilot**, and × to dismiss. Outside Autopilot every mode stands alone: nothing opens another. The night is five taps (the shutter for autofocus, Done, a suggested target, Create and start, plus turning the bolts), four on a permanent pier.
- **Plan:** the quick plan builder is a sheet centred over the image between the status pill and the bottom row (it scrolls inside when taller), with the rails still visible. On a wide window its lists (QUEUE, tonight's best) sit in a 240 px column to the left of the plan; on a narrow one they stack on top. On top, tonight's best: up to four tappable targets from the Tonight's Sky ranking (name, then type and altitude or when it rises; hidden with no site), then the target: a search (the planetarium's own, so the sky shows behind the sheet while it searches) with the picked object's name and "62° up · max 74°" or why it can't be reached, and Pick in Sky, which closes the sheet for the sky (its card's Image this brings the sheet back). Then a row per filter with exposure and frame count, the total frames and time; the frames split the picked target's dark hours left tonight across the rows (60 frames split without a window), a new target refits them and a new sub length refits its row; autofocus, dither and flats follow the profile. Tapping a value opens the same preset list as the Preview settings. Rows start at the profile's default exposure; one line under them names the suggested sub lengths that differ ("Suggested: L 2 min · GB 3 min", the plan chooser's sky-based numbers) with Use all, and each exposure list marks its suggestion. Without tonight's polar alignment (and no permanent pier) an amber line reads "Not polar aligned tonight"; it warns and never blocks. The sheet ends with two buttons. **Add to queue** (outlined) adds the target to the end of the queue; **Create and start** (green) creates the plan and starts it, and once a queue exists it reads **Start now**: the target goes first and imaging starts (a second exception to the shutter starting things). A target below the horizon can be queued but not started; one that never rises here can't be queued. While imaging, the sheet only adds. Add to queue keeps the sheet up for the next pick: a green line says "M42 added · 3 in the queue" and the pick clears; Create and start / Start now close it (decided 2026-11-02). With a queue, the sheet starts with QUEUE: each target's tag, frames of its total and time left, with × on any that hasn't started. The rail ▶ also creates and starts when a target is picked and nothing is queued, and starts a queued plan.

The queue is one sequence on the rig: each target is a block in it, and adding while it images joins the running sequence (decided 2026-11-02). Plan squares (72 × 100) hug the top-right corner under the strip and line up right to left in queue order, the first in the corner. They take only the room left of the corner up to the status pill's slot (and, in Autopilot, beside the step strip, or under it when the window leaves no room); past what fits, the target in hand and those after it show, then one "+N" square with the rest's total time (finished targets drop out first). Plan's sheet starts under the squares once there is a queue. Each shows the target's catalog tag (a curated region's own, "IC 434"; else its best designation, M before NGC, IC, Sh2, B and LBN; else its name, shrunk to fit), frames done of its total ("0 / 60", "23 / 60"), the filter and sub length in hand ("R · 3 min"), and its time: the total before it starts, the time left while imaging, "rises 22:40" while a queued target is still below the horizon (the shutter waits for the first target's rise), "Done" once finished. The square in hand is green when ready, blue with a progress line along its foot while imaging, amber "Paused"; the others are green. Tapping any square opens the sheet, or the progress panel beside the rail while imaging; the queue is managed in the sheet. The progress panel shows the target in hand (frames per filter, when the queue ends) and Add to queue, which opens the sheet. Long filter names fit whole (the sheet's filter column widens to the longest name).

The shutter starts the plan; while imaging it shows pause bars with a blue ring filling as each frame exposes, and a tap pauses after the frame in hand ("Pausing…" on the square until it lands); paused, a tap resumes and re-centres first, and the panel offers Resume without re-centering and Re-center and refocus; after a panic stop paused the run, the panel reads "Mount stopped" in amber; a 2 s hold, imaging or paused, stops the run (a red ring fills round the shutter, "Hold to stop" beside it; letting go early cancels). Screen readers get "Stop the run" as an action, keyboards ⌘. / Ctrl+. When the daemon refuses for want of a storage disk, its words show with Open Storage, which opens the full UI's Storage page. The camera pill and Info read as in Preview. When the queue finishes, Night done takes the same centred sheet: the start and end times, how long it ran and how it ended, the step it stopped at in red, frames per filter for each target (the library's count for a single target, the queue-order estimate for several), then Image Library and New plan (integration, best HFR, issues, flats and the mount's park come later).

**Errors.** While the person works a mode, errors (solve, autofocus, slew) show inside that mode's panel with the cause, the fix and Retry. During an unattended run they use the centred attention banner.

**No rig connected.** The planetarium of tonight's sky fills the image area with a centred card: rigs found on the network with Connect, and Add a rig by address. Device dots go grey, the mode name dims, and Emergency Stop shows disabled.

**Always on, in both modes:** Emergency Stop (except in the immersive view), the attention banner, the device status dots, Night mode and the Simple ↔ full UI switch.

Targets are at least 44 px tall (the settings segments too), with 14 px body text. The rails follow the user's text size up to 1.3x, since they are a fixed-width cluster; panels, the sheet and messages follow it all the way, and the Autopilot offer's words wrap on a narrow window. Colours and status dots behave exactly as in the full UI. Phone Companion Mode uses the same image-first idea in a portrait arrangement. The rendered reference is `design/preview/ara-design-preview.html`.
### Cards / Containers
- **Corner Style:** 6 px.
- **Background:** Panel Grey.
- **Shadow Strategy:** none (see Elevation & Depth).
- **Border:** 1 px Hairline.
- **Internal Padding:** 8–16 px from the spacing scale.

### Inputs / Fields
Filled with Input Well, using the outline border. Focus uses the Material 3 primary focus outline in Console Blue.

### Navigation
A navigation rail on Panel Grey with labels always shown: Planning, Checklist, Sequence, Live. The selected destination gets a Selection Blue indicator with white icons, and unselected items use Dim Text. Settings opens from the top-bar gear, and its sidebar has no Equipment group, because each device button opens that device's page. Below 600 px wide the same four tabs move to a Material bottom navigation bar, and nothing is highlighted while Settings shows. ⌘K / Ctrl-K opens a command palette that searches every setting.

### Attention Banner
An inline warning built from three parts: an amber or red icon, a coloured outline or tint, and a sentence that says what happened and what Ara is doing about it ("Guiding lost. Retrying (2 of 5)."). Full-screen alarm modals are reserved for safety events.

## Do's and Don'ts

### Do:
- **Do** pair every warning or fault with icon + colour + text. Colour alone never carries a message.
- **Do** keep Emergency Stop filled red, visible on every tab and heavier than any other control.
- **Do** use `AraColors`, `AraSpace` and `AraText` tokens. Accents come only through semantic roles.
- **Do** use tabular figures for any live number.
- **Do** check every new state in Night mode: it must still be distinguishable by brightness.
- **Do** write labels in product voice: verbs, sentence case, no NINA or daemon enum names.

### Don't:
- **Don't** change the full UI's layout while Simple Mode is being built. Simple Mode is additive: new work goes into Simple Mode, and the full UI (PR #1474's shell) stays as it is for now. Two exceptions are approved: the heavier Emergency Stop (ALP-4812), which applies to the full UI too, and a Simple Mode icon in the full UI's top-right cluster, between Night mode and the gear (decided 2026-11-01).
- **Don't** use colour for decoration, branding flourishes or emphasis that isn't a state.
- **Don't** add shadows or glows to separate surfaces; use a tonal step and a Hairline.
- **Don't** let any state fall back to plain grey text without a designed look.
- **Don't** fill any control other than Emergency Stop and confirmed destructive actions with Fault Red.
- **Don't** copy NINA's icons, splash, imagery or wordmark (see PRODUCT.md).
- **Don't** hard-code colours outside `lib/theme/`. A few widgets still do, and they should migrate to tokens.
