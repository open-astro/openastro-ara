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
  link-text: "#64B5F6"
  selection-tint: "rgba(25, 118, 210, 0.25)"
  tile-fill: "rgba(56, 56, 56, 0.55)"
  alarm-scrim: "rgba(60, 8, 8, 0.88)"
  emergency-stop: "#D32F2F"
  on-emergency-stop: "#FFFFFF"
  tier-bronze: "#CD853F"
  tier-silver: "#C0C0C0"
  tier-gold: "#FFD54F"
  on-tier: "#000000"
  album-art-backdrop: "#000000"
  album-placeholder-missing: "#808080"
  button-primary: "#1565C0"
  button-secondary: "#424242"
  brand-navy-top: "#272C4D"
  brand-navy-mid: "#171B3B"
  brand-navy-bottom: "#070914"
  brand-line-start: "#4FC3F7"
  brand-line-end: "#7C8CF8"
  brand-glow: "#8FA8FF"
  brand-star-hole: "#0B0F24"
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
    fontFeature: "tnum"
  mono:
    fontFamily: "ui-monospace, SF Mono, Menlo, monospace"
    fontSize: "11px"
  readout-simple:
    fontFamily: "system-ui, -apple-system, Segoe UI, Roboto, sans-serif"
    fontSize: "30px"
    fontWeight: 300
    fontFeature: "tnum"
  heading-simple:
    fontFamily: "system-ui, -apple-system, Segoe UI, Roboto, sans-serif"
    fontSize: "16px"
    fontWeight: 600
  hint-simple:
    fontFamily: "system-ui, -apple-system, Segoe UI, Roboto, sans-serif"
    fontSize: "20px"
    fontWeight: 400
    letterSpacing: "2px"
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
    backgroundColor: "{colors.emergency-stop}"
    textColor: "{colors.on-emergency-stop}"
    rounded: "16px"
    padding: "7px 18px"
  button-emergency-stop-simple:
    backgroundColor: "{colors.emergency-stop}"
    textColor: "{colors.on-emergency-stop}"
    rounded: "{rounded.simple}"
    padding: "0"
    size: "60px"
  panel-simple:
    backgroundColor: "{colors.overlay-scrim}"
    rounded: "{rounded.simple}"
  status-pill-simple:
    backgroundColor: "{colors.overlay-scrim}"
    textColor: "{colors.text-primary}"
    typography: "{typography.body-simple}"
    rounded: "{rounded.simple}"
    padding: "0 16px"
    height: "48px"
    width: "300px"
  message-pill-simple:
    backgroundColor: "{colors.overlay-scrim}"
    textColor: "{colors.text-primary}"
    typography: "{typography.body-simple}"
    rounded: "{rounded.simple}"
    height: "48px"
    width: "420px"
  button-primary-simple:
    backgroundColor: "{colors.button-primary}"
    textColor: "{colors.selection-fg}"
    typography: "{typography.body-simple}"
    rounded: "{rounded.simple}"
    height: "44px"
  plan-square-simple:
    backgroundColor: "{colors.overlay-scrim}"
    textColor: "{colors.text-primary}"
    typography: "{typography.body-simple}"
    rounded: "{rounded.simple}"
    width: "72px"
    height: "100px"
  ara-sheet-simple:
    backgroundColor: "{colors.overlay-scrim}"
    textColor: "{colors.text-primary}"
    typography: "{typography.body-simple}"
    rounded: "{rounded.simple}"
    padding: "16px"
    width: "420px"
  wallpaper-simple:
    backgroundColor: "{colors.brand-navy-mid}"
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
  album-card:
    backgroundColor: "{colors.bg-panel}"
    textColor: "{colors.text-primary}"
    rounded: "{rounded.simple}"
    padding: "4px"
    width: "130px"
  album-card-back:
    backgroundColor: "{colors.bg-panel}"
    textColor: "{colors.text-primary}"
    typography: "{typography.body-simple}"
    rounded: "{rounded.simple}"
    width: "320px"
  album-filter-chip:
    textColor: "{colors.text-primary}"
    typography: "{typography.body-simple}"
    rounded: "22px"
    height: "44px"
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
**The status rule** (decided 2026-11-04, the same everywhere in the app: every status dot, chip, pill, tile and inline status, Simple Mode and the full UI alike; `design/preview/simple-status-colours-mock.html`). One colour, one meaning:

| Colour | Means | Examples |
| --- | --- | --- |
| Green | Good: connected and healthy, done. **Blinks** while a device works normally | Idle guider, connected camera, step done; exposing, slewing, moving, guiding, a plan running, a LIVE feed (blinking) |
| Blue | Changing (in progress): on its way between two states. Its dot is a **ring**, not filled (decided 2026-11-04, `design/preview/night-status-cue-mock.html` B): in Night mode blue and green land at nearly the same brightness, so shape tells them apart | Connecting, starting, stopping, calibrating, settling, cooling to target, parking, downloading, a target not up yet, a plan paused by Ara (it resumes on its own) |
| Amber | Warning or not ideal: needs a look, nothing has failed | A lost guide star still being searched for, a weak fit, focus not at best yet, low disk, a stale reading, a refused action, a plan paused waiting for you |
| Red | Error: something failed or a fault stands | A device that dropped, a failed capture, a crashed guider; also destructive actions and Emergency Stop |
| Grey | Off: not connected or not there | A disconnected device |

- **Connected Green** (`accent-connected`): the Green row. A device that is connected and working blinks its dot in this green (the `active` status).
- **Sky Info Blue** (`accent-info`): the Blue row (the `changing` status, drawn as a ring by the shared `StatusDot`), as well as informational text and links.
- **Dew Amber** (`accent-warning`, `accent-busy`): the Amber row only. The two tokens share a value; `accent-busy` is kept for the `busy` status dot, which now means warning: an in-between state is Blue, never amber.
- **Fault Red** (`accent-error`): the Red row. The theme's `error`.
- **Unplugged Grey** (`accent-disconnected`): the Grey row. The same value as `text-disabled`, deliberately: "not there" reads as absent, not as an alarm.

### Sky Album tiers
- **Bronze, Silver, Gold** (`tier-bronze`, `tier-silver`, `tier-gold`): a Sky Album Card's tier, as its border and the tier name. A category hue, so the State-Only Colour Rule allows it; the tier is also written and spoken, never colour alone. Each reads at 5:1 or better on Panel Grey and Night Floor.
- **On Tier** (`on-tier`): text on a tier-coloured card-back banner, 7:1 or better on every tier.
- **Art Backdrop** (`album-art-backdrop`): the black behind a Card's Art, the night sky the picture sits on.
- **Missing Silhouette** (`album-placeholder-missing`): a missing Card's Placeholder icon, 3.4:1 on Panel Alt (an informative graphic), dimmer than a collected Card's Secondary Grey.
- **The Sky Album follows Simple Mode's density** (decided 2026-11-06): it opens from Simple Mode, so its controls are 44 px tall, its header and card-back text 14 px, and its Cards' corners 10 px on front and back alike, so the flip keeps one shape. The 130 px binder grid stays: a collection is scanned, not read, so a front keeps the dense ramp (13 px label, 11.5 px name) at that size on purpose; everything the user reads or taps (the header, the flipped back, its 44 px action and credit rows) uses Simple Mode's 14 px `kSimpleText`.

