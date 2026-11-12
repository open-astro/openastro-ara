### Fixed (run status)

- **The first meridian flip's wait shows in the run status** (#1783): while the first meridian flip on a profile waits out its 60 s confirmation window, the run status counts it down ("Meridian flip — first flip, starting in 0:42") instead of keeping the last exposure's text.
- **A paused run reads "Paused"** (#1784): a run that is paused by hand now reads "Paused" instead of keeping the last step's countdown, such as "Settling — 00:00:01". A pause that asks you to do something (a lens cap prompt, "press Resume" after a failed step, also when another branch of a parallel run fails after you paused it) keeps that instruction.

### Changed (fault log)

- **Repeated drops of one device fold into one row** (#1767): the fault log folds consecutive rows for one device and fault type in one session, no more than 10 minutes apart, into one row with a count, the first and last time, and how many drops are still unresolved or, once all are resolved, whether the last one recovered or was released because you disconnected the device by hand; tap it for every row, and it stays open when new faults arrive. The daemon's log and `/api/v1/faults` still hold every event.
- **A released fault row's dot is grey** (#1767): a row released because you disconnected the device by hand (#1307) now has the grey dot of a disconnected device instead of the recovered green.
