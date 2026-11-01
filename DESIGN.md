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
  overlay-scrim: "rgba(26, 26, 26, 0.72)"
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
**The Start-Stops Rule.** Whatever starts something also stops it: the shutter that starts a shot, a loop or a routine turns into a red square that stops it, and an Autofocus button turns into Cancel. No second Stop button sits beside it. Done, wherever it shows, is green (Connected Green), as the one completing action.

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
- **Panels:** every panel uses `overlay-scrim` (Night Floor at 72 %), 10 px corners and no border, and sits 16 px from the frame edge and from its neighbours.
- **Fixed slots:** nothing moves when state changes. A panel with nothing to show is not drawn, and the panels around it keep their places.
- **Top:** a full-width status strip, 56 px tall: the device dots (connected gear only; a fault still shows), then Search settings, Night mode, Show all options and Settings, in the full UI's top-bar order. Nothing hides under a ⋯ menu.
- **Camera status:** a pill at the top-left, 16 px under the strip and at most 300 px wide, like a camera's REC light: "Taking a frame · 3 s left" with a thin bar, Looping, Stopping…, Frame ready, or why it failed; nothing while idle. It stays in the immersive view and moves up into the corner.
- **Right rail (72 px wide, only as tall as its controls, centred between the strip and Stop), from the top:**
  - the mode name. Polar align shows as "P.A." here, since the full name doesn't fit 60 px; the grid keeps "Polar align". A tap opens the five modes as a grid beside the rail: Preview, Focus, Polar align, Target, Run;
  - the shutter (60 px). In Single it takes a saved frame, as Take One does today; in Loop it runs live view, whose frames are not saved. While it works it shows a square that cancels;
  - the settings chip, which speaks for the current mode. In Preview it reads "5 s · 100" and its popover holds Exposure and Gain side by side (preset lists trimmed to the camera's limits), Filter, and Loop / Single; there is no Save frame button. In another mode it reads that mode's summary (Focus: "Auto" or "Manual", the method Smart Focus would use; Polar align: "1 s" over "Loop" on two lines, with no icon, since one line can't fit 60 px) and shows or hides that mode's panel.
- **Popovers** (the mode grid, settings, and a mode's panel) open to the left of the right rail, level with its top, 16 px from it. Opening one never moves the rail. A tap on the image closes an open popover.
- **Stop:** the compact Emergency Stop, a 60 px square reading STOP, on its own at the bottom right under the rail. It fires after a 2 s hold and reads HOLD while held; keyboard and screen reader users get the confirm dialog instead.
- **Left rail (72 px wide, centred between the strip and the bottom):** Sky (the planetarium at any time, over a frame), Info (toggles the last frame's facts), System (toggles the server's figures) and Library (the Image Library screen). Each is an icon over a caption.
- **Info:** one line under the strip, aligned right, starting right of the status pill's slot so the two never meet: HFR, Stars, Exposure · filter, Gain · offset, Clipped.
- **System:** one line at the right edge, right under the strip, or 8 px under Info while Info shows: Storage (free on the save drive, or Unplugged), CPU, Memory and Up, re-read every 5 s while it shows. CPU and Memory are Ara's own process until the server reports the whole Pi. CPU temperature, under-voltage or throttling and disk I/O are next; they need the server to report them.
- **Bottom row (56 px tall, 16 px from the left edge and 16 px from Stop):** the guiding graph ("Guiding", the RMS, the RA/Dec trace) and the frame's histogram (plotted over the range the frame uses), each only once it has data. A lone one fills the row; both split it equally, guiding on the left.
- **Centre:** the attention banner, centred 16 px under the status strip, only when something needs the person.
- **Immersive view:** a tap on the image, with no popover open, fades every panel out, Emergency Stop included (220 ms ease-out, instant with reduced motion), so the frame shows fullscreen; the camera status pill and any attention banner stay. A tap brings the panels back. There is no keyboard shortcut for now.
- **No frame yet:** tonight's planetarium (sky only, no in-page controls) fills the window behind the panels, with "Tap the shutter to take a frame" centred.
- **Leaving:** the frame icon in the full UI's top bar enters Simple Mode; Settings, ⌘K, a device dot and Show all options return to the full UI for now.

**Modes.** The mode grid reads Preview · Focus · Polar align · Target · Run. Each mode other than Preview opens its own panel to the left of the right rail when it is picked (a floating card like the settings popover, pinned beside the rail and level with its top), and the settings chip shows or hides it; where needed the mode also draws on the image. The rails, the strip and Stop never move. All modes but Run are built; Run still opens its full-UI tab.
- **Preview:** the right rail itself: shutter, settings chip, Loop or Single. The last frame's facts come from Info.
- **Polar align:** the rail reads "P.A." and the chip shows the routine's setup ("45 s" over "Single"). The shutter starts the routine, and while it runs its red square stops it, in Loop and Single alike. While the routine runs, the image area shows the polar-align camera's latest frame. Nothing is drawn on the frame: the server reports the altitude and azimuth errors, not where the pole sits in the picture. The mode panel holds Exposure and Loop / Single (the profile's polar-align settings, applied to the next frame). While the routine runs it also shows the phase, the total error with its zone dot (green under 10′, amber under 60′, red beyond, matching the server's thresholds), the full UI's bullseye, the altitude and azimuth turns as arrow + word ("↓ Lower 3.2′", "← Move west 14.1′"), failed solves, then Take frame (Single only; Loop shoots on its own) and a green Done (once inside the profile's tolerance). The panel has no Start or Stop: the shutter does both. The bolt-direction hint stays in the full UI for now.
- **Focus:** a zoomed star with its HFR ring sits top-left under the status pill. The mode panel shows the HFR, the focus curve as it builds, the focuser position, an Autofocus button and four nudge buttons (in large, in small, out small, out large).
- **Target:** the image area becomes the planetarium, even over a frame, and a tap on the sky picks an object without closing the panel. The mode panel holds the object search (the planetarium's own: catalogs, the server's resolver, the engine) and then the selected object: name, type, and "62° up · max 74°", or why it can't be reached ("Below your horizon · rises 22:40", "Never rises here") in amber. The shutter is Go to and centre: it unparks if needed, slews, then runs the server's plate-solve centring on the object's J2000 position, and the panel follows it ("Centering · solve 1 of 3", then a green "Centered on M31", or why it failed). While the mount slews or the centring runs, its red square cancels the centring and aborts the slew. A running sequence's refusal shows "Slew anyway". The chip reads the selection's name, or "None". "Go to at <time>", which waits and then slews, is not built yet.
- **Run:** the quick plan builder is a sheet centred over the image, with the rails still visible. It lists exposure and frame count per filter for the current target, then Start imaging; autofocus, dither and flats follow the profile. Start imaging closes the sheet. When the plan finishes, a Night done summary takes the same centred sheet: frames per filter, integration, best HFR, issues during the night, flats, mount parked, then Image Library and New plan.

**Errors.** While the person works a mode, errors (solve, autofocus, slew) show inside that mode's panel with the cause, the fix and Retry. During an unattended run they use the centred attention banner.

**No rig connected.** The planetarium of tonight's sky fills the image area with a centred card: rigs found on the network with Connect, and Add a rig by address. Device dots go grey, the mode name dims, and Emergency Stop shows disabled.

**Always on, in both modes:** Emergency Stop (except in the immersive view), the attention banner, the device status dots, Night mode and the Simple ↔ full UI switch.

Targets are at least 44 px tall, with 14 px body text. Colours and status dots behave exactly as in the full UI. Phone Companion Mode uses the same image-first idea in a portrait arrangement. The rendered reference is `design/preview/ara-design-preview.html`.
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
