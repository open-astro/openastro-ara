### Added (daemon, for the client)

- The run status and `sequence.progress` carry the next meridian flip: when it is expected, its state and its target (#1597).
- `POST /sequences/{id}/stop-after-frame` files the exposure in progress, then ends the run as Stopped, and holds through a client disconnect; the run status shows `stop_after_frame` (#1557).
- Each analysed frame records how many stars were clipped; `GET` frame histogram gains `clipped_stars`, null for a frame analysed before this (#1723).
