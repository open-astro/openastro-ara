# OpenAstro Ara — User Guide (v0.0.1)

Ara is two programs: a **daemon** that runs at the telescope all night (Raspberry Pi is the
reference platform) and **WILMA**, the client app you plan and monitor from (Windows, iOS, Linux,
macOS, Android). The daemon owns the session — close the laptop, imaging keeps going.

Installation is covered elsewhere: [`DEPLOY.md`](DEPLOY.md) for putting a release on a Pi,
[`RUNNING.md`](RUNNING.md) for building from source. This guide starts at "both are installed."

Throughout WILMA, **⌘K / Ctrl-K** searches every setting by keyword, and the **?** affordances open
contextual help — when this guide says "find the X setting," search is the fast way there.

---

## 1. First contact

1. Start the daemon (it listens on port `5555` and announces itself on the LAN via mDNS).
2. Open WILMA. On first run it scans for daemons; pick yours (or add it by hostname/IP if your
   network blocks mDNS).
3. WILMA claims the **control slot**: one client drives the daemon at a time. A second client that
   connects can request a takeover — you'll get an Allow/Keep-connected prompt on the current one.
   Everything is LAN-only in v0.0.1 (no TLS/auth) — treat the network as trusted, or VPN into it.

## 2. The setup wizard

First run walks you through profile creation (about 18 short screens across 7 stages): site
location and horizon, optics and camera, mount, guiding (PHD2/openastro-guider), plate solving,
safety policies, storage, and optional sky-data downloads. Notes:

- **Skip is always available** — anything skipped shows as "Default" in Settings and can be set later.
- The **sky-data screen pre-checks the recommended catalogs** (star catalog + deep-sky objects);
  they download in the background after you finish and power the planetarium, search, and
  Tonight's Sky.
- The wizard writes a **profile**; you can create more later (Settings → Profile) and export/import
  them. Exported profiles are stripped of paths, secrets, and location — the recipient re-runs the
  wizard sections that need local values.

## 3. Connecting equipment

The top bar shows a chip per device (camera, mount, filter wheel, focuser, rotator, guider, …).
Tap a chip to connect/disconnect and open its control panel. Everything speaks **ASCOM Alpaca**;
devices are discovered on the LAN, and the daemon remembers your choices for auto-connect on boot.

- **Camera** — cooler on/off with a target set-point ("Cooling to −10.0 °C" shows next to the live
  sensor temperature), gain/offset/binning capabilities, readout-mode picker where the driver
  offers modes. Connecting a camera auto-fills your profile's optics geometry for framing.
- **Mount** — slew/park/tracking controls plus a big Stop. Ara's meridian-flip handling is on the
  Sequencer side (see §7).
- **Guider** — Ara drives a separate guiding daemon (openastro-guider, a PHD2 fork) over its own
  port. The guider chip connects it; the Calibration dialog builds its **dark library / defect
  map** — you'll be asked to confirm the scope is covered first, and the build shows live progress.
- **Dome / flat panel / switches / safety monitor / observing conditions** — each has a panel with
  the controls its capabilities report.

If a device drops mid-session, the daemon attempts hot-reconnects and raises a diagnostic; the
health pill (see §9) turns amber/red until it clears.

## 4. Planning: the sky atlas and Tonight's Sky

The **Planning** tab is a full planetarium (Stellarium engine, offline once sky data is
installed). Search anything ("M 42", "NGC 7000", "Vega"), explore, and toggle display layers.

- **Tonight's Sky** ranks deep-sky targets for *your* site, horizon, and rig: visibility windows,
  transit times, achievable integration hours, and a 0–100 score. Tap a row to frame it.
- **Framing** — the Frame overlay draws your camera's true field of view (from the profile optics)
  with rotation and a mosaic grid. What you frame here seeds the sequence target.
- Moon/twilight context is annotated; your custom horizon (Settings → Safety → Site) shades the
  sky the trees actually block.

## 5. Sequencing

The **Sequencer** tab is where the night gets defined.

- Build sequences in the tree editor: drag instructions from the catalog, reorder by drag,
  edit coordinates/filters inline, and **Validate** checks the plan against connected gear.
- **Import from NINA** — existing NINA JSON sequences import with high fidelity (the common
  instruction set executes natively; anything unknown is shown and skipped safely).
- Templates let you save shapes you reuse ("LRGB target", "flats at dawn").
- Start/pause/resume/abort from the toolbar; a running sequence's file is protected from edits.
- **Calibration frames** — the Calibration screen generates flats/darks plans as sequences and
  tracks your library's coverage against what your lights need.

## 6. Imaging and monitoring

The **Imaging** tab shows the live story: current frame with stretch control, exposure progress,
HFR/star metrics, guiding RMS (in arcseconds), and **Take One** for a quick snapshot. **Live
View** runs a fast framing/focus loop when you need to point or focus by eye.

## 7. Unattended safety (the 3 a.m. story)

Ara assumes you're asleep while it works:

