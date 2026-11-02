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

## Content (no knobs)

Each place shows what is happening now and what Ara does for you. The values Ara uses are the profile's (set once in the setup wizard or the Advanced UI); Simple Mode reads them and never asks.

| Place | Shows | Ara decides and recovers | Buttons |
|---|---|---|---|
| Gear | Each device's dot and one live value | Connects the profile's devices on connect, reconnects one that drops | none |
| Camera | Temperature, target | Cools when a plan starts, waits for the target, warms at the end; retries a failed frame, reconnects a dropped camera | Connect |
| Mount | Tracking, side of the meridian | Tracks after Go to, flips and re-centres at the meridian, parks at the end | Connect, Park now |
| Focuser | Position, temperature | Focuses before the first frame, again on a 2 °C drop or a filter change (by hand: Focus mode) | Connect |
| Filter wheel | Filter in place | Follows the plan (by hand: Preview's chip) | Connect |
| Guider | State, last RMS | Guides after centring, dithers; on a lost star pauses, finds a star (plate solving if it must) and resumes | Connect |
| Site | Place, coordinates, time zone | Takes it from this device's GPS on connect | Type it, only without GPS |
| Storage | Free space, drive | Saves to the rig's drive, warns before it fills, refuses to start with none | none |
| End of night | Tonight's dawn | Stops at dawn, parks, warms the camera | none |
| Alerts | none | Parks when unsafe, alarm after 5 s unless silenced, resumes when safe | none |
| This rig | System figures, rig, profile, version | none | Change profile / rig, Permanent pier, Simple Mode switch, Logs |

Each "Ara decides" line is a claim to check against the daemon before it ships; one Ara doesn't do yet becomes daemon work, never a knob.

## States

- A device not in the profile: not listed. Disconnected: grey dot and Connect. Connecting: amber dot and Cancel. Fault: red dot and the reason in the window.
- No rig link: every window reads its last values greyed with "Rig not connected", controls disabled.
- A running plan: changes apply to the next frame or step; the This rig section keeps the "plan keeps running" line by Change profile / Change rig.
- Text up to 3.1x and Night mode (WCAG 2.2 AA bar; night mode exempt from contrast).

## Open before building

- Check each setting against what the daemon exposes and name any that need daemon work before the window that uses it is built.
- Build order: the sheet and This rig (replacing today's menu), then one device window at a time, test-first.
