# Simple Mode Settings: design brief

Shaped 2026-11-03 (`/impeccable shape`). Mock: [`design/preview/simple-settings-mock.html`](../preview/simple-settings-mock.html) (sheet B, one window per device). Visual rules live in `DESIGN.md` ("Simple Mode: Image First"); the parent brief is [`simple-mode.md`](simple-mode.md).

## Rule: 4×90% and KISS

Simple Mode covers the top 90 % most used features, for 90 % of the people, 90 % of the time, 90 % faster (the user's "4×90%"), and only the bare minimum for a successful imaging night (KISS). When in doubt, leave it out. A value another Simple Mode control already sets is not repeated (the guiding graph on the bottom row). Settings that only look alike are different settings and each has its own place: Preview's chip sets the gain for Preview's shots, the Camera window the profile's gain that plans shoot at (user, 2026-11-03). Everything else stays in the Advanced UI, and Simple Mode never links to it: Settings' Simple Mode switch is the way out.

## Job and audience

- **Who:** the Simple Mode imager, at the scope or on the couch, at night.
- **Job:** change the few things a typical night touches, fast, without the Advanced form.
- **Kind of screen:** Operate: scanability and the real usage scene outrank expression.

## Direction (chosen: B)

- **Shape:** the gear opens a sheet over the dimmed image. Sections on the left (Devices, with each device listed under it, Site, Storage, End of night, Alerts, Rig), the section or device window on the right. A tap outside or Close returns to the image.
- **One window per device, one piece of code:** each device has its own window (Camera, Mount, Focuser, Filter wheel, Switches, Guider). The same widget is the right pane of Settings and opens on its own, under its dot, when the device's dot in the top bar is tapped (the image stays visible). Each window's header holds the device's status dot and name. A device's window has no Connect / Disconnect (user, 2026-11-03): Ara connects the profile's gear and reconnects what drops. Settings › Devices keeps Connect / Disconnect per device (user: "leave it in the Settings"), and a device that failed or is stuck shows red there with the reason and **Reconnect** (disconnect plus connect).
- **No page titles:** the selected section in the list names the page; the page doesn't repeat it (user, 2026-11-03).
- **Look:** the sheet uses the 90 % `overlay-scrim` every Simple Mode panel uses, plus a 5 px blur of the image behind (user, 2026-11-03). Its dimmest text (#A0A0A0) is 5.8:1 over a white flat under the veil, AA.
- **Values are shared** with the Advanced UI: Simple Mode only hides settings, so switching modes resets nothing.

## Content (bare minimum)

| Place | Holds | Stays in the Advanced UI |
|---|---|---|
| Camera | Cooler on/off, target, current temperature, gain (the profile's, what plans shoot at; user, 2026-11-03) | Warm-up (End of night), electronics |
| Mount | Tracking (On / Off), Park now / Unpark ("Parked" when parked), meridian flip (Auto / Off, the profile's) | Flip timing, refocus after a flip, pointing readout, limits |
| Focuser | Position, temperature, coarse and fine knobs (a click scales to the travel; #1662), Autofocus, refocus when it cools by N °C | Frame-count and filter-change triggers, backlash |
| Filter wheel | Filter in place, tap a filter to move; a manual wheel asks for the swap and its confirmation (#1662) | Renaming filters, focus offsets, slot labels for sequences |
| Rotator | Sky angle (and mechanical angle when it differs), a sky-angle dial in 1° clicks with Start / Stop in its middle, Sync to the dial's target (#1662) | Reverse |
| Switches | Each connected switch device's ports by name: On / Off, a level slider, or a reading; the camera's cooling fan left out (#1651) | Renaming or grouping ports, automatic dew control, adding switch devices |
| Guider | RMS, dither every N frames | Graph (bottom row), settle, tuning, calibration |
| Site | Location (this device's GPS or typed), time zone | Clock check, horizon, Bortle/SQM |
| Storage | Which drive, free space | Copy to another disk, file names |
| End of night | Stop at dawn / at a time / never, park, warm the camera | Flats, safety policies |
| Alerts | Safety alarm sound, silent delay | Where alerts go (in-app only today, §46) |
| Rig | System figures, Change profile / Change rig, permanent pier, Simple Mode switch, version, logs & bug report | Profile editing, wizard, sky data |

Devices lists only the devices the profile has, each with its state. Every Alpaca device the profile has gets a window of its own (#1662, 2026-11-07, reversing the 2026-11-03 decision that the flat panel, dome, weather and safety monitor have none); the flat panel, dome, weather and safety monitor windows join this brief as each is built. ASIAir's settings stay the reference for how much control to give (user, 2026-11-03: "we need to give SOME control to the user").

## States

- A device not in the profile: not listed. Connecting: amber dot (Devices: Cancel). Failed or stuck: red dot and the reason (Devices: Reconnect). Disconnected: grey dot (Devices: Connect).
- No rig link: every window reads its last values greyed with "Rig not connected", controls disabled.
- A running plan: changes apply to the next frame or step; the Rig section keeps the "plan keeps running" line by Change profile / Change rig.
- Text up to 3.1x and Night mode (WCAG 2.2 AA bar; night mode exempt from contrast).

## Open before building

- Check each setting against what the daemon exposes and name any that need daemon work before the window that uses it is built.
- Build order: the sheet and Rig (replacing today's menu), then one device window at a time, test-first.
