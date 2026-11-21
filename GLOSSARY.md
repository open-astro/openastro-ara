# OpenAstro Ara

A headless daemon on the rig runs a night of deep-sky imaging unattended, and an app plans that night and monitors it.

## Language

### Imaging

**Session**:
One night of imaging on the rig, from the first frame to the parked mount.
_Avoid_: imaging night, client session (for a client's connection)

**Live Stack**:
The running colour picture of the Session's saved lights, stacked as they arrive while the Queue runs. A switch the person turns on; it is not the Live View stack that helps with focus.
_Avoid_: session stack (in person-facing text), preview

**Target**:
The sky object an imaging plan points the telescope at. Setpoints such as Target ADU or Cooler Target use the word only inside their full control name, never on its own.
_Avoid_: object (when the imaging subject is meant), bare "target" for a setpoint

**Queue**:
Simple Mode's ordered list of Targets to image in the Session, kept as one sequence on the rig.
_Avoid_: run, plan (for the list itself)

**Guiding**:
Keeping the mount locked on a star with a separate guide camera and guide optics while the main camera exposes. The bare word "guide" belongs to guiding alone.
_Avoid_: "the guide" for the User Guide

**Guide Camera**:
The small camera on the guide scope or off-axis guider that watches a guide star. It belongs to Guiding, not to the Guider.
_Avoid_: guider camera, guiding camera

**Guider**:
The guiding program that reads the Guide Camera and corrects the mount: PHD2 or openastro-guider, reached through the daemon. The device chip, the status pill ("Guider · Calibrating") and the guider window name the program. Simple Mode's **Guider** mode holds its settings (exposure, gain, dither) and starts and stops it, so it carries the same name (2026-11-06).
_Avoid_: guide (for the program), guider camera

**Calibration frames**:
The frames that are not lights: flats (an evenly lit field through each filter), darks (the shutter closed, at a light's exposure, gain and temperature) and bias (the shortest exposure). Flats belong to the night (filters, rotator angle, dust); darks and bias belong to the camera and last months, kept as its **library**. Preview's snapshots don't count as lights.
_Avoid_: calibration (alone, for the frames: it also names the Guider's mount calibration), cal frames

**Dark library**:
One camera's darks and bias, kept on the rig and reused for months: darks by exposure, gain and temperature, bias by gain, offset and binning. Simple Mode's Calibration › Darks & bias shows it and whether it covers the queued plan.
_Avoid_: tonight's darks (they are the camera's, not the night's)

**Guide Camera Custody**:
Which activity holds the Guide Camera at a given moment, and how it passes from one to the next.
_Avoid_: lease (the guider program's own word), lock

**Polar Align Routine**:
The steps that measure the polar alignment error while the mount's polar axis is turned, by the person on the mount's bolts or by a Polar Aligner.
_Avoid_: Polar Align session, Polar Align run

**Polar Aligner**:
A motorised device that moves the mount's polar axis in azimuth and altitude. In ASCOM's draft terms, an Aligner (IAlignerV1) whose purpose is PolarAligner.
_Avoid_: RPA, Polar Corrector, PA motor

### Connection

**Control Slot**:
The single right to command the rig, held by one connected client at a time and handed over through a takeover request.
_Avoid_: session (for holding the slot)

**Event Declaration**:
The one statement of an event the rig sends to the app: its name and what it carries.
_Avoid_: event catalog entry, message type

### Rig

**Profile Section**:
One named part of a profile, such as Storage or Site, read and saved as a whole.
_Avoid_: settings group, settings page

**Device Command**:
One instruction to a device that the rig waits on until it is done, stalls or fails.
_Avoid_: operation, action, move (when the wait is meant)

**Refusal**:
The rig declining a command, with a stable cause that the app puts into words.
_Avoid_: error (for a declined command), rejection, failure (that is a device fault)

**Fault**:
A device failure the daemon detected and recorded in the fault log.
_Avoid_: error, alarm

**Fault Episode**:
One Fault from detection to its end: recovered, gave up, or released by the user.
_Avoid_: incident, event (for the episode)

**Recovery**:
What an adapter tries during a Fault Episode: an Alpaca reconnect, a guider process restart, a star-lost re-centre.
_Avoid_: retry (for the whole attempt)

### Presentations

**Advanced Mode**:
The NINA-like presentation of the app, with every option in view. It is the default.
_Avoid_: Full UI, NINA Mode, Classic Mode

**Simple Mode**:
The ASIAir-like presentation of the same app, which hides options without removing them or changing their values.
_Avoid_: Easy Mode, Beginner Mode

**Companion Mode**:
The phone presentation of the app, built on Simple Mode, with no Advanced Mode behind it.
_Avoid_: Mobile Mode

**Preflight**:
Simple Mode's ordered walk through the steps before imaging (plan, focus, polar alignment, guide), whose last step starts the Session. Not Advanced Mode's Checklist.
_Avoid_: Night Setup, Guided Run, Autopilot

**Screen**:
One of Simple Mode's five views (Preview, Focus, Polar align, Guide, Queue). Preflight is not a Screen; it chooses which Screen shows.
_Avoid_: mode, panel, page

**Monitor**:
Simple Mode's full-screen, read-only view of the Session, opened from the left rail or the phone deck's Monitor word. Not a Screen.
_Avoid_: Cockpit, Night Watch, Dashboard

**Tonight's Best**:
Simple Mode's page of the Targets ranked for the rest of tonight, with sort, filters and Image this, opened from the left rail's Best item, the phone deck's Best word or See all on Plan's Tonight's Best row. A page pushed over the shell like Monitor, not a Screen.
_Avoid_: Suggestions, Best list

**Prompt**:
Something the rig's state asks or tells the person, shown as a dialog in Advanced Mode and as a pill in Simple Mode. Feedback for something the person just did is not a Prompt.
_Avoid_: notification, alert, toast, message (for a rig-raised one)

### Sky Album

**Sky Album**:
The screen that shows the Books.
_Avoid_: collection, gallery

**Book**:
One catalog's complete set of Cards, such as Messier, Caldwell or RCW. A Book shows every object it claims, so an object in two Books is a Card in each.
_Avoid_: page, list

**Card**:
One catalog object's slot in a Book.
_Avoid_: tile, sticker

**Collected**:
A Card at bronze Tier or better.
_Avoid_: imaged, done

**Tier**:
Bronze, silver or gold, derived from the lights the Card has recorded.
_Avoid_: rank, level

**Art**:
The premade picture on a Card's front, never the user's capture.
_Avoid_: thumbnail, photo, image

**Night**:
The observing night a light belongs to: anything before local noon at the site counts as the previous evening.
_Avoid_: date, day

**Placeholder**:
What a Card shows when it has no Art.
_Avoid_: silhouette, fallback

### Releases

**VERSION file**:
The one-line root file that holds the project's version: `X.Y.Z` for a stable release or `X.Y.Z~betaN` for a beta. The daemon, the app and the README badge carry the version it names. `master` always holds the last stable version; a beta version lives only on a Stable Branch.
_Avoid_: version number (for the file), build number

**Stable Branch**:
The long-lived `stable/X.Y` branch one minor or major release is cut onto at its first Beta, which then takes only fixes for defects new since the previous stable release.
_Avoid_: release branch (that is the short-lived `release/X.Y.Z` PR branch), maintenance branch

**Beta**:
A pre-release of the next minor or major release, tagged `vX.Y.Z-beta.N` on its Stable Branch for people who want to try it early.
_Avoid_: release candidate, RC, nightly (a nightly is an untagged dev build, never published)

**Promotion**:
Turning the final Beta into the stable release `X.Y.Z` on the same Stable Branch, once the promotion criteria in `docs/RELEASES.md` hold.
_Avoid_: release (alone, for this step), graduation

**Merge Down**:
A pull request that brings a Stable Branch's fixes into `master` or a newer Stable Branch, from a `merge-down/X.Y-to-<branch>` head, merged with a merge commit by a maintainer. Fixes never travel by cherry-pick.
_Avoid_: back-port, forward-port, cherry-pick, sync

**Release Notes**:
The plain-language page `docs/releases/<version>.md` that is the body of a GitHub Release, written for an astronomer deciding whether to build it tonight. `CHANGELOG.md` is the technical record, not the release notes.
_Avoid_: changelog (for the notes), release summary
