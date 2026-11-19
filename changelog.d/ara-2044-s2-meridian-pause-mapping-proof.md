### Changed (Settings)

- **The meridian pause setting is now called "Stop before meridian (min)"** (#2044): the stored value is the minutes before the meridian at which imaging stops and waits for the flip, not a pause after the flip; the label, its help entry and the settings search entry now say so. The stored key `meridian_pause_min` and its value are unchanged. A server test (`Meridian_pause_min_from_the_store_opens_the_flip_window_that_many_minutes_before_the_meridian`) pins the mapping, and a client test pins the label.
