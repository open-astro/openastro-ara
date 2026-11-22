# OpenAstro Ara — User Guide (v0.0.1)

Ara is two programs: a **daemon** that runs at the telescope all night (Raspberry Pi is the
reference platform) and the **OpenAstro Ara app** you plan and monitor from (Windows, iOS, Linux,
macOS, Android). The daemon owns the session — close the laptop, imaging keeps going.

Installation is covered elsewhere: [`DEPLOY.md`](DEPLOY.md) for putting a release on a Pi,
[`RUNNING.md`](RUNNING.md) for building from source. This guide starts at "both are installed."

Throughout the app, **⌘K / Ctrl-K** searches every setting by keyword, and the **?** affordances open
contextual help — when this guide says "find the X setting," search is the fast way there.

---

## 1. First contact

1. Start the daemon (it listens on port `5555` and announces itself on the LAN via mDNS).
2. Open the app. On first run it scans for daemons; pick yours (or add it by hostname/IP if your
   network blocks mDNS).
3. The app claims the **control slot**: one client drives the daemon at a time. A second client that
   connects can request a takeover — you'll get an Allow/Keep-connected prompt on the current one.
   Everything is LAN-only in v0.0.1 (no TLS/auth) — treat the network as trusted, or VPN into it.

## 2. The setup wizard

First run walks you through profile creation (about 18 short screens across 7 stages): site
location and horizon, optics and camera, mount, guiding (PHD2/openastro-guider), plate solving,
safety policies, storage, and optional sky-data downloads. Notes:

- **Skip is always available** — anything skipped shows as "Default" in Settings and can be set later.
- The **sky catalogs** (stars + deep-sky objects) ship inside the rig server package, so the
  planetarium, search and Tonight's Sky work offline from the first start. The **plate-solver star
  database** is the one download: the review screen works out the field of view of your camera
  (and of the guide camera, which polar alignment solves) from the focal length, the reducer or
  Barlow factor you enter on the equipment step, and the sensor the camera reports, then lists the
  ASTAP database each needs — the smallest that covers the field (D05, 109 MB, for most refractors
  and lenses; G05/W08 for wide lenses and all-sky cameras; D50/D80 for narrow fields) — and downloads
  it on the rig after Save. The rig needs internet once. A field narrower than about 0.15° cannot be
  solved by any database; the screen says so. Settings → Sky data marks the main camera's pick as
  Recommended and lists every database installed; nothing is removed when a second profile needs a
  different one.
- The wizard writes a **profile**; you can create more later (Settings → Profile) and export/import
  them. Exported profiles are stripped of paths, secrets, and location — the recipient re-runs the
  wizard sections that need local values. Each profile has a matching **OpenAstro Guider profile**
  (same name) holding its guide calibration and dark library. Deleting a profile asks whether to
  delete that guider profile too (on by default; off when another profile of the same name shares
  it). The guider profile goes right away when the guider is connected and idle; with no guider
  connected, or while it is guiding or calibrating, the delete waits and runs the next time the
  guider connects or stops — the dialog says which, and a message after Delete reports what
  happened.

## 3. Connecting equipment

The top bar shows a button per device (Camera, Mount, Guider, Focuser, Filter Wheel, Rotator, …),
with a status dot that blinks green while the device is working. Tap one to open its Settings page,
where you connect/disconnect it and use its controls. Everything speaks **ASCOM Alpaca**;
devices are discovered on the LAN, and the daemon remembers your choices for auto-connect on boot.

- **Camera** — cooler on/off with a target set-point ("Cooling to −10.0 °C" shows next to the live
  sensor temperature), gain/offset/binning capabilities, readout-mode picker where the driver
  offers modes. Connecting a camera auto-fills your profile's optics geometry for framing.
- **Mount** — slew/park/tracking controls plus a big Stop. Ara's meridian-flip handling is on the
  Sequencer side (see §7).
- **Guider** — Ara drives a separate guiding daemon (openastro-guider, a PHD2 fork) over its own
  port. The top-bar Guider button opens its Settings page: connect it from the connection card
  there, and the page's Calibration section builds its **dark library / defect
  map** — you'll be asked to confirm the scope is covered first, and the build shows live progress —
  and can also **delete** either artifact (with confirmation). The daemon's own equipment (guide
  camera, mount, rotator, Alpaca server) is configured entirely from **Settings → Guider → Guider
  equipment**: refresh the daemon's device choices, discover Alpaca servers on its network, pick
  devices, and **Apply to guider** — no visit to the daemon's web portal is ever needed. If you
  change the guide camera, Ara flags the existing dark library as stale and recommends a rebuild.
- **Dome / flat panel / switches / safety monitor / observing conditions** — each has a panel with
  the controls its capabilities report.

If a device drops mid-session, the daemon attempts hot-reconnects and raises a diagnostic; the
health pill (see §9) turns amber/red until it clears.

### Smart Focus (Checklist → Smart Focus)

The Checklist tab has a **Smart Focus** step between Connect equipment and Polar align.

- **Main telescope** — **Run autofocus** starts the daemon's sweep with your autofocus settings and
  shows it live: the coarse search, then every probe on the V-curve, the fitted curve and the best
  position it picked, the statistics (HFR measured *at* best focus, stars, fit R², probes kept,
  duration, focuser temperature, filter) and a picture of the focused field with star rings. **Cancel**
  stops a run — including one a sequence started — and the focuser goes back to where it began if
  your profile says so. The same run record is what a sequence's autofocus leaves behind, so you can
  check last night's refocus here in the morning.
