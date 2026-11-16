### Fixed

- A profile file that cannot be read (empty or not valid JSON) is now renamed to `<id>.json.corrupt.<unix-time>` and reported once in the daemon log, instead of a warning at every start; profile and active-profile files are also flushed to disk before they replace the old one, so a power cut is less likely to leave an empty file (#1947).
