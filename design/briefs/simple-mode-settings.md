# Simple Mode Settings: design brief

Shaped 2026-11-03 (`/impeccable shape`). Mock: [`design/preview/simple-settings-mock.html`](../preview/simple-settings-mock.html) (one short Settings panel; one window per device, opened from its dot). Visual rules live in `DESIGN.md` ("Simple Mode: Image First"); the parent brief is [`simple-mode.md`](simple-mode.md).

## Rule: 4×90%, KISS, automation, self-recovery, no repeats

Simple Mode covers the top 90 % most used features, 90 % of the time, 90 % faster (the user's "4×90%"), and only the bare minimum for a successful imaging night (KISS). Less is more: Simple Mode uses the profile's defaults and decides for the user ("think for me"); automation and self-recovery do the work, so a screen says what is happening and what Ara will do rather than offering a knob. No redundant design: each thing lives in one place, and interactions are contextual (tap the thing to act on it). When in doubt, leave it out. A value another Simple Mode control already sets is not repeated (gain is on Preview's chip, the guiding graph on the bottom row). Everything else stays in the Advanced UI, and Simple Mode never links to it: Settings' Simple Mode switch is the way out.

## Job and audience

- **Who:** the Simple Mode imager, at the scope or on the couch, at night.
- **Job:** chang## Direction

- **Devices open from their dots, and only there.** Tapping a device's dot in the top bar opens that device's window under it: what it is doing now (one large line) and what Ara does with it, including how it recovers. Its header holds the status dot, name and Connect / Disconnect (Try again after a failure). Settings has no Gear list: the dots are the list. Ara connects the profile's gear on its own, and only connected or failed gear has a dot, so a device that won't connect shows red and its window says why.
- **The gear opens one short panel** for what belongs to no device: This rig (System tiles incl. storage free, rig and profile, Change profile / Change rig), Tonight (one paragraph: site from this device's GPS, dawn stop, park, warm-up, what happens if the sky turns unsafe), the Permanent pier and Simple Mode switches, version and Logs & bug report. No sheet and no sections (an earlier draft had a sheet with a Gear list; the dots made it redundant).
- **Values are shared** with the Advanced UI and come from the profile; Simple Mode reads them and never asks. Switching modes resets nothing.
- **Missing automation is built as we go** (user, 2026-11-03): a line Ara doesn't do yet becomes daemon work when its window is built, never a knob.

## Content (no knobs, no repeats)

| Place | Shows | Ara decides and recovers | Buttons |
|---|---|---|---|
| Camera dot | Temperature, target | Cools when a plan starts, waits for the target, warms at the end; retries a failed frame, reconnects a dropped camera | Connect |
| Mount dot | Tracking, side of the meridian | Tracks after Go to, flips and re-centres at the meridian, parks at the end | Connect, Park now |
| Focuser dot | Position, temperature | Focuses before the first frame, again on a 2 °C drop or a filter change (by hand: Focus mode) | Connect |
| Filter wheel dot | Filter in place | Follows the plan (by hand: Preview's chip) | Connect |
| Guider dot | State, RMS | Guides after centring, dithers; on a lost star pauses, finds a star (plate solving if it must) and resumes (graph: the bottom row) | Connect |
| A failed device's dot | Why it didn't connect | Tries again every minute; the night runs without it | Try again |
| Gear › This rig | System tiles (storage free included), rig, profile | none | Change profile / rig |
| Gear › Tonight | One paragraph: site, dawn, park, warm-up, unsafe sky | Site from GPS; stop at dawn, park, warm; park when unsafe, alarm after 5 s unless silenced, resume when safe | none |
| Gear › switches | Permanent pier, Simple Mode | none | the two switches, Logs |

ple Mode switch, Logs |

Each "Ara decides" line is a claim to check against the daemon before it ships; one Ara doesn't do yet becomes daemon work, never a knob.

## States

- A device not in the profile: not listed. Disconnected: grey dot and Connect. Connecting: amber dot and Cancel. Fault: red dot and the reason in the window.
- No rig link: every window reads its last values greyed with "Rig not connected", controls disabled.
- A running plan: changes apply to the next frame or step; the This rig section keeps the "plan keeps running" line by Change profile / Change rig.
- Text up to 3.1x and Night mode (WCAG 2.2 AA bar; night mode exempt from contrast).

## Open before building

- Check each setting against what the daemon exposes and name any that need daemon work before the window that uses it is built.
- Build order: the gear panel (replacing today's menu), then one device window at a time from its dot, test-first; each window's missing automation is built with it.
