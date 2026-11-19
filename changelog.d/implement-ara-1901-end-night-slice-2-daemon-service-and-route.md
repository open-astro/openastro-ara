### Added (Daemon)

- **End Night on the daemon** (#1901): `POST /api/v1/end-night` puts the rig to bed and returns at once; the work carries on on the rig when the client sleeps. In order it aborts the run and closes its Session, stops guiding, finds Home when the mount can, parks (or stops tracking when it cannot park), disconnects the filter wheel, focuser, rotator and flat panel, warms the cooler at the profile's rate and switches it off, disconnects the camera, posts a summary notification, and last shuts the Pi down. A step that fails is reported and the next safe step still runs. The Pi shuts down only when asked for and only when the mount ended parked or with tracking off. `GET /api/v1/end-night/status` shows each step as pending, running, done, skipped or failed, and the `end_night.progress` WebSocket event reports each change. `POST /api/v1/end-night/cancel` stops it at the next step boundary; nothing cancels it once the shutdown has started. Starting it again while it runs is refused with the new `end-night-in-progress` cause, worded in English and Brazilian Portuguese. The screen for it comes later.

### Changed (Daemon)

- **The unattended shutdown shares its steps with End Night** (#1901): the guider stop, park, device disconnects, cooler warm-up and camera disconnect moved into one module; the unattended shutdown behaves as before.
