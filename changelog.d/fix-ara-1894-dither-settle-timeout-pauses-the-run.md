### Fixed

- **A dither settle timeout no longer pauses the run** — sequencer, Dither (#1894). A settle timeout or failure now logs a warning and the next frame starts: no second dither, no pause awaiting the user, no failed instruction. A dither refused because the guider is not guiding still restarts guiding first.
