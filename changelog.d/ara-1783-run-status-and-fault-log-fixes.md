### Fixed (run status)

- While the first meridian flip on a profile waits out its 60 s confirmation window, the run status counts it down ("Meridian flip — first flip, starting in 0:42") instead of keeping the last exposure's text (#1783).
- A run that is paused by hand now reads "Paused" instead of keeping the last step's countdown, such as "Settling — 00:00:01". A pause that asks you to do something (a lens cap prompt, "press Resume" after a failed step) keeps that instruction (#1784).

### Changed (fault log)

- The fault log folds consecutive rows for one device and fault type in one session, no more than 10 minutes apart, into one row with a count and the first and last time; tap it for every row. The daemon's log and `/api/v1/faults` still hold every event (#1767).
