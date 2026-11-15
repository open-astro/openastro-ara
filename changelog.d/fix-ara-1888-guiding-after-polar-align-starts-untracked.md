### Fixed

- **Simple Mode turns tracking on before it starts guiding** (Simple Mode, Guide, #1888). Polar Align hands tracking back as it found it, so guiding after it began on a mount that was not tracking. The Preflight Guide step and the Guide shutter now switch tracking on first, asking again up to five times a second apart when the mount is still settling; if it still refuses, guiding does not start and the pill says why. With no mount connected (ST-4 guiding through the guide camera), or a mount that cannot set tracking, guiding starts as before, with no tracking ask.
