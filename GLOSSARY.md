# OpenAstro Ara

A headless daemon on the rig runs a night of deep-sky imaging unattended, and an app plans that night and monitors it.

## Language

### Imaging

**Session**:
One night of imaging on the rig, from the first frame to the parked mount.
_Avoid_: imaging night, client session (for a client's connection)

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

**Polar Align Routine**:
The steps that measure the polar alignment error while the person turns the mount's bolts.
_Avoid_: Polar Align session, Polar Align run

### Connection

**Control Slot**:
The single right to command the rig, held by one connected client at a time and handed over through a takeover request.
_Avoid_: session (for holding the slot)

### Presentations

**Full UI**:
The NINA-like presentation of the app, with every option in view. It is the default.
_Avoid_: Advanced Mode, NINA Mode, Classic Mode

**Simple Mode**:
The ASIAir-like presentation of the same app, which hides options without removing them or changing their values.
_Avoid_: Easy Mode, Beginner Mode

**Companion Mode**:
The phone presentation of the app, built on Simple Mode, with no Full UI behind it.
_Avoid_: Mobile Mode

**Preflight**:
Simple Mode's ordered walk through the steps before imaging (plan, focus, polar alignment, guide), whose last step starts the Session. Not the Full UI's Checklist.
_Avoid_: Night Setup, Guided Run, Autopilot

**Screen**:
One of Simple Mode's five views (Preview, Focus, Polar align, Guide, Queue). Preflight is not a Screen; it chooses which Screen shows.
_Avoid_: mode, panel, page

**Monitor**:
Simple Mode's full-screen, read-only view of the Session, opened from the left rail. Not a Screen.
_Avoid_: Cockpit, Night Watch, Dashboard

### Sky Album

**Sky Album**:
The screen that shows the Books.
_Avoid_: collection, gallery

**Book**:
One catalog's complete set of Cards, such as Messier or Caldwell.
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