- **End-of-session flats**: starting a run pops the "Capture calibration frames tonight?" dialog
  in WILMA (or silently applies your remembered preference — set it under **Settings → Session →
  Calibration**). Answer "Panel flats at end" and, when the run completes, ARA generates a
  matching-flats sequence from tonight's session — each filter at tonight's exact focus, gain,
  and offset — and starts it immediately (light your panel when notified); "Sky flats at
  twilight" generates the sequence ready to run. Tick "remember my choice" to stop the prompt.
  You can always capture matching flats later from the Image Library instead.
- **Emergency stop**: the red **Emergency Stop** button on WILMA's bottom status bar is always one
  tap away, on every tab. After a confirmation it makes the daemon abort the running sequence and
  the in-flight exposure, stop guiding, park the mount, and switch the flat panel light off — then
  reports honestly what each step did (an unreachable mount is called out loudly so you go check,
  never papered over). A panicked double-press is ignored while the first stop is running.
- **Safety monitor reactions**: with an Alpaca SafetyMonitor connected, the daemon polls it every
  10 seconds and reacts the moment it reports unsafe, per **Settings → Safety → Policies → "When
  conditions turn unsafe"**: pause the sequence + stop guiding + park (the default), park only,
  abort + park, or notify only. WILMA gets a `safety.unsafe` alert before the action runs. If
  **auto-resume when safe** is on, the daemon waits your configured delay after conditions clear,
  unparks, restores tracking, and resumes the paused run — verify pointing afterwards unless your
  sequence re-centers its target. A run that is paused *awaiting you* (e.g. after a failed flip)
  is never auto-resumed.
- **Guider loss mid-sequence**: if the guider connection drops while a run is executing, the
  daemon applies **Settings → Safety → Policies → "When the guider is lost"** immediately —
  pause the run (default), skip the current target, or abort — while §63.3 process recovery
  tries to restart the guider daemon in the background. A *wedged* guider (socket alive, RPC
  unresponsive) is caught the same way by an active liveness ping. When recovery succeeds, ARA
  reconnects automatically within your "guider retry timeout" window and notifies you — just
  Resume the paused run; if it can't reconnect in time, reconnect manually (Equipment → Guider).
- **Meridian flips** run a guarded pipeline: a pre-flight check (predicted altitude, mount health,
  required equipment), an in-slew watchdog (stall/timeout/pier-side verification), a hard
  post-flip plate-solve gate (imaging does not resume on an unverified pointing), and a safe-rest
  fallback (park or stop tracking, guider stopped) if anything fails. Your profile's **first**
  flip announces itself and waits briefly for a confirmation.
- A failed flip **pauses the run resumably** ("needs your attention") instead of killing it —
  Resume re-attempts the flip after you sort the rig out.
- **Unattended shutdown**: if a run sits paused-awaiting-you and nobody responds within the
  configured window (default 10 min), the daemon puts the rig to bed — guider stopped, mount
  parked, accessories disconnected, cooler warmed gently at your ramp rate, camera last — and
  leaves a morning summary. Any sign of you (opening WILMA, dismissing the alert, any command)
  cancels the countdown. Configurable under Settings → Safety → Policies.
- **Notifications** escalate in severity during astronomical darkness so the alarm-worthy ones
  reach you; the §35 alarm loops on Critical until acknowledged.

## 8. Image library and stats

- **Library** organizes frames by session/target with a frame viewer (stretch presets + manual),
  auto-rating with HFR drift, bulk operations, and **Resume Target** to pick up where a previous
  night stopped.
- **Stats** rolls the catalog up: per-target integration, focus-vs-temperature trends, guiding
  RMS history, frame-quality scoring, best-frames sorting, calendar heatmap, CSV export.

## 9. Health, diagnostics, and when things go wrong

The always-visible **health pill** aggregates open diagnostics (green/amber/red) and survives
reconnects — tap it for the panel with each issue, what the daemon did about it, and the
recommended action. Diagnostics mode (notify-only vs auto-correct) is a profile setting.

For bugs: **Help → Report a bug** builds a zip (logs + profile + diagnostics) ready to attach to a
GitHub issue. See `CONTRIBUTING.md`.

## 10. Backups

Settings → Storage: the daemon snapshots your configuration (profile + sequences) as zip backups,
keeps the newest N (default 20), and restores atomically — including **from another daemon on your
LAN** by URL, verified by checksum before anything touches live config. Take a backup before big
reconfigurations; restore is a two-click undo.

**Real-time frame mirroring (§44)** — stream every newly-captured FITS to one desktop as the
night runs: enable **Settings → Session → Storage → "Stream new frames to this device"**, pick a
backup folder, and each frame is pulled, SHA-256-verified, and filed under
`<server>/<session>/` within seconds of capture. If the imaging drive dies overnight, everything
already pulled is safe on the desktop — the worst case is losing the single in-flight exposure.
One desktop streams at a time (the panel tells you who holds the slot); a crashed desktop
resumes where it left off, and transfers pause while an exposure is downloading from the camera.

---

### Cheat sheet

| I want to… | Go to |
|---|---|
| find any setting | ⌘K / Ctrl-K |
| connect gear | top-bar equipment chips |
| pick tonight's target | Planning → Tonight's Sky |
| define the night | Sequencer (or import your NINA sequence) |
| check on it from bed | Imaging tab (phone works — same app) |
| see why the pill is amber | tap the health pill |
| morning report | Notifications + the Stats tab |
