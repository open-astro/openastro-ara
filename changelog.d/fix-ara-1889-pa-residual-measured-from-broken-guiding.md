### Fixed

- **A polar alignment residual is no longer measured from guiding that was not holding the star** (guider, #1889): a guided run whose RA or Dec runs away against the maximum guide pulses, whose settle timed out, or that began with the mount not tracking now ends `unavailable` (reasons `guiding_not_holding`, `settle_timed_out`, `mount_not_tracking`, each with its own sentence in the strip) and writes nothing to the session's PA log, instead of logging a huge "above Polar Align" figure.
