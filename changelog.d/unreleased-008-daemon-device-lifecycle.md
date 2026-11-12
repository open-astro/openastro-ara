### Changed (daemon device lifecycle)

- The rotator and the dome share one device lifecycle module: connect, disconnect, remove, the refresh loop, the connection probe and the read-health check (#1696, slice 1 of 4). A dome whose Slewing read keeps failing while it still answers its connection probe now shows a degraded fault. Before, only the probe watched the dome.
- The fault recovery ladder no longer reconnects a rotator or dome that the user disconnected while the ladder ran, also when the disconnect lands just before a reconnect attempt. The device records whether the user connected it, disconnected it, or lost it, and recovery asks it first (#1539).
