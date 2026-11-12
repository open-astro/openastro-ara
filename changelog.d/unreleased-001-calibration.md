### Added (calibration)

- **Calibration** is a seventh Simple Mode mode, between Guider and Plan (#1757). Flats belong to the night; darks and bias belong to the camera for months. Three tabs, and the shutter and chip follow the tab:
  - **Flats**: tonight's filters, covered or missing, and the light: a panel, or any even screen or light box in front of the scope (asked for first when no panel is connected), or the twilight sky. The shutter shoots the missing flats.
  - **Darks & bias**: the camera's library, what is on file and how old (older than six months reads Old), and whether it covers the queued plan. Build or refresh what the plan needs, now or as the Queue's Calibration block.
  - **Guide Camera**: the Guider's dark library and bad-pixel map, each with Use while guiding, Rebuild and Delete. A build asks you to cover the guide scope first, is refused while guiding, and has a status pill; how it ended is a message with the guider's reason.
- The Plan sheet has a **Flats** row per target: None, After target (one set per filter right after its last frame, at its rotator angle) or End of night (the Queue's one Flats block after the last target). It replaces the profile-driven None / Sky / Panel row (#1757).
- Every calibration run goes through the Queue and has a plan square: a queue of its own when nothing is queued, after the targets otherwise (#1757).
- **Night done** offers flats every night, and darks only when the camera's library lacks tonight's (#1563). A calibration run that pauses for the lens cap or a screen asks in a pill with **Resume**.
- **Focus offsets**: Simple Mode's Focus mode gets an **Offsets** routine beside Auto and Manual: pick the filters, press the shutter, then Save or Discard the results table. The Filter wheel window shows the filter in place's saved offset. The daemon measures each filter's offset from the reference filter (two reference runs check for drift) and saves the offsets with the profile, so they survive a restart and a filter change applies them (#1675). New routes under `/equipment/filterwheel/offsets`, which the full UI runs too, from Settings › Filter Wheel's Focus offsets section. The Bahtinov readout and guide-camera focus are refused while it runs.
- The daemon records the camera on every frame (`camera_name`, `camera_id`), keys the dark and bias library by camera (`GET /calibration/dark-library/status?camera=`; without it, the connected main camera), and matches calibration coverage by camera. Frames from before keep no camera and count for any camera.
- The daemon marks a calibration session that holds only Take One snapshots (`manual_capture`); Simple Mode leaves those out of its calibration checks.
- New sequence item **Close Flat Panel Cover**: closes the motorised cover for unattended darks and bias, and fails when there is no cover to close.

### Fixed (calibration)

- An edited sequence body (a daemon-generated run with a step added) no longer fails to load on the rig: a new collection wrapper writes `$type` before `$values`, as Json.NET needs.