### Brand
The app icon's palette, kept apart from the status colours. Only the Ara mark and Simple Mode's no-frame wallpaper use it (#1602).
- **Ara Navy** (`brand-navy-top`, `brand-navy-mid`, `brand-navy-bottom`): the wallpaper's top-to-bottom gradient, the icon tile's deep blue. Dim Text reads on its middle step at about 6.4:1.
- **Constellation Blue** (`brand-line-start` to `brand-line-end`): the mark's lines and star rings, one gradient from cyan to periwinkle across the figure.
- **Bright Star Glow** (`brand-glow`): the halo around the mark's bright star, faded to nothing.
- **Star Socket** (`brand-star-hole`): the dark centre inside each ringed star.

### Illustration greys
The device windows' drawings (the Dome window's clamshell and dial, the Flat panel window's face) are painted in their own grey ramp, outside the UI tokens on purpose: they picture hardware, so they never mark a state, and Night mode's red filter covers them like everything else (#1690 audit, 2026-11-08). State on a drawing comes only from the status tokens (a moving shell's edge in Info Blue, a target in Connected Green).
- **Dome body** `#808080` to `#4A4A4A` (top-left to bottom-right): the fixed lower half, the base and the door.
- **Dome shells** `#A8A8A8` to `#6A6A6A`: the moving shells, one step lighter than the body so they read on top of it.
- **Plinth** `#8A8A8A`; **hinge bolt** `#D0D0D0`, the brightest point, where the shells turn.
- **Dial slit** `#6E6E6E` at 95 %: the slit on the dial's Tile Fill face.
- **Flat panel face** `#111111` dark, its lids `#303030` with `#242424` ribs, and its glow `#FFFBE8` to `#B8B29A` (warm white, faded by the brightness).

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

**The Brand Stays Home Rule.** The brand palette lives in two places, the Ara mark and the no-frame wallpaper. It never marks a state, a selection or a call to action.

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

**Simple Mode's ramp** (all of it follows the user's text size, WCAG 1.4.4):
- **Readout** (300, 30px, tabular figures): the one number a mode is about: HFR in Focus, the polar error, Night done's frame count.
- **Alarm** (26px title, 20px button, 16px reason): the full-screen safety alarm only.
- **Hint** (400, 20px, 2px tracking): the "Tap the shutter to take a frame" line over the empty sky.
- **Heading** (600, 16px): the sheet's and panels' titles ("Edit M42", "Night done").
- **Body** (`body-simple`, 400, 14px): everything a user reads or taps; the minimum for running text.
- **Fact value** (600, 13px, tabular): the value in an Info or System tile.
- **Secondary line** (400, 11.5–12px, Dim Text): the line under a name in a tile, a plan square or the picked target ("Galaxy · up 62°", "L · 5 s"), and the rail captions (11px).

### Named Rules
**The Steady Digits Rule.** Every number that updates live uses tabular figures. A countdown never shifts its neighbours.

**The One Ramp Rule.** New and redesigned surfaces use the `AraText` ramp above, not per-widget 11/12/13 px styles. The ramp is being adopted incrementally, and older widgets still carry ad-hoc sizes (12 px is common).

## Layout

The shell (reference: PR #1474, merged 2026-11-02) follows NINA's arrangement:
- **Top bar (64 px):** device buttons first, then search, Night mode and a Settings gear at the right edge. The buttons run in order of use: Camera, Mount, Guider, Focuser, Filter Wheel, Rotator, Flat Panel, Switch, Weather, Safety, Dome. The Flat Panel glyph is round, as panels are: the lit panel face on, the tube's rim as a thin ring outside it and the cover's hinge on top (2026-11-07, mock `design/preview/flat-panel-icon-mock.html` D; it was a square).
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
**The Two Densities Rule.** The full UI is dense: 11–15 px type and 4–8 px corners. Simple Mode is roomy and image-first: the frame fills the window, controls float on translucent overlays, every interactive target is at least 44 px tall, corners are 10 px, and body text is 14 px. Both densities share the same colours, the same named rules and the same Emergency Stop hold; both densities' Stop fills with `emergency-stop` (#D32F2F) so its label reads at 4.5:1. Phones run Companion Mode, which builds on Simple Mode, so phones always get the roomy density. Simple Mode ships on master behind an experimental toggle (Settings → System → Experimental); this is its target.

## Elevation & Depth

Flat. Depth comes from tonal layering: Night Floor, then Panel Grey, then Panel Alt. Hairline borders separate surfaces. App bars render at elevation 0, cards are bordered rather than lifted, and no surface casts a shadow. Material dialogs and snackbars keep their framework defaults.

### Named Rules
**The Start-Stops Rule.** Whatever starts something also stops it: the shutter that starts a shot, a loop or a routine turns into a red square that stops it, and an Autofocus button turns into Cancel. No second Stop button sits beside it. Simple Mode's Plan is the one variant: a plan runs a whole night, so its shutter starts the plan (a play triangle in a white ring, like every mode but Preview), then pauses it (pause bars), resumes it (a play triangle in an amber ring), and a 2 s hold stops it. In Simple Mode, Done is green (Connected Green), as the one completing action; the full UI keeps its default Done (decided 2026-11-02: the full UI stays as it is).

**The No-Glow Rule.** Surfaces don't cast shadows or glow. A panel is separated by a 1 px Hairline and a tonal step, nothing more. The exceptions are Simple Mode's overlays, which float over the image on a translucent scrim, still with no shadow, and a gold Sky Album Card, which carries a soft gold glow (8 px blur at 35 %) as the top tier's reward (decided 2026-11-06, as the album spec asks); bronze and silver Cards do not glow.

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
A filled red button with a report icon and the label "Emergency Stop", in the bottom status bar on every tab, right after the connection indicator. While a stop is in flight it shows a spinner and "Stopping…". It confirms by holding: pressing reads "Hold to confirm" while a darker fill sweeps across, and 2 s of holding sends the stop; letting go early sends nothing. Keyboard and screen-reader activation, which can't hold, get a dialog ("Stop everything now?") whose confirm button is filled red and labelled EMERGENCY STOP (decided 2026-11-01). In Simple Mode it also carries a ring: a 2 px dark gap, then 1 px of the same red (ALP-4812), and in both densities it fills with `emergency-stop` (#D32F2F, Fault Red darkened so the white label reads at 5:1; Fault Red gives 4.2:1) under a sweep that darkens (HOLD at 7.7:1); the full UI has no ring (it moved off Fault Red and the lighter sweep for AA, #1569). It is the heaviest control on any screen: no other control at rest is filled red.

### Equipment Chip (signature)
A fixed 76 px-wide tile in the top bar (48 px when compact), so the icons sit evenly spaced. It has a 28 px device icon in Starlight Text, a 10 px status dot at its top-right corner (2 px Panel Grey ring), and the device name underneath in label-small Dim Text. Mount and Filter Wheel use hand-drawn glyphs (a telescope on a tripod, a five-slot filter wheel) because Material Icons has neither. The dot blinks green (700 ms) while the device is connected and working; amber stays for states that need a look. Tapping opens the device's Settings page, and long-press is supported. When compact the name is dropped and lives on as the tooltip.

### Status Indicator / State Chip
A pill (12 px radius) with a 10 px status dot followed by a label in body-small. The label truncates with an ellipsis in tight rows. The design rule: every state (idle, offline, draft, running, paused, attention, failed, done) has a designed look; nothing falls back to plain grey text.

### Switches and Segmented Controls
Material segmented buttons and choice chips. Selected segments use Selection Blue with white text, and outlines use Hairline. On the panels over the image (Simple Mode's device windows, the rail's popovers) an on/off is a pair of segments that name their states ("Cooler On" / "Off"), never a bare toggle, so it reads at night over any frame. A segment that takes no tap (the filters while the wheel turns) is read as disabled, not as a button, except the selected one, which is read as selected (as a native segmented control), and a label too long for its segment ends in an ellipsis (2026-11-07, #1666). A choice among many (the Filter wheel window's filters) is a list of full-width segments, one a line, the labels left-justified and centred as a block, with the slot first ("1 · Ha"), like the full UI's list. A device window's ask before a move that would disturb a running plan (Park anyway, Change anyway) is its question with Cancel and the action beside it; too narrow for that (under 312 pt, where rows put the label over the control), the buttons go under the question. In the Switches window (#1651) each port of a power box is one row: an on/off port is these segments (toggles were mocked and dropped on 2026-11-07: hard to read in Night mode), a port with a range is a slider sent when let go, its level after the slider, as the Flat panel window's brightness (one line, as wide as "100 %" so the slider keeps its width), as a percent of the port's range ("60 %"; after the name it wrapped "Light Box · 60 %" onto two lines, user 2026-11-08; anything above the bottom reads at least 1 %; the rig is still sent its own value, as ASCOM reports no unit; user, 2026-11-07), stopping only on the device's own step when the daemon reports one (a light box in steps of 10 offers 0, 10 … 100; #1680), else on whole numbers over a range of 10 or more and hundredths below; only the ends of the range read 0 % and 100 % (just off them reads 1 % and 99 %); a mode port (a writable range of at most 3 whole steps) is segments instead, named Auto / Manual / Switch for a dew channel's 0..2 mode and numbered otherwise (a 0..3 fan speed), as the full UI's mode picker; a dew output whose mode reads Auto shows "Set automatically" in place of its control, even when its driver reports it read-only meanwhile, so the user never fights the controller (2026-11-07, #1678 audit); and a port that can't be written is left out: a reading isn't a switch, so readings (voltage, current, temperature) show only in Settings › Switches, as a **Sensors** table under each device's ports (each value with the unit its name gives, as the full UI infers it: "12.4 V"; V, A, W, % or °C, and a bare number when the name says none; #1683), followed by that device's versions (driver, SDK, firmware, serial number) like any device window in Settings (user, 2026-11-07: a Sensors subsection in the dot's window and then one in Rig were each built and moved the same day); in Settings a device with nothing to switch is still listed, by name, for its readings and versions, and a device after another one has 24 pt above its title (12 pt for the first) so it reads as the next device, not one more table; a port whose range is one value isn't offered; a device with nothing to switch isn't listed, and when no connected device has anything to switch the window says "Nothing to switch"; each connected device is a section titled with its name and port count, and the camera's cooling fan is left out (the rig runs it with the cooler). While a port change is on its way that port shows the value asked and every other control in the window is locked (no ring by the port: it flashed on each fast write and pushed the name sideways, and the status pill already says it; user, 2026-11-07), since the switches take one change at a time; if the rig takes longer than 300 ms to answer, the other ports dim to `AraOpacity.locked` (0.38, Material's disabled opacity) so the wait shows (a slider too, which then takes no touch or screen-reader action, rather than going Material grey), and a faster answer dims nothing, so a write doesn't flash the window (2026-11-07, PR #1678 audit). A screen reader hears each port's name once, from its control; only the "Set automatically" readout leaves it to the row (2026-11-08, #1678 audit). Elsewhere a locked segment keeps its look, because words already say why ("Moving to Ha…", "Parking…"). A device window opened from its dot stops above the bottom row and scrolls when it is taller than the screen (a many-slot filter wheel on a short window). In a list of settings (Settings › Rig, the full UI's panels) it is the platform's switch (`Switch.adaptive`: the iOS switch on iPad and iPhone, Material elsewhere), with its label read together with it (decided 2026-11-03).

### Knobs and Dials (Simple Mode device windows)
A device window that moves something along a scale sets it with a control shaped like the hardware, not a slider (decided 2026-11-07, #1662, mocks `design/preview/simple-focuser-knobs-mock.html` B and `design/preview/simple-rotator-dial-mock.html` A). Both windows put the reading big on the left (30 pt, 600, tabular figures: the focuser's Position, the rotator's Sky angle) with the blinking green "Moving to …" under it (the focuser also shows "Let go to move to …" before the finger lifts) and the second reading below (Temperature; Mechanical angle when it differs), and the control on the right. On the focuser nothing moves until the finger lets go (the rotator waits for Start in its dial's middle); a mouse wheel turns it one click a notch (small smooth-wheel deltas add up to half a click first), and a trackpad's two-finger scroll rolls it like a finger (the dial by its vertical travel, 7 pt a degree); a scroll counts as let go once it stops; a pointer cancel moves nothing, and so does a finger dragged well off the control (a knob's width past either side, a quarter of the dial past its rim), which drops the target. A locked control (autofocus running, an unknown position, the rotator already turning) takes no finger, so a swipe on it scrolls the page, and screen readers hear it as disabled with nothing to adjust (2026-11-07, #1670 audit). For a screen reader, increase is the bigger number: out on the focuser, though rolling a knob up and the Up key go in, so the knobs carry the hint "Increase moves out, decrease moves in". The dial's numbers follow the text size.
- **Focuser: coarse and fine knobs.** Two upright knurled cylinders side by side, like a dual-speed focuser's knobs seen from the side: Coarse (72 × 180 pt) and Fine (56 × 140 pt), growing with the text, each centred on the coarse knob's height so both marks sit on one line and both captions on another, "▲ In" above and "▼ Out" under them (Material arrow icons beside the words). Rolling a knob up goes in and down goes out, the way the drawtube travels; each ridge is a click (14 pt of travel) with a haptic tick. A click scales to the focuser's travel: Coarse is 1 % of it, Fine a tenth of that, never under 1 step; a relative focuser, or one with no travel, clicks 100 and 10. Each knob's name shows under it ("Coarse", "Fine"); the click size is not shown (user, 2026-11-07). They replace the 10 / 100 / 1000 step picker and the round dial. Autofocus is a full-width button under them, green (Connected Green, dark words) to start and red (Emergency Stop red #D32F2F, white words at 5:1; Error Red would give 4.2:1; no stop square) reading Cancel autofocus while it runs (user, 2026-11-07); the knobs move nothing then, or while the position is unknown.
- **Flat panel: the panel face.** No scale to set, so the Flat panel window keeps segments: Cover Open / Closed and Light On / Off rows, each label over its two segments (which share the row's width), with a drawing of the panel face beside them, 140 pt in the 360 pt window, as it sits on the scope (decided 2026-11-07, #1662, mock `design/preview/simple-flat-panel-mock.html` B). Two ribbed lids cover the face and slide apart as the cover opens (half open while it moves), and the face glows warm white with the brightness (dim while the light warms up). The drawing takes no tap and screen readers skip it, since the rows say the same; it shrinks when the rows need the room (they keep 176 pt, scaled with the text) and is left out under 96 pt. Under them, Brightness is a slider from 0 to the panel's maximum, the level beside it as a percent of that maximum ("50 %"; anything above 0 reads at least 1 %), sent once on release in the panel's own units (user, 2026-11-07); until the panel reports its maximum it is disabled and reads "—". While the cover moves, blinking green "Moving…" sits beside its label and both segments lock (one motor); while the light warms up, blue "Warming up…" sits beside the Light label and its segments lock. The segments never give way to the words, so the window keeps its size (user, 2026-11-07). A panel without a cover has no Cover row and no lids; one without a light has no Light row and no slider.
- **Weather: reading tiles.** Nothing to set, so the Weather window is read-only: tiles two across (tile fill, 10 pt corners), all one height (80 pt, scaled with the text: the number and two caption lines; a longer caption ends in an ellipsis; user, 2026-11-08), each with an icon in front of the reading (22 pt, Text Secondary; user, 2026-11-08: a cloud, stars for sky quality, a thermometer, a leaf with a drop over its broken edge for dew point (drawn as a glyph: Material's dew point mark read as a second thermometer; mock `design/preview/dew-point-icon-mock.html` H, user 2026-11-08), a drop for humidity, a cloud with rain (Cupertino `cloud_rain`: Material has no rain cloud), air for wind, a crescent for the moon), the reading big on one line (22 pt, 600, tabular, its unit beside it in 13 pt Text Secondary; sky quality's mag/arcsec² is too long for a half tile and sits on its own line under the name, whole: after the name it broke at its slash) and its name under it (12 pt), in this order: cloud cover, sky quality, temperature, dew point, humidity, rain in mm/h ("0 mm/h" when dry: the word, "Nenhuma" in Portuguese, didn't fit a narrow half tile; drizzle under 0.1 reads "< 0.1 mm/h", not a "0.0" a hair from dry), wind in km/h with its compass point and gusts in the caption ("Wind · NW · gusts 27"), then the moon, computed, not read ("3 %", "Moon · Waning crescent"; user, 2026-11-08). With an odd count the last tile spans the row, so a row never has a hole (user, 2026-11-08). A reading the station doesn't report, or reports as NaN, has no tile; pressure, sky temperature and the other computed rows stay in the full UI. While "Weather triggers safety" is on, a reading past the rig's safety limit (wind, the worse of speed and gust, over its km/h limit; humidity over its limit; the air closer to the dew point than its margin, the daemon's same checks) gets a 1.5 pt amber outline, its icon in amber and the limit on a line of its own under the name ("limit 85 %", one line; the name and details then keep one line, so the reason for the amber is never the part cut off). The number turns amber only when it is the value past the limit: when a gust trips the wind limit, the sustained speed stays plain and "gusts 36" in the caption turns amber, and the caption shows only the name and that gust (the compass point drops out, still read to a screen reader) so its one line can't cut the gust off. A NaN wind reading marks nothing, as the daemon's check. A screen reader hears the value next to the name, then the details, then why: "Wind 18 km/h, gusts 36 km/h, past the safety limit (limit 30 km/h)"; "Sky quality 21.34 mag/arcsec²". If the safety limits can't be read, an amber line under the tiles says "Safety limits not loaded: nothing is marked." rather than let calm tiles pass for within limits (2026-11-08, #1682 audit). Under the tiles, "Updated 8 s ago" counts up once a second (only that line redraws); past a minute it turns amber, "Last reading 4 min ago". A failed read says "Sensor read failed — check the device." in Error text and shows no numbers. No status pill and no plan guard (decided 2026-11-08, #1662, mock `design/preview/simple-weather-mock.html` B).
- **Dome: the clamshell from the front, and a dial from above.** The azimuth big on top ("212°", 26 pt, 600, tabular, its compass point after it in Text Secondary, a turn's target "→ 245°" in Connected green) with "Azimuth", or "Azimuth · Parked" / "At home", under it. Under that, side by side, 150 pt each in the 360 pt window, the dial on the left and the drawing on the right (user, 2026-11-08), the drawing filling its box so both read the same size: the dome from the front, drawn as a Baader AllSky clamshell from the user's photos (a sphere on a base with sloped shoulders and a round plinth, the door running up into the sphere; each side of the top two nested shells fanned on one hinge bolt at the centre, so closed they meet with no gap; no sky or scope inside, user 2026-11-08), and a dial from above (North up, a tick every 10°, the slit at the azimuth, 32° wide (user, 2026-11-08: 16° read too narrow), the target as a thin green slit). Opening, the shells fan outward and down, the outer ones twice as fast, and stop with their lower edges on the base's shoulders; closing, they fan back and meet at the crown. The daemon says only "shutter moving", so the drawing slides over the time this dome's last full move took, timed by Ara from its own ask to the end it reads (the dome is read every 1.5 s for up to 5 min of a move) and kept on this device (60 s until one has been seen; a move a plan or the safety policy started is drawn but not timed, since it is seen up to a poll late), at that pace to 80 % and then slowing toward the end without reaching it (no freeze at a cap if the dome is slower), and once the dome reports done the shells ease the rest of the way in half a second rather than jump (user, 2026-11-08: the end looked glitchy); which way comes from the shutter's state before it moved ("Opening…" / "Closing…" in blue; "Shutter moving…" when Ara didn't see the start, the shells then half open and still). Reduce motion: no slide. Drag round the dial or tap it to point a whole-degree target; a mouse wheel steps it a degree a notch and two fingers on a trackpad (or a Magic Mouse) 7 pt a degree from the target already picked, as the rotator's dial (where the fingers rest picks nothing); a screen reader's increase / decrease and the arrow keys step it too (up or right clockwise, a 2 pt Info Blue focus ring, as the rotator's); its N / E / S / W are the reader's language's compass letters, as the azimuth's, in boxes that grow with the text size (#1690 audit and re-audit); its middle is the rotator's hub: a green play turns the dome there, grey with no target, and the red stop square while the dome moves (turning or the shutter). Turning reads "Moving…" in blinking green. A shutter the daemon reads as faulted (ASCOM ShutterState.Error, e.g. after Stop mid-move on the simulator) reads "Shutter fault: open or close again." in Error text in the same line, and neither shutter segment reads as selected, so either one clears it (a driver clears the fault on the next open or close). What was asked shows from the tap until the dome is done, in the moving line and the status pill, as Mount's Park does: "Parking…", "Finding home…", "Turning to 245°…" (blinking green), "Opening…" / "Closing…" (blue ring); a fast dome, or one already there, would otherwise move between two reads and show nothing (user, 2026-11-08). An ask with nothing moving 3 s later clears. The moving line keeps its room while the dome is still, and a roll-off roof's Stop tile stays (dimmed), so the window keeps its size as the dome starts and stops (user, 2026-11-08). Then, 12 pt clear of the drawing, the Shutter Open / Closed row and Park and Home as full-width tiles. Moving locks the segments, the dial, Park and Home, never Stop. Too narrow for both (under 96 pt each), the dial stays and the drawing goes. Only what the dome reports it can do shows: a roll-off roof has no dial, the drawing alone and Stop as a tile. While a plan runs, a turn, a shutter change, Park or Home asks first in the window; Stop never asks. Sync, set park and slaving stay in the full UI (decided 2026-11-08, #1662, mock `design/preview/simple-dome-safety-mock.html` v13).
- **Safety monitor: one state tile.** Read-only: a tile (tile fill, 10 pt corners, 80 pt) with a shield (34 pt) and "Safe" in Connected green or "Unsafe" in Warning amber with a 1.5 pt amber outline (amber, not Error red: the monitor works, the sky doesn't), "since 02:14 · 1 h 12 min" under it in local 24 h time, counting on (left out until a change is seen). While Unsafe, one caption line says what the rig's safety policy does next: "Resumes 10 min after Safe" or "Stays paused until you resume"; the window loads the policies on open, never writes them, and says nothing until they load. A screen reader hears "Unsafe, since 02:14 · 1 h 12 min". The tile says what the rig is: Unsafe also when the monitor reads Safe but a weather reading is past its safety limit, with "Solo reads Safe" under it in place of since when (#1690 audit). Under the tile, while the rig is unsafe, **Why** then **What Ara does** (12 pt headings, rows with an 18 pt icon in 13 pt text): the monitor's own line ("Solo says unsafe. It doesn't say why: check the device.": an ASCOM safety monitor reports only safe or unsafe), any weather reading past its safety limit in amber while weather triggers safety ("Wind 41 km/h, over the 36 km/h limit.", the daemon's same checks; the pill shows the same words without the full stop; then the On unsafe policy in words ("On unsafe, Ara pauses the plan, parks and closes the dome.", and for that policy alone the resume line under it). Read from the policy, never written; nothing about what Ara does until it loads (2026-11-08, user). A failed read says "Sensor read failed — check the device." and shows no state (decided 2026-11-08, #1662, same mock).
- **Rotator: the sky-angle dial.** The round dial the Focuser had: its whole face turns under a fixed blue mark, a 0–360° scale with a tick every 5° and a number every 30°, running clockwise from 0 at the top, 180 pt (at most 55 % of a narrow window). The number under the mark is where the rotator is or, once turned, the target: clockwise brings lower numbers under it, each click is one degree (no step picker), and turning back to where the rotator is clears the target. Letting go moves nothing (unlike the focuser's knobs): the target waits for the dial's middle, which works like the shutter: **Start**, a play triangle, green with a target and grey and taking no tap without one, moves the rotator there (asking first while a plan runs, as Sync does) and turns into **Stop**, the red stop square, while the rotator turns, whoever turned it. Under the dial, a full-width **Sync** on the tile fill tells the rotator it is at the target without moving it ("Sync to 75°"; dimmed with no target) (user, 2026-11-07). Every angle in the window (the readings, the targets, the screen reader's value) is in whole degrees, as the dial sets them; 359.6 reads 0°, and the full UI keeps the decimals (user, 2026-11-07). Reverse stays in the full UI.

### Simple Mode: Image First (two rails)
Simple Mode (see PRODUCT.md) puts the latest frame first. It is simpler than ASIAir, not a copy of it. The image fills the window edge to edge, and the controls float over it on translucent panels. The layout below was approved on 2026-11-01 and is built on master; it sits behind Settings → System → Experimental → Simple Mode (off by default, per client), and the system status bar is hidden while it shows.
- **Panels:** every panel uses `overlay-scrim` (Night Floor at 90 %, so panel text reads at 4.5:1 or better over a dark sky, a grey frame or a white flat; raised from 72 % on 2026-11-02) over a 5 px blur of the image behind it (`AraBlur.panel` in code, `panel-simple`'s blur in `.impeccable/design.json`; one shared blur for all panels (`BackdropGroup`, measured on the iPad 6 on 2026-11-05: one filter per panel cost ~30 ms a frame against ~6 ms shared and ~4 ms with none): the Settings sheet's and the device windows' look, on every panel since 2026-11-04 for legibility; the strip, rails, status pill, message pills, Info tiles, the Night Setup strip, the plan squares and every popover), 10 px corners and a 1 px Hairline contour drawn on top of the panel so it takes no room (decided 2026-11-08 after a panel vanished into a frame of its own colour on a real night; mock `design/preview/simple-panel-contour-mock.html`; a message pill that has its own colour keeps it), and sits 16 px from the frame edge and from its neighbours. Error words on a panel use `error-text` (#FF8A80), Fault Red lightened for the scrim; Fault Red itself stays for fills and the E-stop. Text-button labels on a panel use `link-text` (#64B5F6, Info Blue lightened: 4.5:1 on a tile too) and outlined-button labels white; the button primary (#1565C0) is for fills only, since as text it reads at 3:1 on a panel and 2:1 on a tile. A selected or current item on a panel uses `selection-tint`, with its words and icon in `link-text` (Info Blue reads 4.3:1 on the tint over a white frame).
- **Fixed slots:** nothing moves when state changes. A panel with nothing to show is not drawn, and the panels around it keep their places.
- **Top:** a full-width status strip, 56 px tall: the OpenAstro Ara mark (the app icon's constellation without its tile, decided 2026-11-05, #1602), which opens a small **OpenAstro Ara** sheet under it (This app, Ara server and Guider versions, the rig's name and the selected Profile; no Settings row, the gear is Settings; as wide as its text up to 420 px, then values wrap), set apart by a 28 px Hairline so it doesn't read as one more device (2026-11-06), then the device dots (connected gear only; a fault still shows; with none, a note that opens Settings › Devices: "Rig not connected" in amber, 2026-11-06, or "No devices connected"), then Search, Night mode and Settings. Search (⌘K too) is Simple Mode's own: it finds sky objects (the sky points at the hit, with Image this), Simple Settings rows (the sheet opens on their section), the modes and imaged targets (the Image Library, filtered); nothing it finds leads to the full UI (decided 2026-11-03). Nothing hides under a ⋯ menu, and nothing on it leads to the full UI. Settings (⌘, too) opens Simple Mode's own Settings, a sheet over the image under the gear, at most 640 pt wide (growing with the text; window-wide until 2026-11-04), its Rig values all one width (160 pt): **Rig** (the rig's figures, name and profile with Change, telescope, site, Permanent pier, the Simple Mode switch, language, versions, logs) and **Devices** (the profile's devices with Connect / Disconnect / Reconnect, each opening its device window).
- **Camera status:** a pill at the top-right, 16 px under the strip and at most 300 px wide (half the row on a narrow window), like a camera's REC light: "Taking a frame · 3 s left" with a thin bar, Looping, Stopping…, Frame ready, or why it failed. A running plan's step is its own pill under it (see Status pills below), not this one between frames: "Plan · Cooling camera to -10 °C", a blue ring while Ara paused it, amber when it waits for you, with a thin line along the pill's foot showing how far the step has got when it reports that (cooling, a wait, autofocus); a scheduled plan says when it starts ("Plan · Starts at 21:47") (decided 2026-11-02; stacked pills 2026-11-04). Nothing while idle. It stays in the immersive view and moves up into the corner.
- **Status pills (top right, decided 2026-11-04):** one pill per thing happening, stacked under the strip in this order: the main camera's shot or loop, a running plan's step, the safety monitor's Unsafe ("Safety monitor · Unsafe since 02:14", steady amber, until it reads Safe; or, while the monitor reads Safe, the first weather reading past its safety limit, "Safety monitor · Wind 41 km/h, over the 36 km/h limit"; right after the plan, as it is why a plan holds; 2026-11-08, #1668), a park, a filter change ("Filter wheel · Moving…", blinking green as the wheel's dot and window, or amber "Swap in Ha, then confirm." for a manual wheel; 2026-11-07, #1666), the focuser moving ("Focuser · Moving…", blinking green, whoever moved it; not while autofocus runs, which has its own pill; 2026-11-07), the rotator turning ("Rotator · Moving…", blinking green, whoever turned it; 2026-11-07, #1668), a switch port change ("Switches · Setting Dew 1…", a blue ring until the rig answers, naming the device too when another connected device has a port of that name ("Setting DC1 (Gemini)…"); #1651), the flat panel ("Flat panel · Cover moving…", blinking green, or "Light warming up…" with a blue ring; the cover wins when both; 2026-11-07, #1668), the dome (from the tap of a move asked in its window: "Dome · Parking…", "Finding home…", "Turning to 245°…" blinking green, "Opening…" / "Closing…" with a blue ring; otherwise "Dome · Moving…" blinking green while it turns, parks or finds home, or "Opening…" / "Closing…" with a blue ring while the shutter moves, "Shutter moving…" when Ara didn't see it start; 2026-11-08, #1668), manual cooling, a Focus routine (autofocus, the Bahtinov readout, guide-camera focus), polar alignment, the guider. Every mode has a pill while it runs; a failed routine says why as a message on the left instead. Three at most, the rest fold into "+n". Each follows the status rule (colour, blink while working), keeps its own steady width, and a tap opens what it is about (the mode's panel, or the Mount, Main Camera, Focuser, Filter wheel, Rotator, Switches, Flat panel, Dome or Safety monitor window).
- **Messages (no popups, ALP-4825):** results, errors and questions show as pills stacked in the top-left corner, beside the status pill and never under it (they share the row, up to 420 px), under the Night Setup strip when it shows, as many as fit above the left rail; the rest fold into "+N more" (at the end of the newest pill when there is no room below). A pill shows its whole message, however many lines (an Emergency Stop's summary was cut at two, 2026-11-04). A result has a green dot and fades after 10 s (6 s until 2026-11-04; a line along its foot shows the time left); an Emergency Stop's summary (the stop also ends a guider that is looping or on its star, the guide camera's live loop included, as a safety reaction does) is amber when it went through (done, but the rig needs a look) and red when a step failed, and stays until closed with ×, with no time-left line, as does the red error for a stop that never reached the daemon (ALP-5016; both are sticky: the only errors that don't fade); an error has a red outline and light-red words and fades after 20 s on screen, or on × (not a sticky pill, 2026-11-04); a routine that fails (autofocus, polar alignment) says so this way, with why; a question has an amber outline and its buttons and stays until answered. Each is announced to screen readers. They stay in the immersive view. In the full UI the same messages are its snackbars. The one exception is the safety alarm (safety monitor or weather unsafe): it stays a full-screen warning over everything, the immersive view included: on `alarm-scrim` (a deep red veil over the whole window), a large red circled "!" (Material `error`; not a bell, which reads as a notification, and not the octagon, which is Emergency Stop's), "SAFETY ALERT", the reason, what the rig did, and a large red **Silence alarm**. It shows at once and the tone follows after the device's silent delay (5 s by default), so someone at the screen can silence it before it rings; Silence only stops the sound, and once silenced the screen closes and the status pill keeps the unsafe reason in red until the daemon reports safe (decided 2026-11-02).
- **Right rail (72 px wide, only as tall as its controls, centred between the strip and Stop), from the top:**
  - the mode name. Polar align shows as "P.A." and Night Setup as "Setup" here, since the full names don't fit 60 px; the grid and screen readers keep the full names. A tap opens the six modes as a grid beside the rail: Night Setup, Preview, Focus, Polar align, Guide, Plan;
  - the shutter (60 px), always in a white ring. Only Preview's is a camera button, a filled white circle: Single takes a saved frame, as Take One does today, and Loop runs live view, whose frames are not saved. Every other mode's shows a white play triangle and starts that mode's routine (Focus, Auto or Manual; Polar align; Plan; Night Setup follows its step), and Plan's ring turns amber only while paused (decided 2026-11-03). While it works it shows a square that cancels;
  - the settings chip, which speaks for the current mode. In Preview it reads "5 s · 100" and its popover holds Camera (one row per connected camera, its role over its model, the selected one in `selection-tint`; decided 2026-11-08, mock `design/preview/simple-preview-camera-mock.html` B; a locked row gives its reason on the row and, tapped, in a pill, decided 2026-11-09), Exposure (a preset list trimmed to the camera's limits), the Gain slider under it (the camera's gain, the same slider and value as the Camera window; decided 2026-11-03), Filter (only with a filter wheel connected), and Loop / Single. On the Guide Camera the chip reads "Guide" over its exposure and gain, Exposure and Gain are the guide camera's, frames always loop through the guider and Filter and Loop / Single go; the row is greyed out, saying why, while the guider holds its camera, and Preview is on the main camera then; there is no Save frame button. In another mode it reads that mode's summary (Focus: "Auto" or "Manual", the method Smart Focus would use; Polar align: "1 s" over "Loop"; Guide: the RMS while it guides, "Off" otherwise; Plan: the total imaging time, "2 h" over "45 min") and shows or hides that mode's panel. The chip is 72 px tall in every mode, the settings icon over up to two lines, so the rail is the same height whatever the mode (decided 2026-11-02).
- **Popovers** (the mode grid, settings, and a mode's panel) open to the left of the right rail, level with its top, 16 px from it. Opening one never moves the rail. A tap on the image closes an open popover.
- **Stop:** the compact Emergency Stop, a 60 px square reading STOP, on its own at the bottom right under the rail. It fires after a 2 s hold and reads HOLD while held; keyboard and screen reader users press it, then press again within 3 s (it reads PRESS AGAIN, no dialog).
- **Left rail (72 px wide, centred between the strip and the bottom):** Sky (the planetarium at any time, over a frame; an object tapped there shows a card under it with its name, type and altitude and Image this, which opens Plan's sheet with it), Info (toggles the last frame's facts) and Library (the Image Library screen). Each is an icon over a caption.
- **Info:** a row of fact tiles (72 × 56, label over value, on `overlay-scrim` with a 1 px Hairline border) hugging the bottom-left corner, just above the bottom row, HFR nearest the corner, then Stars, Exposure · filter, Gain · offset, Clipped; on Preview's Guide they are the guide camera's HFR, Stars, Exposure and Gain, and the main camera's frame and histogram step aside (decided 2026-11-09). The bottom-right corner belongs to the plan squares, the top-left to the message pills.
- **System:** a row of the same tiles, in the bottom-left corner alone or 8 px above Info's row while Info shows: Storage (free on the save drive, or Unplugged), CPU, Memory and Up, re-read every 5 s while it shows. CPU and Memory are Ara's own process until the server reports the whole Pi. CPU temperature, under-voltage or throttling and disk I/O are next; they need the server to report them.
- **Bottom row (56 px tall, growing with the text above 1.1x like the rails; 16 px from the left edge and 16 px from Stop):** the guiding graph ("Guiding", the RMS, the RA/Dec trace) and the frame's histogram (plotted over the range the frame uses), each only once it has data. A lone one fills the row; both split it equally, guiding on the left.
- **Centre:** the attention banner, centred 16 px under the status strip, only when something needs the person (the daemon's prompts become question pills in phase B).
- **Immersive view:** a tap on the image, with no popover open, fades every panel out, Emergency Stop included (220 ms ease-out, instant with reduced motion), so the frame shows fullscreen; the camera status pill and any attention banner stay. A tap brings the panels back. There is no keyboard shortcut for now.
- **No frame yet:** the wallpaper (decided 2026-11-05, #1602, `design/preview/simple-branding-mock.html` E on branch `prototype/simple-branding`; stars only, the constellation dropped 2026-11-06): the brand navy and three layers of stars. On an iPad or iPhone the layers shift by depth as the device tilts, on the screen's axes in either orientation (accelerometer; about 20° is full tilt, eased, with a dead band so a rig on a stand doesn't redraw, and the centre drifts back to how it is held). They stay still, and the sensor isn't read, with iOS Reduce Motion or Android's Remove animations, and on desktop. Sky shows tonight's planetarium over it; the first frame closes the Sky. "Tap the shutter to take a frame" stays centred. Until 2026-11-05 tonight's planetarium (sky only, no in-page controls) filled the window here.
- **Leaving:** the frame icon in the full UI's top bar enters Simple Mode. Simple Mode's Settings (the gear, ⌘,), its search (⌘K) and the device windows (every device dot: Main Camera, Mount, Focuser, Filter Wheel, Rotator, Switches, Flat Panel, Weather, Safety, Dome and Guide Camera; the Filter Wheel, Rotator and Flat Panel windows since 2026-11-07, the Weather, Safety monitor and Dome windows since 2026-11-08, #1662; the Switches window since 2026-11-07, #1651; Camera and Guider were renamed on 2026-11-04, and the Guide Camera window keeps only Gain and, in Settings, its versions: State, RMS and dither moved to the Guider mode) stay inside it. Not yet: Settings › Rig › Logs and Open Storage still open the full Settings page over Simple Mode, closed with Back (#1659, #1660). The way out is the Simple Mode switch in Settings › Rig.

**Simple Mode's rule: 4×90% and KISS.** Simple Mode covers the top 90 % most used features, for 90 % of the people, 90 % of the time, 90 % faster, with only the bare minimum for a successful imaging night: defaults and automation first, self-recovery doing the work, and a small set of controls where the user needs them (ASIAir as the reference); a value one Simple Mode control sets is not repeated elsewhere, and nothing links to the full UI. No redundant design: each thing lives in one place, and interactions are contextual (tap the thing to act on it). Its Settings (a sheet over the image with a few controls, ASIAir as the reference; one window per device, the same one its top-bar dot opens; Connect / Disconnect on Settings › Devices only) are shaped in `design/briefs/simple-mode-settings.md` (2026-11-03).

**Modes.** The mode grid reads Night Setup · Preview · Focus · Polar align · Guider · Plan (Plan was named Run until 2026-11-02; Guider added 2026-11-04 as Guide, renamed 2026-11-06). Targets are picked in Plan's sheet or in Sky; there is no Target mode (decided 2026-11-02), so Go to, Solve and Sync without a run stay in the full UI. Each mode other than Preview opens its own panel to the left of the right rail when it is picked (a floating card like the settings popover, pinned beside the rail and level with its top), and the settings chip shows or hides it; where needed the mode also draws on the image. The rails, the strip and Stop never move. All six modes are built.
- **Preview:** the right rail itself: shutter, settings chip, Loop or Single. The last frame's facts come from Info.
- **Polar align:** the rail reads "P.A." and the chip shows the routine's setup ("45 s" over "Single"). The shutter starts the routine, and while it runs its red square stops it, in Loop and Single alike. While the routine runs, the image area shows the polar-align camera's latest frame. Nothing is drawn on the frame: the server reports the altitude and azimuth errors, not where the pole sits in the picture. The mode panel holds Exposure and Loop / Single (the profile's polar-align settings, applied to the next frame). While the routine runs it also shows the phase, the total error with its zone dot (green under 10′, amber under 60′, red beyond, matching the server's thresholds), the phase with the total error beside it, then the full UI's bullseye (180 pt) with the altitude and azimuth turns beside it as tiles, a 44 pt arrow over the axis and the move ("Lower 3.2′", "Move west 14.1′"), the panel 420 pt wide with Loop / Single and Exposure side by side so it all fits beside the rail on an iPad (decided 2026-11-03; before it starts, and at large text, they take a row each: user, 2026-11-04), failed solves, then Take frame (Single only; Loop shoots on its own) and a green Done (once inside the profile's tolerance). The panel has no Start or Stop: the shutter does both. When one axis got worse after a turn while the other held, an amber line says "Altitude got worse. Turn that bolt back past where you started." (the full UI's bolt-direction hint, cut to two lines).
- **Guider** (decided 2026-11-04 as Guide, `design/preview/simple-guide-mode-mock.html`; renamed 2026-11-06 because the mode holds the guider's settings): the rail reads "Guider" (pt-BR "Guia", the app's word for the guider; icon: a target) and the chip shows the RMS while it guides, else "Off". The shutter starts the guider's whole routine: loop, pick a star, calibrate when needed, settle, guide; its red square stops it. The panel (340 pt, left of the rail) says what the guider is doing (Idle, Calibrating · leg, Guiding; "Not guiding" read as a failure, 2026-11-04), how to start when idle, the RMS in arcseconds big with RA and Dec under it, the guide camera's Exposure (a tile like Preview's) and Gain (the Guide Camera window's slider, one value), both sent to the guider live, RA and Dec aggressiveness (one line each, axis, slider and value) and Dec guide mode (a tile like Exposure), also sent live, and Dither (every N frames, Off) and Dither by (px), which the plans use. A tap says **Starting…** or **Stopping…** at once (the guider takes seconds; a stop waits out its frame). Its dot, the status pill's and the top bar's share one colour rule (a lost star amber, settling blue). In Night Setup, once guiding, it shows **Done · Start imaging**. The bottom row's graph reads **Guider** and shows only while the guider runs (decided 2026-11-04). The aggressiveness sliders run 0-100 % in 5s and Dec guide mode offers Auto, North, South and Off. They were in the Guide Camera window from 2026-11-08 so the panel would fit an iPad without scrolling; one line per slider and a Dec mode tile brought them to the panel (decided 2026-11-09), and it still fits. Gain is one line there too. The mode panels share Plan's bottom limit, 8 pt above the bottom row, and keep clear of the plan squares; the Guider panel is also centred between the status pill and the bottom row, as Plan's sheet is (decided 2026-11-09).
- **Focus:** a zoomed star with its HFR ring sits top-left under the status pill. The mode panel (400 pt wide, as built; Polar align's is 420) shows the headline, the focuser's position, temperature and model, the HFR and the focus curve as it builds (160 pt tall, the same floor as the full UI: the axes take 62 pt); a finished run adds best position, stars and fit R² as tiles and, when the fit was weak, the full UI's weak-fit warning, all on view (nothing folds away). Measured stars On / Off (off to start, per session) fills the screen behind the panels with the run's own frame, its measured stars circled (decided 2026-11-03, `design/preview/simple-focus-results-mock.html` layout A); the focuser is moved with the Focuser window's coarse and fine knobs (its dial until 2026-11-07), so the panel has no position or nudge buttons (decided 2026-11-03). The panel opens with Main / Guide (which camera) and, for the main telescope, Auto / Manual on a row under it (locked while anything runs; one line read as four of a kind, user 2026-11-04); a finished run's headline shares its line with Measured stars On / Off, so the panel still fits an iPad. Guide focuses the guide camera by hand, as the full UI's Guide camera tab: what to turn (guide scope or off-axis guider), the blockers in words (connect the guider; stop guiding first; an off-axis guider waits for the main telescope, with Already in focus), the live HFR headline, Stars and Frames, the turn advice and the exposure (1 / 2 / 3 / 5 s); the shutter starts and stops it and its frames fill the screen; the chip reads Guide (decided 2026-11-03, `design/preview/simple-guide-focus-mock.html` A). The shutter follows it: on Auto it starts autofocus and its red square cancels it; on Manual it starts the Bahtinov readout (1 / 2 / 4 / 8 s frames, 2 s to start) and its square stops it. Manual's panel is the full UI's Bahtinov helper, flat: the crop with its spikes, the offset in px from focus in big type, the turn advice in words (Make a move, Keep going, Go back, In focus) and Finish, which asks for the mask to come off inside the panel (Not yet / Mask removed); leaving Manual before Finish asks the same (decided 2026-11-03, ALP-4874).
- **Night Setup** (named Autopilot until 2026-11-03 and Guided Run until 2026-11-05; Plan first since 2026-11-03, `design/preview/simple-night-setup-mock.html` B): the night's steps in order, Plan → Focus → Polar align → Guide (focus before polar alignment: it plate-solves and fails on a soft focus; Guide added 2026-11-04), each on its usual panel, with a step strip centred under the status strip (the steps behind the current one carry a green tick when done tonight, Plan once a queue is in, or an amber skip mark when skipped; the current one is lit; Back and Skip). **Plan** is the plan sheet (beside the rail, as in Plan) with no title (the strip names the step; decided 2026-11-04), that only queues: "Imaging starts after focus and polar alignment." over Add to queue and a green **Next: Focus** (Next: Polar align, or Start imaging when both are done tonight), enabled once one target is queued. A finished autofocus moves on to Polar align, which starts its routine on entry (an exception to the shutter starting things; its red square still stops it); Guide does the same with the guider's routine, and leaving it keeps guiding for imaging. Polar align's Done reads **Next: Guide** when Guide follows. The last setup step's green Done reads **Done · Start imaging** and starts the queue (at astronomical dark when it is before dark, as the sheet's own Start at would); on a permanent pier, or already aligned, and when Guide is skipped too (already guiding, or no guider), Focus is the last step and shows that Done once it has focused, so imaging never starts on its own. Starting imaging ends Night Setup and leaves you in Plan. The rail reads "Setup"; it starts from the mode grid (Preview's "Start Night Setup" offer was removed, user 2026-11-04). The chip names the step whose settings it shows ("Plan" over "2 h", "Focus" over "Auto", "P.A." over "45 s"). Steps already done tonight are skipped (focus: an autofocus finished this session; polar align: inside tolerance this session), and a permanent pier skips Polar align; Guide is skipped while already guiding or with no guider connected (an unguided rig images without it). Outside Night Setup every mode stands alone: nothing opens another.
- **Plan:** the quick plan builder is a sheet beside the right rail like every mode's panel (centred over the image until 2026-11-04), between the status pill and the bottom row (it scrolls inside when taller), with the rails still visible. On a wide window tonight's best sits in a 240 px column to the left of the plan; on a narrow one it stacks on top. On top, tonight's best: up to four tappable targets from the Tonight's Sky ranking (name, then type and altitude or when it rises; hidden with no site), then the target: a search (the planetarium's own, so the sky shows behind the sheet while it searches) with the picked object's name and "62° up · max 74°" or why it can't be reached, and Pick in Sky, which closes the sheet for the sky (its card's Image this brings the sheet back). Then the filters as chips (a picked one in the segments' solid selection blue with white words, decided 2026-11-04) and a row per filter with exposure and frame count, the total frames and time; the frames split the picked target's dark hours left tonight across the rows (60 frames split without a window), a new target refits them and a new sub length refits its row; autofocus, dither and flats follow the profile (one set to ask gets none, since Simple Mode has no popups, and a pill says so; decided 2026-11-09). Tapping a value opens the same preset list as the Preview settings. Rows start at the profile's default exposure; one line under them names the suggested sub lengths that differ ("Suggested: L 2 min · GB 3 min", the plan chooser's sky-based numbers) with Use all, and each exposure list marks its suggestion. Without tonight's polar alignment (and no permanent pier) an amber line reads "Not polar aligned tonight"; it warns and never blocks. The sheet ends with two buttons. **Add to queue** (solid `button-primary` with white words, like the queue panel's Add target; outlined until 2026-11-04) adds the target to the end of the queue; **Create and start** (green) creates the plan and starts it, and once a queue exists it reads **Start now**: the target goes first and imaging starts (a second exception to the shutter starting things). A target below the horizon can be queued but not started; one that never rises here can't be queued. While imaging, the sheet only adds. Add to queue keeps the sheet up for the next pick: a green line says "M42 added · 3 in the queue" and the pick clears; Create and start / Start now close it (decided 2026-11-02). Before dark the green button names when the plan starts, **Start at 21:47** (astronomical dark from the site's twilight setting), with a line under it: "Starts at dark (21:47) · Start now instead · Pick a time"; once dark it reads Create and start with "Starts now · Pick a time". Pick a time opens a time wheel inside the sheet (5-minute steps; a time earlier than now means tomorrow). The plan starts on the rig at once and waits there: until 15 minutes before the start, then it cools the camera, then until the start (decided 2026-11-02). Every plan parks the mount after its last target, before the camera warms up; a plan that starts in the dark also stops at dawn (the site's twilight setting), and a dim line under the start line says when: "Stops at dawn · 05:12". A target not started by dawn is skipped, no frame starts that would end after dawn, and anything still running at dawn is interrupted (the daemon's TimeCondition checks before each step and watches the clock every second). Start now in daylight parks but has no dawn stop, so an indoor test runs on. The dawn stop is settled when the queue starts, from its start time (the daemon reads a dawn stop started in daylight as already past and would skip every target), and the line says what that start will do; targets added while it images keep it, those added from the full UI's Planning too (#1560). The rail ▶ also creates and starts when a target is picked and nothing is queued, and starts a queued plan.

The queue is one sequence on the rig: each target is a block in it, and adding while it images joins the running sequence (decided 2026-11-02). Plan squares (72 × 100) hug the bottom-right corner, just above the bottom row and below the right rail, and line up right to left in queue order, the first in the corner (moved from the top right on 2026-11-02 to clear the top for the message pills). They take the right half of the window (the Info tiles have the left); past what fits, the target in hand and those after it show, then one "+N" square with the rest's total time (finished targets drop out first). While Plan's sheet shows, the squares step aside so the sheet keeps its full room down to the bottom row (decided 2026-11-02; it used to end above them, and two queued targets made it scroll). Each shows the target's catalog tag (a curated region's own, "IC 434"; else its best designation, M before NGC, IC, Sh2, B and LBN; else its name, shrunk to fit), frames done of its total ("0 / 60", "23 / 60"), the filter and sub length in hand ("R · 3 min"), and its time: the total before it starts, the time left while imaging, "rises 22:40" while a queued target is still below the horizon (the shutter waits for the first target's rise), "Done" once finished. The queue's last square (the leftmost, or the "+N" square) adds a fifth line, "Parks" (pt-BR "Estaciona"): the mount parks once the queue ends; it goes once the queue is done. One short word and no icon, because the line has 60 px and anything longer shrinks below the other lines' 11.5 px (measured in Roboto, #1560). The square in hand is green when ready, blue with a progress line along its foot while imaging, amber "Paused"; the others are green. Tapping any square opens the queue panel beside the rail (the chip opens it too while imaging): "QUEUE · 3 targets · 6 h", then each target with its tag, frames of its total and time left. A tap on a target that hasn't started edits it: the sheet opens on its rows only ("Edit M42", the target stays; Cancel / Save), and Save replaces its block on the rig and puts it back in place, live while imaging. × deletes a target that hasn't started. Add target opens the plan sheet. While imaging the panel first shows the target in hand (frames per filter, when the queue ends) and the resume choices. The sheet itself no longer lists the queue (decided 2026-11-02). Long filter names fit whole (the sheet's filter column widens to the longest name).

The shutter starts the plan; while imaging it shows pause bars with a blue ring filling as each frame exposes, and a tap pauses after the frame in hand ("Pausing…" on the square until it lands); paused, a tap resumes and re-centres first, and the panel offers Resume without re-centering and Re-center and refocus; after a panic stop paused the run, the panel reads "Mount stopped" in amber; a 2 s hold, imaging or paused, stops the run (a red ring fills round the shutter, "Hold to stop" beside it; letting go early cancels). Up to 0.8 s counts as a tap (gloves, cold hands); a longer press short of 2 s cancels. Screen readers get "Stop the run" as an action. Keyboards reach the shutter and the plan squares with Tab, Enter or Space taps them, and ⌘. / Ctrl+. asks first ("Press ⌘. again to stop the run") and stops on a second press within 3 s. When the daemon refuses for want of a storage disk, its words show as a pill, and the sheet keeps Open Storage, which opens the full UI's Storage page. The camera pill and Info read as in Preview. When the queue finishes, Night done takes the same centred sheet: the start and end times, how long it ran and how it ended ("stopped at dawn" when a queue with a dawn stop completed short of its frames within a sub of dawn: the daemon starts no frame that would end after dawn), the step it stopped at in red, frames per filter for each target (the library's count for a single target, the queue-order estimate for several), then Image Library and New plan (integration, best HFR, issues and flats come later).

**Errors.** While the person works a mode, errors (solve, autofocus, slew) show inside that mode's panel with the cause, the fix and Retry. During an unattended run they use the centred attention banner. The Queue is the exception (decided 2026-11-04): a failure after a rig round-trip (add, edit, reorder, start, pause, stop) shows as one message pill, because the sheet that would show it may be closed (Night Setup's last Done, a Stop from the shutter); only a refusal the sheet can give before asking the rig (already queued, run ending) stays inline, and the no-storage case keeps Open Storage in the sheet under the pill.

**No rig connected (not built yet; ALP-4858).** Planned: the planetarium of tonight's sky fills the image area with a centred card: rigs found on the network with Connect, and Add a rig by address; device dots go grey and the mode name dims. Today Simple Mode shows the stars-only wallpaper with no card (Sky shows tonight's planetarium), and Emergency Stop stays enabled and answers a press with "No server connected — nothing to stop."

**Always on, in both modes:** Emergency Stop (except in the immersive view), the attention banner, the device status dots, Night mode and the Simple ↔ full UI switch.

Targets are at least 44 px tall (the settings segments too), with 14 px body text. Text follows the user's size (WCAG 1.4.4), checked from 1x to 2x on iPad, in English and Brazilian Portuguese, by the text-size test, which also fails on a label cut short: the rails keep 72 px up to 1.1x and then widen with the text (131 px at 2x), their buttons, the mode tiles, Stop, the plan squares, the Focuser window's knobs and the Rotator window's dial growing with them; above 1.3x Info's and System's tiles become one panel, a label and value per line, since 72 px tiles would squeeze the text back down (decided 2026-11-03); panels, the sheet and messages follow it too. Colours and status dots behave exactly as in the full UI. Phone Companion Mode uses the same image-first idea in a portrait arrangement. The rendered reference is `design/preview/ara-design-preview.html`.
- **Settings › Rig** (decided 2026-11-09, #1659, #1660; mock `design/preview/simple-logs-storage-fan-mock.html`): Storage and Logs & bug report sit inline in the section, with no window. Storage: "Frames are saved to", the disk's name in 20 px semibold, an 8 px free-space bar (red on a problem), "X free of Y", a red line when saving can't work, then a full-width 44 px Choose disk above Eject and Check disk side by side. With no rig the status says so and all three are disabled. Open Storage in the Plan panel's run error opens Settings on Rig with Storage in view. Logs hosts the full UI's bug-report and client-log cards; below 480 px each card's action moves under its text.
- **Settings › Devices** (#1710): a device row that isn't live and a switch row show a 44 px "Choose…" after Connect or Reconnect, and a last "Add a device" row picks a type with nothing set up. Both open the full UI's device picker.
- **A paused Queue and Guiding** (#1725): a Queue that has actually paused doesn't grey the Guide shutter or hold Night Setup's guide step. A frame in progress, Preview's loop, and a running or aborting Queue still do, with the same message.
- **Plan on a rig that can't change filters** (#1737): the sheet has one row with no filter name.

### Ara Mark and Sheet (Simple Mode, signature)
- **Mark:** the app icon's constellation without its tile, drawn in code (`AraMark`), 30 px at the left end of the status strip as a 48 px tap target with the tooltip "OpenAstro Ara". At small sizes the lines stay at least 2 px, the rings 1.5 px wide and 3 px across, and the bright star 6 px. A 28 px Hairline (4 px before it, 12 px after) sets it apart from the device dots.
- **Sheet:** a tap opens it under the mark (16 px below the strip, at the left inset), and a tap again or on the background closes it. A Simple panel (16 px padding) holds a `SimpleVersions` table titled "OpenAstro Ara": This app, Ara server, Guider when known, Rig (its name) and Profile. Names are in Dim Text, values in Starlight Text. It is as wide as its text, from 280 to 420 px; past that, values wrap. It has no Settings row: the gear is Settings.

### No-Frame Wallpaper (Simple Mode)
- **Look:** the Ara Navy gradient and three layers of white stars from fixed seeds: far ones many and faint (260, 0.5 px), near ones few and bright (28, 1.6 px). No mark on it (dropped 2026-11-06). The "Tap the shutter to take a frame" hint stays centred over it.
- **Motion:** on iPad and iPhone the layers shift 6, 14 and 26 px at full tilt (about 20°), eased over 150 ms, on the screen's axes in either orientation. The centre drifts back to how the device is held, and a dead band of about 1 px ignores sensor noise. With Reduce Motion (iOS) or Remove animations (Android), or on desktop, it is still and the sensor isn't read.

### Sky Album Card
A trading card (2.5:3.5) in the binder grid, 130 px wide. The front is the object's Art filling the picture on Art Backdrop, in colour once collected and grey at 45 % while missing (one colour matrix, one layer), or the Placeholder silhouette when it has no Art; under it the catalog label and the name in the dense ramp. The border is the tier colour (2 px; 1 px Hairline while missing) and a gold Card alone glows. Tapped, it flips in place and grows to at most 320 px wide: a tier-coloured banner (On Tier text), the bio line, the latest four Nights and a totals row, "Did you know?", the action and a 44 px credit row, all in 14 px `kSimpleText`. Front and back share the 10 px corner. Under Reduce Motion the flip is instant.
- **The reveal** (decided 2026-11-07): a Card collected or moved up a tier since the album was last opened sits on its own shelf above the grid for that open, titled like the Tonight shelf ("2 new cards · 1 tier up since your last visit"; on the very first open just "n new cards"), so a Card far down the grid is still seen. There it plays once as the open's fetch lands: it holds grey for about 630 ms, so the page has finished coming in, then over about 1.2 s (ease-in-out) its colour Art fades in over the grey, the border turns from the old tier's colour (or the Hairline) to the new one, and a gold Card's glow arrives last. The grid copy stays plain; the shelf hides under Missing. It plays again only when new rows arrive, so it cannot wear thin. Under Reduce Motion the Card is simply in its colours; the shelf still shows. Tapping a Card mid-reveal ends the reveal, so the flip never shows colour the shelf hasn't reached yet. On the shelf a screen reader hears "new" or "up a tier" after the tier, since only the title says it on screen. The colour Art fades by the Image's own opacity, not an Opacity layer.
- **Tier in words** (decided 2026-11-09, #1637): a collected Card writes its Tier, never colour alone. The front has the tier name as a tier-coloured caption at the right of the label row, and the back's banner has the set number and the tier name in `on-tier`. The back's action is 44 px or more on every platform.
- **Shelves** (the new-cards and Tonight rows): one horizontal row of 130 px Cards at a 10 px gap. A plain mouse wheel scrolls the page, so a mouse drags a shelf sideways, as touch and trackpad do.

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
- **Do** meet WCAG 2.2 AA on every new or changed control (playbook §53): 4.5:1 text, keyboard reach with visible focus, a pointer-free path for every hold or drag. Night mode is exempt from contrast; the default theme isn't.
- **Do** write labels in product voice: verbs, sentence case, no NINA or daemon enum names.

### Don't:
- **Don't** change the full UI's layout while Simple Mode is being built. Simple Mode is additive: new work goes into Simple Mode, and the full UI (PR #1474's shell) stays as it is for now. Two exceptions are approved: the heavier Emergency Stop (ALP-4812), which applies to the full UI too, and a Simple Mode icon in the full UI's top-right cluster, between Night mode and the gear (decided 2026-11-01).
- **Don't** use colour for decoration, branding flourishes or emphasis that isn't a state. The one exception is the brand palette (`AraColors.brand*`, the app icon's navy and line blues): only the OpenAstro Ara mark and Simple Mode's no-frame wallpaper use it (approved 2026-11-05, #1602).
- **Don't** add shadows or glows to separate surfaces; use a tonal step and a Hairline.
- **Don't** let any state fall back to plain grey text without a designed look.
- **Don't** fill any control other than Emergency Stop and confirmed destructive actions with Fault Red.
- **Don't** copy NINA's icons, splash, imagery or wordmark (see PRODUCT.md).
- **Don't** hard-code colours outside `lib/theme/`. A few widgets still do, and they should migrate to tokens.
