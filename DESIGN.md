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
Material segmented buttons and choice chips. Selected segments use Selection Blue with white text, and outlines use Hairline. On the panels over the image (Simple Mode's device windows, the rail's popovers) an on/off is a pair of segments that name their states ("Cooler On" / "Off"), never a bare toggle, so it reads at night over any frame. In a list of settings (Settings › Rig, the full UI's panels) it is the platform's switch (`Switch.adaptive`: the iOS switch on iPad and iPhone, Material elsewhere), with its label read together with it (decided 2026-11-03).

### Simple Mode: Image First (two rails)
Simple Mode (see PRODUCT.md) puts the latest frame first. It is simpler than ASIAir, not a copy of it. The image fills the window edge to edge, and the controls float over it on translucent panels. The layout below was approved on 2026-11-01 and is built on master; it sits behind Settings → System → Experimental → Simple Mode (off by default, per client), and the system status bar is hidden while it shows.
- **Panels:** every panel uses `overlay-scrim` (Night Floor at 90 %, so panel text reads at 4.5:1 or better over a dark sky, a grey frame or a white flat; raised from 72 % on 2026-11-02) over a 5 px blur of the image behind it (`AraBlur.panel` in code, `panel-simple`'s blur in `.impeccable/design.json`; one shared blur for all panels (`BackdropGroup`, measured on the iPad 6 on 2026-11-05: one filter per panel cost ~30 ms a frame against ~6 ms shared and ~4 ms with none): the Settings sheet's and the device windows' look, on every panel since 2026-11-04 for legibility; the strip, rails, status pill, message pills, Info tiles, the Night Setup strip, the plan squares and every popover), 10 px corners and no border, and sits 16 px from the frame edge and from its neighbours. Error words on a panel use `error-text` (#FF8A80), Fault Red lightened for the scrim; Fault Red itself stays for fills and the E-stop. Text-button labels on a panel use `link-text` (#64B5F6, Info Blue lightened: 4.5:1 on a tile too) and outlined-button labels white; the button primary (#1565C0) is for fills only, since as text it reads at 3:1 on a panel and 2:1 on a tile. A selected or current item on a panel uses `selection-tint`, with its words and icon in `link-text` (Info Blue reads 4.3:1 on the tint over a white frame).
- **Fixed slots:** nothing moves when state changes. A panel with nothing to show is not drawn, and the panels around it keep their places.
- **Top:** a full-width status strip, 56 px tall: the OpenAstro Ara mark (the app icon's constellation without its tile, decided 2026-11-05, #1602), which opens a small **OpenAstro Ara** sheet under it (This app, Ara server and Guider versions, the rig's name and the selected Profile; no Settings row, the gear is Settings; as wide as its text up to 420 px, then values wrap), set apart by a 28 px Hairline so it doesn't read as one more device (2026-11-06), then the device dots (connected gear only; a fault still shows; with none, a note that opens Settings › Devices: "Rig not connected" in amber, 2026-11-06, or "No devices connected"), then Search, Night mode and Settings. Search (⌘K too) is Simple Mode's own: it finds sky objects (the sky points at the hit, with Image this), Simple Settings rows (the sheet opens on their section), the modes and imaged targets (the Image Library, filtered); nothing it finds leads to the full UI (decided 2026-11-03). Nothing hides under a ⋯ menu, and nothing on it leads to the full UI. Settings (⌘, too) opens Simple Mode's own Settings, a sheet over the image under the gear, at most 640 pt wide (growing with the text; window-wide until 2026-11-04), its Rig values all one width (160 pt): **Rig** (the rig's figures, name and profile with Change, telescope, site, Permanent pier, the Simple Mode switch, language, versions, logs) and **Devices** (the profile's devices with Connect / Disconnect / Reconnect, each opening its device window).
- **Camera status:** a pill at the top-right, 16 px under the strip and at most 300 px wide (half the row on a narrow window), like a camera's REC light: "Taking a frame · 3 s left" with a thin bar, Looping, Stopping…, Frame ready, or why it failed. A running plan's step is its own pill under it (see Status pills below), not this one between frames: "Plan · Cooling camera to -10 °C", a blue ring while Ara paused it, amber when it waits for you, with a thin line along the pill's foot showing how far the step has got when it reports that (cooling, a wait, autofocus); a scheduled plan says when it starts ("Plan · Starts at 21:47") (decided 2026-11-02; stacked pills 2026-11-04). Nothing while idle. It stays in the immersive view and moves up into the corner.
- **Status pills (top right, decided 2026-11-04):** one pill per thing happening, stacked under the strip in this order: the main camera's shot or loop, a running plan's step, a park, manual cooling, a Focus routine (autofocus, the Bahtinov readout, guide-camera focus), polar alignment, the guider. Every mode has a pill while it runs; a failed routine says why as a message on the left instead. Three at most, the rest fold into "+n". Each follows the status rule (colour, blink while working), keeps its own steady width, and a tap opens what it is about (the mode's panel, or the Mount or Main Camera window).
- **Messages (no popups, ALP-4825):** results, errors and questions show as pills stacked in the top-left corner, beside the status pill and never under it (they share the row, up to 420 px), under the Night Setup strip when it shows, as many as fit above the left rail; the rest fold into "+N more" (at the end of the newest pill when there is no room below). A pill shows its whole message, however many lines (an Emergency Stop's summary was cut at two, 2026-11-04). A result has a green dot and fades after 10 s (6 s until 2026-11-04; a line along its foot shows the time left); an Emergency Stop's summary (the stop also ends a guider that is looping or on its star, the guide camera's live loop included, as a safety reaction does) is amber when it went through (done, but the rig needs a look) and red when a step failed, and stays until closed with ×, with no time-left line, as does the red error for a stop that never reached the daemon (ALP-5016; both are sticky: the only errors that don't fade); an error has a red outline and light-red words and fades after 20 s on screen, or on × (not a sticky pill, 2026-11-04); a routine that fails (autofocus, polar alignment) says so this way, with why; a question has an amber outline and its buttons and stays until answered. Each is announced to screen readers. They stay in the immersive view. In the full UI the same messages are its snackbars. The one exception is the safety alarm (safety monitor or weather unsafe): it stays a full-screen warning over everything, the immersive view included: on `alarm-scrim` (a deep red veil over the whole window), a large red circled "!" (Material `error`; not a bell, which reads as a notification, and not the octagon, which is Emergency Stop's), "SAFETY ALERT", the reason, what the rig did, and a large red **Silence alarm**. It shows at once and the tone follows after the device's silent delay (5 s by default), so someone at the screen can silence it before it rings; Silence only stops the sound, and once silenced the screen closes and the status pill keeps the unsafe reason in red until the daemon reports safe (decided 2026-11-02).
- **Right rail (72 px wide, only as tall as its controls, centred between the strip and Stop), from the top:**
  - the mode name. Polar align shows as "P.A." and Night Setup as "Setup" here, since the full names don't fit 60 px; the grid and screen readers keep the full names. A tap opens the six modes as a grid beside the rail: Night Setup, Preview, Focus, Polar align, Guide, Plan;
  - the shutter (60 px), always in a white ring. Only Preview's is a camera button, a filled white circle: Single takes a saved frame, as Take One does today, and Loop runs live view, whose frames are not saved. Every other mode's shows a white play triangle and starts that mode's routine (Focus, Auto or Manual; Polar align; Plan; Night Setup follows its step), and Plan's ring turns amber only while paused (decided 2026-11-03). While it works it shows a square that cancels;
  - the settings chip, which speaks for the current mode. In Preview it reads "5 s · 100" and its popover holds Exposure (a preset list trimmed to the camera's limits), the Gain slider under it (the camera's gain, the same slider and value as the Camera window; decided 2026-11-03), Filter, and Loop / Single; there is no Save frame button. In another mode it reads that mode's summary (Focus: "Auto" or "Manual", the method Smart Focus would use; Polar align: "1 s" over "Loop"; Guide: the RMS while it guides, "Off" otherwise; Plan: the total imaging time, "2 h" over "45 min") and shows or hides that mode's panel. The chip is 72 px tall in every mode, the settings icon over up to two lines, so the rail is the same height whatever the mode (decided 2026-11-02).
- **Popovers** (the mode grid, settings, and a mode's panel) open to the left of the right rail, level with its top, 16 px from it. Opening one never moves the rail. A tap on the image closes an open popover.
- **Stop:** the compact Emergency Stop, a 60 px square reading STOP, on its own at the bottom right under the rail. It fires after a 2 s hold and reads HOLD while held; keyboard and screen reader users press it, then press again within 3 s (it reads PRESS AGAIN, no dialog).
- **Left rail (72 px wide, centred between the strip and the bottom):** Sky (the planetarium at any time, over a frame; an object tapped there shows a card under it with its name, type and altitude and Image this, which opens Plan's sheet with it), Info (toggles the last frame's facts) and Library (the Image Library screen). Each is an icon over a caption.
- **Info:** a row of fact tiles (72 × 56, label over value, on `overlay-scrim` with a 1 px Hairline border) hugging the bottom-left corner, just above the bottom row, HFR nearest the corner, then Stars, Exposure · filter, Gain · offset, Clipped. The bottom-right corner belongs to the plan squares, the top-left to the message pills.
- **System:** a row of the same tiles, in the bottom-left corner alone or 8 px above Info's row while Info shows: Storage (free on the save drive, or Unplugged), CPU, Memory and Up, re-read every 5 s while it shows. CPU and Memory are Ara's own process until the server reports the whole Pi. CPU temperature, under-voltage or throttling and disk I/O are next; they need the server to report them.
- **Bottom row (56 px tall, growing with the text above 1.1x like the rails; 16 px from the left edge and 16 px from Stop):** the guiding graph ("Guiding", the RMS, the RA/Dec trace) and the frame's histogram (plotted over the range the frame uses), each only once it has data. A lone one fills the row; both split it equally, guiding on the left.
- **Centre:** the attention banner, centred 16 px under the status strip, only when something needs the person (the daemon's prompts become question pills in phase B).
- **Immersive view:** a tap on the image, with no popover open, fades every panel out, Emergency Stop included (220 ms ease-out, instant with reduced motion), so the frame shows fullscreen; the camera status pill and any attention banner stay. A tap brings the panels back. There is no keyboard shortcut for now.
- **No frame yet:** the wallpaper (decided 2026-11-05, #1602, `design/preview/simple-branding-mock.html` E on branch `prototype/simple-branding`; stars only, the constellation dropped 2026-11-06): the brand navy and three layers of stars. On an iPad or iPhone the layers shift by depth as the device tilts, on the screen's axes in either orientation (accelerometer; about 20° is full tilt, eased, with a dead band so a rig on a stand doesn't redraw, and the centre drifts back to how it is held). They stay still, and the sensor isn't read, with iOS Reduce Motion or Android's Remove animations, and on desktop. Sky shows tonight's planetarium over it; the first frame closes the Sky. "Tap the shutter to take a frame" stays centred. Until 2026-11-05 tonight's planetarium (sky only, no in-page controls) filled the window here.
- **Leaving:** the frame icon in the full UI's top bar enters Simple Mode. Simple Mode's Settings (the gear, ⌘,), its search (⌘K) and the device windows (the Main Camera, Mount, Focuser and Guide Camera dots; Camera and Guider were renamed on 2026-11-04, and the Guide Camera window keeps only Gain and, in Settings, its versions: State, RMS and dither moved to the Guider mode) stay inside it. Not yet: Settings › Rig › Logs, Open Storage and the dot of a device with no window of its own (Filter Wheel, Rotator, Flat Panel, Switch and the rest) still open the full Settings page over Simple Mode, closed with Back. The way out is the Simple Mode switch in Settings › Rig.

**Simple Mode's rule: 4×90% and KISS.** Simple Mode covers the top 90 % most used features, for 90 % of the people, 90 % of the time, 90 % faster, with only the bare minimum for a successful imaging night: defaults and automation first, self-recovery doing the work, and a small set of controls where the user needs them (ASIAir as the reference); a value one Simple Mode control sets is not repeated elsewhere, and nothing links to the full UI. No redundant design: each thing lives in one place, and interactions are contextual (tap the thing to act on it). Its Settings (a sheet over the image with a few controls, ASIAir as the reference; one window per device, the same one its top-bar dot opens; Connect / Disconnect on Settings › Devices only) are shaped in `design/briefs/simple-mode-settings.md` (2026-11-03).

**Modes.** The mode grid reads Night Setup · Preview · Focus · Polar align · Guider · Plan (Plan was named Run until 2026-11-02; Guider added 2026-11-04 as Guide, renamed 2026-11-06). Targets are picked in Plan's sheet or in Sky; there is no Target mode (decided 2026-11-02), so Go to, Solve and Sync without a run stay in the full UI. Each mode other than Preview opens its own panel to the left of the right rail when it is picked (a floating card like the settings popover, pinned beside the rail and level with its top), and the settings chip shows or hides it; where needed the mode also draws on the image. The rails, the strip and Stop never move. All six modes are built.
- **Preview:** the right rail itself: shutter, settings chip, Loop or Single. The last frame's facts come from Info.
- **Polar align:** the rail reads "P.A." and the chip shows the routine's setup ("45 s" over "Single"). The shutter starts the routine, and while it runs its red square stops it, in Loop and Single alike. While the routine runs, the image area shows the polar-align camera's latest frame. Nothing is drawn on the frame: the server reports the altitude and azimuth errors, not where the pole sits in the picture. The mode panel holds Exposure and Loop / Single (the profile's polar-align settings, applied to the next frame). While the routine runs it also shows the phase, the total error with its zone dot (green under 10′, amber under 60′, red beyond, matching the server's thresholds), the phase with the total error beside it, then the full UI's bullseye (180 pt) with the altitude and azimuth turns beside it as tiles, a 44 pt arrow over the axis and the move ("Lower 3.2′", "Move west 14.1′"), the panel 420 pt wide with Loop / Single and Exposure side by side so it all fits beside the rail on an iPad (decided 2026-11-03; before it starts, and at large text, they take a row each: user, 2026-11-04), failed solves, then Take frame (Single only; Loop shoots on its own) and a green Done (once inside the profile's tolerance). The panel has no Start or Stop: the shutter does both. When one axis got worse after a turn while the other held, an amber line says "Altitude got worse. Turn that bolt back past where you started." (the full UI's bolt-direction hint, cut to two lines).
- **Guider** (decided 2026-11-04 as Guide, `design/preview/simple-guide-mode-mock.html`; renamed 2026-11-06 because the mode holds the guider's settings): the rail reads "Guider" (pt-BR "Guia", the app's word for the guider; icon: a target) and the chip shows the RMS while it guides, else "Off". The shutter starts the guider's whole routine: loop, pick a star, calibrate when needed, settle, guide; its red square stops it. The panel (340 pt, left of the rail) says what the guider is doing (Idle, Calibrating · leg, Guiding; "Not guiding" read as a failure, 2026-11-04), how to start when idle, the RMS in arcseconds big with RA and Dec under it, the guide camera's Exposure (a tile like Preview's) and Gain (the Guide Camera window's slider, one value), both sent to the guider live, and Dither (every N frames, Off) and Dither by (px), which the plans use. A tap says **Starting…** or **Stopping…** at once (the guider takes seconds; a stop waits out its frame). Its dot, the status pill's and the top bar's share one colour rule (a lost star amber, settling blue). In Night Setup, once guiding, it shows **Done · Start imaging**. The bottom row's graph reads **Guider** and shows only while the guider runs (decided 2026-11-04).
- **Focus:** a zoomed star with its HFR ring sits top-left under the status pill. The mode panel (400 pt wide, as built; Polar align's is 420) shows the headline, the focuser's position, temperature and model, the HFR and the focus curve as it builds; a finished run adds best position, stars and fit R² as tiles and, when the fit was weak, the full UI's weak-fit warning, all on view (nothing folds away). Measured stars On / Off (off to start, per session) fills the screen behind the panels with the run's own frame, its measured stars circled (decided 2026-11-03, `design/preview/simple-focus-results-mock.html` layout A); the focuser is moved with the Focuser window's dial, so the panel has no position or nudge buttons (decided 2026-11-03). The panel opens with Main / Guide (which camera) and, for the main telescope, Auto / Manual on a row under it (locked while anything runs; one line read as four of a kind, user 2026-11-04); a finished run's headline shares its line with Measured stars On / Off, so the panel still fits an iPad. Guide focuses the guide camera by hand, as the full UI's Guide camera tab: what to turn (guide scope or off-axis guider), the blockers in words (connect the guider; stop guiding first; an off-axis guider waits for the main telescope, with Already in focus), the live HFR headline, Stars and Frames, the turn advice and the exposure (1 / 2 / 3 / 5 s); the shutter starts and stops it and its frames fill the screen; the chip reads Guide (decided 2026-11-03, `design/preview/simple-guide-focus-mock.html` A). The shutter follows it: on Auto it starts autofocus and its red square cancels it; on Manual it starts the Bahtinov readout (1 / 2 / 4 / 8 s frames, 2 s to start) and its square stops it. Manual's panel is the full UI's Bahtinov helper, flat: the crop with its spikes, the offset in px from focus in big type, the turn advice in words (Make a move, Keep going, Go back, In focus) and Finish, which asks for the mask to come off inside the panel (Not yet / Mask removed); leaving Manual before Finish asks the same (decided 2026-11-03, ALP-4874).
- **Night Setup** (named Autopilot until 2026-11-03 and Guided Run until 2026-11-05; Plan first since 2026-11-03, `design/preview/simple-night-setup-mock.html` B): the night's steps in order, Plan → Focus → Polar align → Guide (focus before polar alignment: it plate-solves and fails on a soft focus; Guide added 2026-11-04), each on its usual panel, with a step strip centred under the status strip (the steps behind the current one carry a green tick when done tonight, Plan once a queue is in, or an amber skip mark when skipped; the current one is lit; Back and Skip). **Plan** is the plan sheet (beside the rail, as in Plan) with no title (the strip names the step; decided 2026-11-04), that only queues: "Imaging starts after focus and polar alignment." over Add to queue and a green **Next: Focus** (Next: Polar align, or Start imaging when both are done tonight), enabled once one target is queued. A finished autofocus moves on to Polar align, which starts its routine on entry (an exception to the shutter starting things; its red square still stops it); Guide does the same with the guider's routine, and leaving it keeps guiding for imaging. Polar align's Done reads **Next: Guide** when Guide follows. The last setup step's green Done reads **Done · Start imaging** and starts the queue (at astronomical dark when it is before dark, as the sheet's own Start at would); on a permanent pier, or already aligned, and when Guide is skipped too (already guiding, or no guider), Focus is the last step and shows that Done once it has focused, so imaging never starts on its own. Starting imaging ends Night Setup and leaves you in Plan. The rail reads "Setup"; it starts from the mode grid (Preview's "Start Night Setup" offer was removed, user 2026-11-04). The chip names the step whose settings it shows ("Plan" over "2 h", "Focus" over "Auto", "P.A." over "45 s"). Steps already done tonight are skipped (focus: an autofocus finished this session; polar align: inside tolerance this session), and a permanent pier skips Polar align; Guide is skipped while already guiding or with no guider connected (an unguided rig images without it). Outside Night Setup every mode stands alone: nothing opens another.
- **Plan:** the quick plan builder is a sheet beside the right rail like every mode's panel (centred over the image until 2026-11-04), between the status pill and the bottom row (it scrolls inside when taller), with the rails still visible. On a wide window tonight's best sits in a 240 px column to the left of the plan; on a narrow one it stacks on top. On top, tonight's best: up to four tappable targets from the Tonight's Sky ranking (name, then type and altitude or when it rises; hidden with no site), then the target: a search (the planetarium's own, so the sky shows behind the sheet while it searches) with the picked object's name and "62° up · max 74°" or why it can't be reached, and Pick in Sky, which closes the sheet for the sky (its card's Image this brings the sheet back). Then the filters as chips (a picked one in the segments' solid selection blue with white words, decided 2026-11-04) and a row per filter with exposure and frame count, the total frames and time; the frames split the picked target's dark hours left tonight across the rows (60 frames split without a window), a new target refits them and a new sub length refits its row; autofocus, dither and flats follow the profile. Tapping a value opens the same preset list as the Preview settings. Rows start at the profile's default exposure; one line under them names the suggested sub lengths that differ ("Suggested: L 2 min · GB 3 min", the plan chooser's sky-based numbers) with Use all, and each exposure list marks its suggestion. Without tonight's polar alignment (and no permanent pier) an amber line reads "Not polar aligned tonight"; it warns and never blocks. The sheet ends with two buttons. **Add to queue** (solid `button-primary` with white words, like the queue panel's Add target; outlined until 2026-11-04) adds the target to the end of the queue; **Create and start** (green) creates the plan and starts it, and once a queue exists it reads **Start now**: the target goes first and imaging starts (a second exception to the shutter starting things). A target below the horizon can be queued but not started; one that never rises here can't be queued. While imaging, the sheet only adds. Add to queue keeps the sheet up for the next pick: a green line says "M42 added · 3 in the queue" and the pick clears; Create and start / Start now close it (decided 2026-11-02). Before dark the green button names when the plan starts, **Start at 21:47** (astronomical dark from the site's twilight setting), with a line under it: "Starts at dark (21:47) · Start now instead · Pick a time"; once dark it reads Create and start with "Starts now · Pick a time". Pick a time opens a time wheel inside the sheet (5-minute steps; a time earlier than now means tomorrow). The plan starts on the rig at once and waits there: until 15 minutes before the start, then it cools the camera, then until the start (decided 2026-11-02). Every plan parks the mount after its last target, before the camera warms up; a plan that starts in the dark also stops at dawn (the site's twilight setting), and a dim line under the start line says when: "Stops at dawn · 05:12". A target not started by dawn is skipped, no frame starts that would end after dawn, and anything still running at dawn is interrupted (the daemon's TimeCondition checks before each step and watches the clock every second). Start now in daylight parks but has no dawn stop, so an indoor test runs on. Once a queue is on the rig, the line follows the queue's own dawn stop, which every target added to it keeps, those added from the full UI's Planning too (#1560). The rail ▶ also creates and starts when a target is picked and nothing is queued, and starts a queued plan.

The queue is one sequence on the rig: each target is a block in it, and adding while it images joins the running sequence (decided 2026-11-02). Plan squares (72 × 100) hug the bottom-right corner, just above the bottom row and below the right rail, and line up right to left in queue order, the first in the corner (moved from the top right on 2026-11-02 to clear the top for the message pills). They take the right half of the window (the Info tiles have the left); past what fits, the target in hand and those after it show, then one "+N" square with the rest's total time (finished targets drop out first). While Plan's sheet shows, the squares step aside so the sheet keeps its full room down to the bottom row (decided 2026-11-02; it used to end above them, and two queued targets made it scroll). Each shows the target's catalog tag (a curated region's own, "IC 434"; else its best designation, M before NGC, IC, Sh2, B and LBN; else its name, shrunk to fit), frames done of its total ("0 / 60", "23 / 60"), the filter and sub length in hand ("R · 3 min"), and its time: the total before it starts, the time left while imaging, "rises 22:40" while a queued target is still below the horizon (the shutter waits for the first target's rise), "Done" once finished. The queue's last square (the leftmost, or the "+N" square) adds a fifth line, "Parks" (pt-BR "Estaciona"): the mount parks once the queue ends; it goes once the queue is done. One short word and no icon, because the line has 60 px and anything longer shrinks below the other lines' 11.5 px (measured in Roboto, #1560). The square in hand is green when ready, blue with a progress line along its foot while imaging, amber "Paused"; the others are green. Tapping any square opens the queue panel beside the rail (the chip opens it too while imaging): "QUEUE · 3 targets · 6 h", then each target with its tag, frames of its total and time left. A tap on a target that hasn't started edits it: the sheet opens on its rows only ("Edit M42", the target stays; Cancel / Save), and Save replaces its block on the rig and puts it back in place, live while imaging. × deletes a target that hasn't started. Add target opens the plan sheet. While imaging the panel first shows the target in hand (frames per filter, when the queue ends) and the resume choices. The sheet itself no longer lists the queue (decided 2026-11-02). Long filter names fit whole (the sheet's filter column widens to the longest name).

The shutter starts the plan; while imaging it shows pause bars with a blue ring filling as each frame exposes, and a tap pauses after the frame in hand ("Pausing…" on the square until it lands); paused, a tap resumes and re-centres first, and the panel offers Resume without re-centering and Re-center and refocus; after a panic stop paused the run, the panel reads "Mount stopped" in amber; a 2 s hold, imaging or paused, stops the run (a red ring fills round the shutter, "Hold to stop" beside it; letting go early cancels). Up to 0.8 s counts as a tap (gloves, cold hands); a longer press short of 2 s cancels. Screen readers get "Stop the run" as an action. Keyboards reach the shutter and the plan squares with Tab, Enter or Space taps them, and ⌘. / Ctrl+. asks first ("Press ⌘. again to stop the run") and stops on a second press within 3 s. When the daemon refuses for want of a storage disk, its words show as a pill, and the sheet keeps Open Storage, which opens the full UI's Storage page. The camera pill and Info read as in Preview. When the queue finishes, Night done takes the same centred sheet: the start and end times, how long it ran and how it ended ("stopped at dawn" when the dawn stop ended it before its frames ran out), the step it stopped at in red, frames per filter for each target (the library's count for a single target, the queue-order estimate for several), then Image Library and New plan (integration, best HFR, issues and flats come later).

**Errors.** While the person works a mode, errors (solve, autofocus, slew) show inside that mode's panel with the cause, the fix and Retry. During an unattended run they use the centred attention banner. The Queue is the exception (decided 2026-11-04): a failure after a rig round-trip (add, edit, reorder, start, pause, stop) shows as one message pill, because the sheet that would show it may be closed (Night Setup's last Done, a Stop from the shutter); only a refusal the sheet can give before asking the rig (already queued, run ending) stays inline, and the no-storage case keeps Open Storage in the sheet under the pill.

**No rig connected (not built yet; ALP-4858).** Planned: the planetarium of tonight's sky fills the image area with a centred card: rigs found on the network with Connect, and Add a rig by address; device dots go grey and the mode name dims. Today Simple Mode shows the stars-only wallpaper with no card (Sky shows tonight's planetarium), and Emergency Stop stays enabled and answers a press with "No server connected — nothing to stop."

**Always on, in both modes:** Emergency Stop (except in the immersive view), the attention banner, the device status dots, Night mode and the Simple ↔ full UI switch.

Targets are at least 44 px tall (the settings segments too), with 14 px body text. Text follows the user's size (WCAG 1.4.4), checked from 1x to 2x on iPad, in English and Brazilian Portuguese, by the text-size test, which also fails on a label cut short: the rails keep 72 px up to 1.1x and then widen with the text (131 px at 2x), their buttons, the mode tiles, Stop, the plan squares and the Focuser window's dial growing with them; above 1.3x Info's and System's tiles become one panel, a label and value per line, since 72 px tiles would squeeze the text back down (decided 2026-11-03); panels, the sheet and messages follow it too. Colours and status dots behave exactly as in the full UI. Phone Companion Mode uses the same image-first idea in a portrait arrangement. The rendered reference is `design/preview/ara-design-preview.html`.
### Ara Mark and Sheet (Simple Mode, signature)
- **Mark:** the app icon's constellation without its tile, drawn in code (`AraMark`), 30 px at the left end of the status strip as a 48 px tap target with the tooltip "OpenAstro Ara". At small sizes the lines stay at least 2 px, the rings 1.5 px wide and 3 px across, and the bright star 6 px. A 28 px Hairline (4 px before it, 12 px after) sets it apart from the device dots.
- **Sheet:** a tap opens it under the mark (16 px below the strip, at the left inset), and a tap again or on the background closes it. A Simple panel (16 px padding) holds a `SimpleVersions` table titled "OpenAstro Ara": This app, Ara server, Guider when known, Rig (its name) and Profile. Names are in Dim Text, values in Starlight Text. It is as wide as its text, from 280 to 420 px; past that, values wrap. It has no Settings row: the gear is Settings.

### No-Frame Wallpaper (Simple Mode)
- **Look:** the Ara Navy gradient and three layers of white stars from fixed seeds: far ones many and faint (260, 0.5 px), near ones few and bright (28, 1.6 px). No mark on it (dropped 2026-11-06). The "Tap the shutter to take a frame" hint stays centred over it.
- **Motion:** on iPad and iPhone the layers shift 6, 14 and 26 px at full tilt (about 20°), eased over 150 ms, on the screen's axes in either orientation. The centre drifts back to how the device is held, and a dead band of about 1 px ignores sensor noise. With Reduce Motion (iOS) or Remove animations (Android), or on desktop, it is still and the sensor isn't read.

### Sky Album Card
A trading card (2.5:3.5) in the binder grid, 130 px wide. The front is the object's Art filling the picture on Art Backdrop, in colour once collected and grey at 45 % while missing (one colour matrix, one layer), or the Placeholder silhouette when it has no Art; under it the catalog label and the name in the dense ramp. The border is the tier colour (2 px; 1 px Hairline while missing) and a gold Card alone glows. Tapped, it flips in place and grows to at most 320 px wide: a tier-coloured banner (On Tier text), the bio line, the latest four Nights and a totals row, "Did you know?", the action and a 44 px credit row, all in 14 px `kSimpleText`. Front and back share the 10 px corner. Under Reduce Motion the flip is instant.

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
