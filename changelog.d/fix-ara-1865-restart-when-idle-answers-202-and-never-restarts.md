### Fixed (server)

- **Restart when idle restarts** (#1865): the Restart card's "Restart when idle" now restarts the daemon at once when no sequence is running; while one runs it says `server.restart_pending` on the stream and restarts once no run is on, checked in the catalog sessions and the sequencer, again after the 2 s notice (a run that starts then, such as end-of-session flats, is not killed). A second request while one is pending adds no second restart.
