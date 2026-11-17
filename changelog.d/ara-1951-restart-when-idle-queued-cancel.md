### Added (Server)

- **Restart when idle: queued answer and cancel.** The 202 of `POST /api/v1/server/restart-on-idle` now carries `status`: `queued` while a run holds the restart, `restarting` when the 2 s notice starts at once. `DELETE /api/v1/server/restart-on-idle` cancels a queued request (204; with nothing queued it does nothing) and publishes `server.restart_cancelled` once. `GET /api/v1/server/state` fills `pending_restart` while a request is queued. A paused or aborting run holds the restart until the run is terminal (#1951).
