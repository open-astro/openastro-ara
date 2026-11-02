# Simple Mode Settings: design brief

Shaped 2026-11-03 (`/impeccable shape`). Mock: [`design/preview/simple-settings-mock.html`](../preview/simple-settings-mock.html) (sheet B, one window per device). Visual rules live in `DESIGN.md` ("Simple Mode: Image First"); the parent brief is [`simple-mode.md`](simple-mode.md).

## Rule: 4×90%, KISS, automation, self-recovery

Simple Mode covers the top 90 % most used features, 90 % of the time, 90 % faster (the user's "4×90%"), and only the bare minimum for a successful imaging night (KISS). Less is more: Simple Mode uses the profile's defaults and decides for the user ("think for me"); automation and self-recovery do the work, so a screen says what is happening and what Ara will do rather than offering a knob. When in doubt, leave it out. A value another Simple Mode control already sets is not repeated (gain is on Preview's chip, the guiding graph on the bottom row). Everything else stays in the Advanced UI, and Simple Mode never links to it: Settings' Simple Mode switch is the way out.

## Job and audience

- **Who:** the Simple Mode imager, at the scope or on the couch, at night.
- **Job:** change the few things a typical night touches, fast, without the Advanced form.
- **Kind of screen:** Operate: scanability and the real usage scene outrank expression.

## Direction (chosen: B)

- **Shape:** the gear opens a sheet over the dimmed image. Sections on the left (Gear with its devices listed under it, Site, Storage, End of night, Alerts, This rig), the section or device window on the right. A tap outside or Close returns to the image.
- **One window per device, one piece of code:** each device has its own window (Camera, Mount, Focuser, Filter wheel, Guider). The same widget is the right pane of Settings and opens on its own, under its dot, when the device's dot in the top bar is tapped (the image stays visible). Each window's header holds the device's status dot, name and Connect / Disconnect.
- **Values are shared** with the Advanced UI: Simple Mode only hides settings, so switching modes resets nothing.

## Content (bare minimum)

| Place | Holds | Stays in the Advanced UI |
|---|---|---|
| Camera | Connect, cooler on/off, target, current temperature | Gain (Preview's chip), warm-up (End of night), electronics |
| Mount | Connect, tracking, park, meridian flip on/off | Flip timing, refocus after a flip, pointing readout, limits |
| Focuser | Connect, position, four move buttons, refocus when it cools by N °C | Frame-count and filter-change triggers, backlash |
| Filter wheel | Connect, filter in place, tap a filter to move | Renaming filters, offsets |
| Guider | Connect, RMS, dither every N frames | Graph (bottom row), settle, tuning, calibration |
| Site | Location (this device's GPS or typed), time zone | Clock check, horizon, Bortle/SQM |
| Storage | Which drive, free space | Copy to another disk, file names |
| End of night | Stop at dawn / at a time / never, park, warm the camera | Flats, safety policies |
| Alerts | Safety alarm sound, silent delay | Where alerts go (in-app only today, §46) |
| This rig | System figures, Change profile / Change rig, permanent pier, Simple Mode switch, version, logs & bug report | Profile editing, wizard, sky data |

Gear lists only the devices the profile has; flat panel, dome, weather and safety monitor show Connect only.

## States

- A device not in the profile: not listed. Disconnected: grey dot and Connect. Connecting: amber dot and Cancel. Fault: red dot and the reason in the window.
- No rig link: every window reads its last values greyed with "Rig not connected", controls disabled.
- A running plan: changes apply to the next frame or step; the This rig section keeps the "plan keeps running" line by Change profile / Change rig.
- Text up to 3.1x and Night mode (WCAG 2.2 AA bar; night mode exempt from contrast).

## Open before building

- Check each setting against what the daemon exposes and name any that need daemon work before the window that uses it is built.
- Build order: the sheet and This rig (replacing today's menu), then one device window at a time, test-first.
