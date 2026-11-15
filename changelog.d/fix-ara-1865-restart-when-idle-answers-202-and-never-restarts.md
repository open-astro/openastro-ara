### Fixed (server)

- **Restart when idle restarts** (#1865): the Restart card's "Restart when idle" now restarts the daemon at once when no sequence is running; while one runs it says `server.restart_pending` on the stream and restarts when the run ends (completed, aborted or failed). A second request while one is pending adds no second restart.
