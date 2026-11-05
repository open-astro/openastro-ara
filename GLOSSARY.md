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

**Night Setup**:
Simple Mode's ordered walk through the steps before imaging (plan, focus, polar alignment), whose last step starts the Session.
_Avoid_: Guided Run, Autopilot

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
Bronze, silver or gold, derived from the lights that contain the object.
_Avoid_: rank, level