- **Guide camera** — a live focus readout, not an autofocus: guide scopes and off-axis guiders have
  hand-turned helical focusers. **Start** shows a live view from the guide camera with HFR, star
  count, peak and FWHM per frame, an HFR trend and the best HFR seen so far; turn the focuser slowly
  until the HFR stops falling, then **Stop**. The frames come *through the guider* (the same path
  polar alignment uses), so the guider must be connected and not guiding or calibrating, and Ara never
  opens the guide camera itself. With an **off-axis guider** the card waits until the main telescope
  has been focused this session (an OAG shares the main scope's focus) — or tick *already in focus*.

Do not connect the guide camera as the *main* camera to focus it: two programs driving one sensor
breaks guiding, and Ara now refuses that connect while the guider is connected (add `?force=true`
only if you know what you are doing).

## 4. Planning: the sky atlas and Tonight's Sky

The **Planning** tab is a full planetarium (Stellarium engine, offline once sky data is
installed). Search anything ("M 42", "NGC 7000", "Vega"), explore, and toggle display layers.

- **Tonight's Sky** ranks deep-sky targets for *your* site, horizon, and rig: visibility windows,
  transit times, achievable integration hours, and a 0–100 score. Tap a row to frame it.
- **Target by coordinates** — the pin button beside the search box (or pasting a position such as
  `02 37 31.5 +71 18 16` or `39.381 71.304` into the search) takes an RA/Dec from SIMBAD, Gaia or
  your mount, as J2000 or JNow, with a name. *Add to plan* builds the same imaging run as a
  Tonight's Sky row, so the sequence slews there; *Show on sky* frames it. Recent typed targets
  are kept on the device for the dark site.
- **Catalogs** — highlight a whole catalog on the sky: colored rings mark every Messier, Caldwell,
  NGC, or IC object (or a type — galaxies, globular clusters, planetary nebulae, …), brightest
  500 first, sized by magnitude so the showpieces stand out. Toggle several at once; each catalog
  gets its own color, your picks persist across restarts, and the underlying deep-sky layer still
  supplies names and tap-to-select. The catalogs ship inside the app, so this works offline
  and without a daemon.
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
- While a run is up, the band above the tree shows the current step and, under it, what the guider
  is doing in the same words as the Live tab's guiding strip — *Looping*, *Star selected*,
  *Calibrating · North step 7 · 11.9 px*, *Guiding*, *Settling*, *Star lost* — so a long
  calibration behind "Start guiding" never looks like a stalled run.
- **Calibration frames** — the Calibration screen generates flats/darks plans as sequences and
  tracks your library's coverage against what your lights need.

### Automatic refocusing (autofocus triggers)

Add autofocus **triggers** to a sequence container and ARA refocuses on its own when it matters:

- **After N exposures** — the classic cadence trigger.
- **After time** — once a set number of minutes has passed since the last autofocus (the
  long-term drift catch-all).
- **After temperature change** — when the focuser temperature drifts a set number of °C from
  the last autofocus (temperature is the dominant focus-drift driver).
- **After HFR increase** — when the measured HFR trend of your recent frames climbs a set
  percentage above the session's best. Every light frame is star-analyzed seconds after
  capture, so this reads your actual image quality, not a proxy.
- **After filter change** — the first light frame on a newly-selected filter refocuses, which
  is how per-filter focus offsets are learned (autofocus always runs on the current filter —
  it never swaps to luminance and back).

All of them yield to an imminent meridian flip, and all of them **defer during bad sky** —
if diagnostics reports clouds passing, a blocked aperture, or dew forming, the trigger waits
("Autofocus deferred — clouds passing. Will run when conditions recover.") instead of burning
sky time on a sweep that would fail, then fires as soon as conditions clear. Imported NINA
plans carry these triggers over natively.

> Note: the deferral machinery is live, but the sky-condition detector that feeds it (the
> live acquisition-pattern analyzer) hasn't shipped yet — until it does, triggers fire
> normally in all conditions. No configuration is needed; deferral switches on by itself
> when the detector lands.

## 6. Imaging and monitoring

The **Live** tab shows the live story: current frame with stretch control, exposure progress,
HFR/star metrics, guiding, and **Take One** for a quick snapshot. **Live View** runs a fast
framing/focus loop when you need to point or focus by eye.

- **Exposure timer.** Whenever the daemon is exposing — a sequence light, a Smart Focus probe or a
  plate-solve capture — a timer appears in the right-hand panel under **Take One** with the frame
  type and filter, the elapsed / total clock and a progress bar. It switches to **Downloading** ("Reading out the
  sensor…") once the shutter should have closed and disappears when the frame has landed. Your own Take One
  shows its progress on the Take One card instead, and Live View frames have no timer.
- **Guiding strip.** The guide graph runs along the bottom of the Live tab and behaves like PHD2's
  graph window (the guider *is* PHD2): RA error in blue, Dec in red, correction pulses as faint
  bars from the zero line, newest frame at the right. Dithers are marked with a dashed line and the
  settle window is shaded until PHD2 reports settled; a lost star shows as a red × on the zero
  line. Under the plot are PHD2's controls — frames in view (50/100/200/400), y range (auto or
  fixed), arc-sec / pixels, corrections on/off, and **Clear**. Beside it, PHD2's numbers over the
  frames in view, leaving out the frames taken while settling after a dither (they are the dither,
  not the guiding; PHD2's own graph counts them): RMS RA / Dec / Total, peak per axis, and the RA oscillation index. The axis is in
  arcseconds once the guider has reported its pixel scale (or you have set the guide focal length
  and pixel size under **Settings → Guider**), and in guide-camera pixels until then. Tap the
  header to collapse the graph to a one-line status. A client that connects while guiding is already
  running gets the guider's last frames (up to 400) drawn straight away. **Tune** opens **Tune Guiding**: RA/Dec
  aggressiveness, minimum move, Dec guide mode, dither pixels, the **guide exposure** (0.5 s is a
  good start on a harmonic mount) and the **guide camera gain** (percent of the camera's range, 50 %
  by default; lower it if guide frames look grainy and stars are hard to find). Apply sends only these runtime-safe values to the guider, so
  guiding keeps running; equipment and optics changes live in **Settings → Guider** and do
  reconnect the guider. **More…** opens **Guider settings** for everything else the guider offers —
  multi-star, star detection, auto exposure, noise reduction, subframes, the RA and Dec algorithms,
  maximum pulses, Dec backlash, mount options, dither scale and variable delay. Each row shows the
  guider's own value until you set one; your values are kept in the profile and sent on Apply while
  guiding continues. Multi-star is on unless you turn it off; saving it briefly reconnects the guider's equipment, so it is refused while guiding (stop guiding first) - the **Multi-star** / **Single star** chip in the Guide camera window changes it the same way. Setting a row back
  to the guider's (or **All guider's**) only stops Ara sending it: the guider keeps whatever value it has,
  including one Ara sent before.
- **Guiding Assistant.** The Guide camera dialog's assistant button (also under Checklist → Smart Focus →
  Guide camera) runs PHD2's Guiding Assistant from Ara. Loop, pick a star and calibrate once (start
  guiding, or let a run do it), then choose how long to sample (two minutes is plenty) and whether to
  measure Dec backlash, and press **Start**. Guiding is held off while the star drifts; the dialog shows
  the RA and Dec drift, the RMS, the high-frequency RMS (your seeing), the polar-alignment error it
  implies and, after the backlash test, how much Dec backlash the mount has. When it is done, the
  suggested guide exposure, RA and Dec minimum move and Dec backlash compensation (and, for a mount
  whose backlash is too large to compensate, a Dec guide mode or Dec algorithm instead) are listed, ticked,
  with PHD2's own notes; untick anything you want to keep and press **Accept ticked** to write the rest to
  the guider's profile; what was accepted stays marked **applied** and is not offered again when the
  dialog is reopened. **Start** waits while the guider is guiding (a run's or your own): stop guiding
  first, because the assistant needs the star to drift uncorrected. Run it on a calm night after polar alignment, and again after you change the
  guide camera, scope or mount.
- **Starting Polar Align.** Point the tube 2–5° from the pole with the counterweights down, then
  start. Polar Align starts from there: it never sends the mount Home and never slews it, and a run
  never moves its Dec axis, as NINA's three-point alignment. A parked mount is refused ("Mount is
  parked — unpark it and point near the pole first"); Polar Align never unparks it. A tube within 3°
  of the pole is refused too ("point it 2–5° from the pole"): the camera would look along the RA
  axis. Point it off yourself, or use **Move to start position** (below). Polar Align also refuses, like NINA, a site with
  no longitude or one still at latitude 0° and longitude 0° (set your site first: it steers by the
  sidereal time), an alt-az mount (turning it would turn azimuth, not RA), and a mount that cannot
  turn its RA axis by motor speed: Polar Align always turns RA with the motor. Before anything moves,
  a confirm step shows which way RA will turn (east or west: on the first start the **Turn
  direction** setting, later back toward Polar Align's start) and how far (about 10° twice); the
  mount moves only when you tap **Move the mount**. Keep a hand on the mount's power switch.
- **Room to turn the mount** (#2165). Before the motor first turns a mount, Polar Align asks one
  question: "Polar Align will turn the mount up to N° each way. Is there room for the tube,
  counterweight and cables to move that far?" N is how far this run turns RA each way: the two turns,
  plus 0.5° the first time, when it learns which way the RA motor runs. Look, and tap **Yes, there is
  room**; you never measure an angle. Until you do, the pre-flight list fails and Start stays off.
  Cancel just closes the question, and nothing moves. The answer is kept for each mount: a smaller or
  equal turn stays confirmed, a bigger one (a larger turn size, or a Check whose step is bigger) asks
  again when you start it. **Take back** withdraws it.
- **Move to start position** (#2166). When the pre-flight list's "Where the scope points" check fails
  because the tube points within 3° of the pole, a **Move to start position** button shows under it.
  It moves only the Dec axis, never RA and never a GoTo, until the tube points about 4° from the pole;
  the counterweights stay where they are. It first moves Dec 0.3° to learn which way the Dec motor
  turns. While the mount is parked, the site is not set, its position is not trusted, the clearance
  is not confirmed, it is an alt-az mount or it cannot turn Dec by motor speed, the button is off and
  the reason is written beside it. A tap asks first ("Move to the start position?"); nothing moves
  until you confirm. While it moves, "Moving the scope to the start position…" shows above the list
  with **Stop**, also after you switch tab or mode and come back; Stop Mount and Emergency Stop work
  too. It stops on its own if Dec stalls, takes too long, goes the wrong way, RA moves, or the pier
  side changes, and a mount too slow to finish within 5 minutes is refused before it moves. Then the
  list is read again: once the check passes, start Polar Align.
- **Turn direction and the motor test.** Settings › Imaging › Polar align › **Turn direction** picks
  which way RA turns when Polar Align starts from a new pose: **Automatic** (east in the north, west
  in the south, as NINA), **East** or **West**. Pick the one whose turns do not cross the meridian from
  where you point. Later starts in the same session turn back toward the first one, whatever it says.
  Mount drivers do not agree on which way a positive motor speed turns RA, so the first start on a
  mount (and the first after its driver or driver version changes) first turns it 0.5° to learn
  which way its motor runs; the confirm step says so. Ara saves that for the mount, and **RA motor
  direction** in the same panel shows it with the mount it was tested on (its Alpaca unique id or
  name) and the driver's name and version, when the driver reports them. If a turn ever goes more than 0.5° the other way, Polar
  Align stops the motor and ends the run ("the mount turned the other way from its saved motor
  direction"); it never turns the mount back by itself. Check the mount, tap **Test direction
  again**, and start again.
- **Where to point for Polar Align** (NINA's advice, also on the confirm step). Pick a field fairly
  high toward the pole for your hemisphere, at least about 15° above the horizon when you can, and a
  start whose turns do not cross the meridian, so the tube cannot swing into the pier. Keep a hand on
  the mount's power switch the first time you run this on a mount.
- **The azimuth number is a true arc.** Ara shows the azimuth error as an arc on the sky (cos alt
  applied, "true arc" next to the number). NINA shows the angle itself, which is larger by about
  1/cos(latitude) (1.5× at 48°); the Full UI shows NINA's figure beside Ara's so the two compare.
  A measurement that starts more than 2° from the pole warns that the correction is less reliable (get
  closer, then run again); over 10°, check the site location, the start field, and that the mount
  turned only in RA.
- **Polar Align moves only from a fresh reading of the mount.** Every turn starts and stops on where
  the mount says it points right now, never on the app's last known position. When the mount gives
  no fresh reading (no answer, a blank 0, 0 position, or one more than 2 s old), Polar Align refuses
  to start ("got no fresh reading of where the mount points"), or stops the motor and ends the run:
  check the mount's connection and start again. A mount that reconnects during a run (Wi-Fi drop,
  power blip) ends the run too, even while you turn the knobs: it may have been moved or re-homed
  meanwhile. A mount that reports a Dec past the pole (some mounts do after a sync) is refused before
  anything moves, because its pier side is not known: point the tube 2–5° from the pole with the
  counterweights down using the hand controller, or send the mount Home, then start again.
- **Polar Align keeps RA near where it first started.** The first start after the mount connects is
  Polar Align's start for the session. Every later run (**Adjust again**, **Check**, or a new Start) turns back
  toward it, and the confirm step says how far RA is from it ("RA is 20° east of where Polar Align
  started"). A run that would take RA more than 30° from it is refused (or more than twice the turn
  plus 2°, when your turns are over 14° each): slew the mount back near the pole, counterweights down,
  and start again. When you move the mount some other way (a slew, the hand
  pad, by hand) or reconnect it, the confirm step says the pose is a new start, and it becomes the start.
  Tracking that stays on (or stays off) does not count as moving it: after a long time tracking
  (guiding, say) RA may have turned far enough that the next run is refused until you slew back.
  Turning tracking on or off between runs can count as a move, and the next start is then a new start.
  In Simple Mode's Preflight, the polar step no longer starts on its own: start it from the shutter.
- **Polar Align makes one measurement per Start.** **Start**: from where you pointed it, the RA motor
  makes two 10° turns, never a GoTo, the guide camera solving a frame at each of the three stops; the
  circle through them is where the mount's axis really points. Then the guide camera follows your knob
  moves live, a frame a second, until **Done** or **Stop**. A Start never moves on to another
  measurement by itself (the old Rough → Fine → Check chain and its **Next** button are gone).
  **Check**: on the adjust screen once inside the tolerance, and again after **Done**, three solves
  from where the mount is, 10° apart by default, measure the result on their own, not read off the
  loop, so it says how good the alignment really is and how far it disagrees with the last run's
  readout. Check says how far it turns the mount ("Check turns the mount 2 × 10°") and goes through
  the same confirm step; from the adjust screen it ends the live loop first. Every Check counts toward
  the limit Polar Align keeps RA within (30°, or twice the turn plus 2° for big turns), so repeated Checks are refused once RA is too far from where
  Polar Align started. **Rough first (main camera)**: offered when the guide camera could not solve
  the pole field; the same measurement on the main camera, whose frames take a few seconds each, and
  the card says how far from the pole you are and on which side, for the big moves. Like Check, it
  says how far it turns ("Rough first turns the mount 2 × 10°") and goes through the confirm step.
  Within a few arcminutes, stop and start again: the guide camera refines it.
- **Polar Align's camera and frames per point.** Settings › Imaging › Polar align › *Camera* picks
  the camera for every measurement, its live readout and Check: the guide camera (the default) or
  the main camera, with its own plate solver at the rough step exposure. Use the main camera when the
  guide camera cannot solve the pole field; Start refuses while it is not connected, and the confirm
  step says the run uses it. *Frames per point* (1 to 5, 3 by default) is how many frames each of the
  three points takes with the mount still; they are averaged, so one bad solve does not move the
  axis, and their scatter is in the log. A point whose solves scatter more than 3″ is taken again
  once; if it is still scattered, or a cloud hides the retake, the run goes on but says the
  measurement is noisy, under the readout or under the Check. Check focus and seeing, or take more
  frames per point. The mount tracks while the frames are taken: with many long frames per point,
  a run can end before a turn that would take the mount past its sweep limit. Slew back near the
  pole and start again, or take fewer or shorter frames.
- **How sure the readout is (#2072).** The total error shows with its uncertainty, for example
  **0.2′ ± 1.1′** (one sigma), in the full UI and Simple Mode. The uncertainty comes from how much
  each point's frames scattered. A point is never counted surer than the solver can measure: about
  0.15 pixel per frame at the pixel scale of the camera that took it (guide or main camera), shared
  among the point's frames (÷ √3 for three); when Ara does not know the pixel scale it counts 1″.
  With 10° turns, each arcsecond of solve noise is about 1.3′ of axis error.
  The screen says **inside your tolerance** only when the error plus twice its uncertainty is inside
  it. While only the error is, it says the measurement can't confirm the alignment; **Done** and
  **Check** stay available. Larger turns shrink the uncertainty most: 20° in place of 10°
  (**Polar align turn** and **Check step** in Settings → Imaging → Polar align) cuts it about four
  times; more frames per point help a little. The Check's verdict (Excellent, Very good, …) is rated
  on the error plus twice its uncertainty too, so a noisy check does not read Excellent. The
  uncertainty covers only what the frames of each point show: a mount that shifts between points
  (flexure, a slipping clutch) is not in it. With a Polar Aligner, the motors stop once the error is
  inside the tolerance (more moves cannot shrink the uncertainty), and the line says when the
  alignment is not confirmed.
- **Polar Align's Stop stops the mount.** Stop, Done, a failure and the end of a Check all stop the
  mount where it is: Ara stops both motors and checks on fresh readings
  that the mount stands still. If it cannot confirm that, the card says so: cut the mount's power and
  check the mount before you start again. Ara then stops trusting where the mount says it points,
  as after a power loss: GoTo, Park and Polar Align stay blocked until a Home ends at Home. Press Home
  in the app (it works while the position is unverified, as in NINA): once Ara sees the mount move and
  then report Home, the position is trusted again. A Home that never moves (a mount that already thinks
  it is at Home) clears nothing; you can also confirm Home in the mount window once you have checked by
  eye that it is there. A mount with no Home in Ara: run its zero search from its hand controller or
  app, then confirm Home. The same happens when
  the mount disconnects and connects again while Polar Align is open. Then tracking goes off, as NINA does by default, so the mount
  cannot track into the pier while nobody watches. With *Stop tracking when polar align ends* off
  (Settings › Imaging › Polar align), Done and the end of a Check give back the tracking the run found;
  a Stop or a failure still leaves tracking off. A GoTo turns tracking on again by itself; a sequence
  started over an open run needs a slew or *Set Tracking* before it guides or images. Starting
  guiding while Polar Align is still open stops the mount the same way, then gives back the tracking
  Polar Align found, because guiding needs it.
- **Which number to trust (#2072).** The two numbers measure different things. Polar Align's figure
  is the RA axis against the refracted pole, from plate solves, live while you turn the bolts; its
  azimuth is the true arc on the sky (cos alt applied, so it can read smaller than NINA's unscaled
  azimuth). The guiding residual is a lower bound from Dec drift, taken later. The live readout
  needs the camera to stand still: Polar Align turns tracking off, checks that the mount reports it
  off (and not slewing) before the first live frame and on every frame, and ends the run (*the mount
  kept tracking*, *started tracking during the live readout*, *reads as slewing*, or *is not
  connected* when tracking off is sent) when it does not. When the camera points nearly due
  east or west of the pole, the readout reads the altitude bolt poorly and the card says so: trust
  **Verify**, or start again with the scope nearer the pole.
  When Polar Align carries its uncertainty, the two numbers are said to disagree only when the
  guiding figure is above Polar Align's by more than twice their combined uncertainty, and the
  tooltip shows both with it (for example *12″ ± 66″ vs at least 17′ ± 70″*).
- **Stop Mount and Emergency Stop end Polar Align.** Any stop Polar Align did not send itself ends
  the run at once, even between two turns while it solves a frame or while you turn the knobs:
  **Stop Mount**, **Emergency Stop**, the meridian-flip watchdog, a **Park** or a **Home**, the
  direction pad, or the safety or equipment-fault reaction when it could not park the mount. The card
  says what stopped it, in the app's language, and no further turn starts. Emergency Stop latches before any of its other
  steps, so Polar Align cannot start a turn or switch tracking on while the rest of the stop runs.
  From Start until it ends (its solves and the live adjust loop too), Polar Align holds the mount: a
  GoTo, a sync, Center, Home and Park are refused ("Polar alignment is using the mount"), and so are a
  sequence's GoTo, Park, Find Home and meridian flip; a direction-pad move is refused while it turns
  the mount. Stop Polar Align first, tap **Done**, or tap Stop Mount. Emergency Stop, Stop Mount, a pad
  release and Tracking off always get through. When the sky turns unsafe, a device fault ends in a
  park, or End Night (or the unattended shutdown) homes and parks the mount, it stops Polar Align and
  the Polar Aligner first. A Home you tap just before Stop Mount or Emergency Stop does not start
  after the stop.
- **Polar Align turns only a still mount.** It refuses to start while the mount is slewing (or does
  not say whether it is), or while the guider is guiding or calibrating ("the mount is moving or the
  guider is guiding"): let the slew finish or stop guiding, then start again. Before each turn it
  reads the mount twice, a second apart: if the mount slews or moved in between (the hand controller,
  another program), or the guider started guiding, the run ends before the motor starts and the card
  says why.
- **Polar Align stops a turn the moment it goes wrong (#2067).** Each turn runs at the fastest speed
  the mount offers at or below 3°/s (1°/s for its last 3°), never faster than that. While it turns,
  Ara reads the mount four times a second and stops the motor and ends the run when: the position
  stops changing for 2 s (*stopped changing*); the turn takes more than twice as long as it should at
  its speed (*turned only … in … s*); the Dec reading moves more than 0.1° (*Dec reading moved*: an
  RA turn should not move it; check the Dec clutch); the mount reports the other side of the pier;
  or the RA reading jumps further than the motor can turn. After the motor stops, Ara waits until the
  mount stands still. A mount that runs on more than 2° past the turn is stopped again at once (if
  that stop cannot be confirmed either, cut the mount's power). The turn must end within 2° (or 10%)
  of its size. The plate solves check the mount too: if one frame is turned about 180° from the one
  before, the mount flipped to the other side of the pier; the run ends with tracking off. If the
  mount's Dec reading moved more than 2″ across the three points, the readout (or the Check's verdict)
  warns that the measurement may be off: check the Dec clutch is tight and measure again.
- **Polar Align's pre-flight checks.** The Start card lists what Polar Align checks before it moves
  the mount: the mount is connected and not parked, its position is trusted, its clock is set, and
  where the scope points. A check that fails turns Start off and says why; the rig checks the same
  list again when you tap Start, so an older app cannot skip it. The mount's clock check passes once
  Ara has set the mount's clock from a synced rig clock: just after you connect, Start waits a few
  seconds for that. The first time you use the motor on a mount, **Confirm clearance…** asks you to
  turn RA with the hand controller from counterweight-down, see how far it can go before anything
  touches the pier, the tripod or a cable, and set that limit. Until you confirm, **Move the mount**
  stays off. The confirmation is kept for
  that mount in this profile, for that limit: change the limit and you confirm again. **Take back**
  (in the dialog, or Settings › Imaging › Polar align) removes it. No setting or app option skips a
  failing check.
- **A Polar Aligner's motors stop when Ara says stop.** With a motorised Polar Aligner that Ara can
  drive, Ara turns its bolts for you. Every Stop is checked: Ara sends Stop and watches for up to 3 s
  until neither motor moves, sending Stop at most three times in all. If the device never confirms it,
  the card says so (cut its power if it still moves), **Stop** sends it again, and **Resume** and
  **Home** stay off until a Stop is confirmed; then Ara waits for you to tap **Resume**. Each move
  must change the device's reported position by about the amount sent, in the right direction, or
  the motors stop ("went the wrong way", "didn't go the distance asked"). A run moves each axis no
  further than three times its first error plus 30′ (at least 10′) and makes at most 40 moves; a
  device that cannot stop on command gets moves of at most 10′.
- **Starting guiding ends Polar Align.** Polar Align holds the guide camera while it runs. If guiding
  is started while it is still open (a run's Start Guiding after the Polar Align step was left
  without **Done**, a re-guide after a meridian flip, or Guide pressed by hand in the guider panel), Ara stops the
  alignment, without saving a result, and then guides. Finish Polar Align with **Done** first if you
  want its result kept.
- **Polar alignment left after Align.** Each time guiding starts, Ara measures how much polar
  alignment error is really left, at no setup cost: over the first 5 minutes of clean guiding
  (dithers and settling are left out) it adds the guider's Dec corrections back onto the star's
  offset to get the drift the guider is fighting, and turns that into a polar alignment error. The
  guiding strip's header reads "PA residual · measuring 2:00 / 5:00", then e.g. **PA ≥ 48″ ·
  Excellent**, with the same bands as the Polar Align card. It catches what a plate-solve alignment
  can't see: refraction, flexure and the mount's own slop. It is a lower bound ("≥"): Dec drift at
  one hour angle shows only part of the error. The tooltip has the drift, the hour angle and
  tonight's Polar Align result to compare. "noisy" means the guiding was too rough for a firm
  figure. "backlash" means the star did not answer the Dec pulses: the guider kept pushing one way
  and the star kept moving away, which is Dec backlash, not drift, so that window can't measure
  polar alignment. Both are marked unreliable. When the Dec pulses change direction, Ara also waits
  until the star answers the new pulses, at most 60 s, before it measures again, so the gear's
  slack is taken up first. "disagrees with Polar Align" means it came out well above tonight's Polar Align result,
  which a lower bound can't be: either Dec backlash ate guide pulses (the guiding figure is too
  high), or the mount shifted since the alignment or its reference was wrong (the Polar Align figure
  is too low). Run Polar Align again: about the same figure again points at backlash, a much larger
  one at the mount or the alignment; "n/a" means it could not be measured (lock-position shift on, an adaptive-optics unit, or no calibration data).
  Guiding that stops after 2 minutes still reports what it has.

## 7. Unattended safety (the 3 a.m. story)

Ara assumes you're asleep while it works:

- **End-of-session flats**: starting a run pops the "Capture calibration frames tonight?" dialog
  in the app (or silently applies your remembered preference — set it under **Settings → Session →
  Calibration**). Answer "Panel flats at end" and, when the run completes, ARA generates a
  matching-flats sequence from tonight's session — each filter at tonight's exact focus, gain,
  and offset — and starts it immediately (light your panel when notified); "Sky flats at
  twilight" generates a **sky-flat** sequence and starts it too — it opens by waiting for dawn
  twilight, so you can leave it parked on the wait and it fires itself when the sky is right.
  Tick "remember my choice" to stop the prompt. You can always capture matching flats later from
  the Image Library instead.
- **Flats expose themselves**: each generated per-filter set is a **Flat Panel Flats** step — it
  lights a connected flat panel, probes short exposures until the frame's mean brightness hits
  your target ADU, then captures the set at the converged exposure. Tune the target, tolerance,
  frames per filter, and whether the mount parks after flats under **Settings → Session →
  Calibration**; the same step is available in the sequence editor's Calibration category for
  hand-built plans (manual EL panels work too — the probe measures whatever light is there).
- **Twilight sky flats**: prefer the sky to a panel? The **Sky Flats** step captures against the
  dawn sky. The generated sky sequence waits for the sun to reach the altitude you set (about
  −9° / nautical twilight by default), slews to a blank patch of sky, then re-probes the sky
  before **every** frame because twilight brightness drifts minute to minute — and stops honestly
  if the sky climbs above or falls below the usable brightness window before the set finishes.
  Set the target ADU, frames per filter, the sky patch (azimuth/altitude), the stop-above/below
  bounds, and the wait-for sun altitude under **Settings → Session → Calibration**; the step is
  also in the sequence editor's Calibration category for hand-built plans.
- **Emergency stop**: the red **Emergency Stop** button on the app's bottom status bar is always
  there, on every tab. Press and hold it for 2 s ("Hold to confirm"; letting go early sends nothing).
  With a keyboard or screen reader, activating it opens a "Stop everything now?" confirmation
  instead (in Simple Mode, press it again within 3 s). Before anything else it latches the stop, so a
  polar alignment cannot start another turn meanwhile. It then makes the daemon abort the running sequence, stop the mount where it is (it never parks), halt the rotator (it stops where it is), abort
  the in-flight exposure, stop the guide-camera focus loop, the Bahtinov readout, a running autofocus and polar alignment, stop guiding, stop the mount and the Polar Aligner again if a polar alignment was running, switch the flat panel light off, and switch the camera's cooler off — then reports honestly what each step did (an unreachable mount is called out loudly so you go check,
  never papered over). A panicked double-press is ignored while the first stop is running.
- **Audible alarm**: when anything above reports UNSAFE, the app pops a safety modal at once and,
  after a short silent delay (default 5 s — silence it from the screen before it ever rings),
  loops an alarm tone at full volume until you silence it or conditions clear. Pick the tone and
  delay under **Settings → Session → Notifications**; the master switch is the "Sound alert"
  toggle there.
- **Safety monitor reactions**: with an Alpaca SafetyMonitor connected, the daemon polls it every
  10 seconds and reacts the moment it reports unsafe, per **Settings → Safety → Policies → "When
  conditions turn unsafe"**: pause the sequence + stop guiding + park (the default), park only,
  abort + park, or notify only. The app gets a `safety.unsafe` alert before the action runs. If
  **auto-resume when safe** is on, the daemon waits your configured delay after conditions clear,
  unparks, restores tracking, and resumes the paused run — verify pointing afterwards unless your
  sequence re-centers its target. A run that is paused *awaiting you* (e.g. after a failed flip)
  is never auto-resumed.
- **Weather thresholds**: beyond the boolean safety monitor, Ara can react to the numbers from a
  connected weather station — turn on **Settings → Safety → Policies → "React to weather-station
  thresholds"** and set your wind (km/h, sustained or gust), humidity (%), and dew-delta (°C)
  limits. A breached threshold is treated exactly like the safety monitor reporting unsafe: your
  configured action runs and the alert names which threshold tripped ("wind 54 km/h over the 36
  km/h limit"). Auto-resume waits until every source — monitor and weather — reads safe. Off by
  default; a sensor your station doesn't report is simply skipped.
- **Guide star lost**: when the guider loses its star for more than 30 s (a cloud), the run pauses
  under **"When the guider is lost"** and resumes on its own as soon as the guider locks on again.
  After 3 min Ara starts checking the guider's own frames once a minute: while they are starless it
  waits; once stars are back and the guider still has no lock (the mount drifted while unguided),
  Ara stops guiding, re-centers the target by plate solve, restarts guiding and resumes the run,
  with an Info notice saying so. If the solve or the restart fails it warns once and tries again
  after 5, 10, then every 15 min; it never parks. It stands aside while the safety engine holds
  the rig, hands over as soon as you pause, resume or stop the run yourself, will not slew to a
  target that has sunk below your horizon limit, and tells you once when the daemon has no plate
  solver. A dither that must restart guiding after a gap of 3 min or more re-centers first too.
- **Dec guiding runs away**: when the Dec error keeps growing for 10 guide frames in a row while the
  Dec corrections are at the guider's Max Dec Duration (a wrong Dec direction after a meridian flip
  does this), Ara
  stops guiding and runs **"When the guider is lost"**, with a notification. Calibrate the guider again
  (after a flip, check the guider's **Meridian flip reverses Dec** setting), then start guiding: once
  guiding has settled, a paused run resumes on its own.
- **Guider loss mid-sequence**: if the guider connection drops while a run is executing, the
  daemon applies **Settings → Safety → Policies → "When the guider is lost"** immediately —
  pause the run (default), skip the current target, or abort — while §63.3 process recovery
  tries to restart the guider daemon in the background. A *wedged* guider (socket alive, RPC
  unresponsive) is caught the same way by an active liveness ping. When recovery succeeds, ARA
  reconnects automatically within your "guider retry timeout" window and notifies you — just
  Resume the paused run; if it can't reconnect in time, reconnect manually (the top-bar Guider button → the connection card).
  The daemon restart only applies when the profile's guider host is the SBC itself (`localhost`,
  its own name or address). A guider running on **another machine** (PHD2 on a laptop, say) is
  not ARA's to restart: you get a "Guider connection lost" notification, ARA keeps retrying the
  connection for the same retry window, and reconnects when that machine's guider is back. The
  same rule gates the guider restart action (`POST /api/v1/equipment/guider/restart`), which
  only restarts the SBC's own guider daemon.
- **Meridian flips** run a guarded pipeline when flip safety (**Extra care on flips when you are
  away**) is on, the default: a pre-flight check (predicted altitude, mount health, required
  equipment), an in-slew watchdog (stall and timeout), a pier-side check as soon as the flip slew
  ends, a post-flip plate-solve gate when **Re-center after flip** is on (imaging does not resume on an unverified pointing), and a
  safe-rest fallback (park or stop tracking, guider stopped) if anything fails. If the mount
  reports the same pier side after the slew, the flip fails before Ara re-centers or restarts
  guiding. With flip safety off, Ara warns you at once and continues (some drivers misreport
  pier side).
- **Confirm a flip on each profile.** Until you confirm one, every flip sends a critical alert and
  waits 60 s; stop the run in that window if the scope could hit the mount. Watch one flip from
  start to end. When it completes, Ara asks you to confirm it under **Settings → Unattended
  running → When to stop**: press **Confirm** if the scope cleared the mount and tripod and
  frames and guiding came back on target. If the mount reported no pier side change (unknown, or
  the same side with flip safety off), check the flip by eye before you confirm. Ara never
  confirms on its own; an optics change, **Re-arm** or a new profile (also a duplicate) asks
  again. **Re-arm** also drops a completed flip you have not confirmed, for example after you
  re-balanced the rig.
- **Set the meridian pause per rig.** **Stop before meridian** is when imaging stops before the
  meridian. Where the camera or rotator can reach the tripod or pier, measure how long the target
  takes from a safe angle to the meridian and add a margin.
- **Set a clearance limit where the camera can reach the mount** (#1589). Near the zenith a long
  imaging train or a rotator can swing into the pier or tripod even before any flip is due.
  Turn on **Clearance limit** under **Settings → Unattended running → When to stop** and set
  **Highest safe altitude**: point the scope higher by hand with the stop controls ready, at the
  rotator angle where the camera comes closest, and set a few degrees under where it gets close.
  While it is on, Ara refuses a GoTo, a plan's slew, a Center step or a meridian flip to a target
  at or above that altitude, and nothing moves. If the mount tracks up to it, Ara stops tracking
  and guiding, pauses the run and sends a critical alert saying why; it never slews or parks the
  mount on its own. Slew to a lower target before you turn tracking back on. The limit is off on
  a new profile and on an upgraded one, it is one value per profile (it does not follow the
  rotator angle), and Simple Mode's Mount window shows it.
- **Guiding after a flip:** the guider flips its calibration from the mount's pier side. If Dec
  runs away after a flip, change the guider's **Meridian flip reverses Dec** for this mount and
  watch the next flip.
- A failed flip **pauses the run resumably** ("needs your attention") instead of killing it —
  Resume re-attempts the flip after you sort the rig out.
- **Unattended shutdown**: if a failure pauses a run and nobody responds within the
  configured window (30 min on a new profile), the daemon puts the rig to bed — guider stopped, mount
  parked, accessories disconnected, cooler warmed gently at your ramp rate, camera last — and
  leaves a morning summary. Any sign of you (opening the app, dismissing the alert, any command)
  cancels the countdown. A plan's own wait-for-user step doesn't start it. Configurable under Settings → Safety → Policies.
- **Notifications** escalate in severity during astronomical darkness so the alarm-worthy ones
  reach you; the §35 alarm loops on Critical until acknowledged.

## 8. Image library and stats

- **Library** organizes frames by session/target with a frame viewer (stretch presets + manual),
  auto-rating with HFR drift, bulk operations, and **Resume Target** to pick up where a previous
  night stopped. A session that was guided shows the polar alignment left after Align in its
  header (**PA ≥ 48″**); hover it for every measurement that night.
- **Stats** rolls the catalog up: per-target integration, focus-vs-temperature trends, guiding
  RMS history, frame-quality scoring, best-frames sorting, calendar heatmap, CSV export.

## 9. Health, diagnostics, and when things go wrong

The always-visible **health pill** aggregates open diagnostics (green/amber/red) and survives
reconnects — tap it for the panel with each issue, what the daemon did about it, and the
recommended action. Diagnostics mode (notify-only vs auto-correct) is a profile setting.

For bugs: **Help → Report a bug** builds a zip (logs + profile + diagnostics) ready to attach to a
GitHub issue. See `CONTRIBUTING.md`.

## 10. Backups

Settings → Storage: the daemon snapshots your configuration (profile + sequences) **and your
image library's records** (the frames catalog — sessions, frame metadata, ratings; never the
FITS files themselves, which back up via drive clone) as zip backups, keeps the newest N
(default 20), and restores atomically — including **from another daemon on your LAN** by URL,
verified by checksum before anything touches live config. Take a backup before big
reconfigurations; restore is a two-click undo. Restoring the frame catalog is a separate
opt-in checkbox: it rolls the library's records back to the snapshot, so frames captured since
then disappear from the library (their files stay on disk, and the startup scan can
re-register them).

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
