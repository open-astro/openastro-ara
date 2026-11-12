### Added (mount by hand)

- Simple Mode's Mount window has a direction pad: eight directions around a center Stop, with Slow, Medium and Fast speeds taken from the mount's own rates. Hold a direction to move and let go to stop. The arrow keys drive it, and Escape stops. The pad is off, saying why, while the mount is parked, the plan is imaging, polar alignment runs or the Guider guides (#1649).
- The rig stops a pad move by itself when the app stops renewing it, so a dropped link or a sleeping tablet can't leave the mount moving. Leaving the app or closing the window stops it too. `POST /equipment/telescope/moveaxis` takes an optional `lease_ms` (#1649).
- The pad's centre is Home for mounts that can find home, and turns into the red Stop square while the mount moves on its own (a Home, a GoTo, a park). The Park row is now Position, and says At home when the mount is there (#1649).
- A Tracking speed row sets Sidereal, Lunar, Solar or King, as the mount offers them, in Simple Mode's Mount window and in the full UI's Mount panel. New `POST /equipment/telescope/trackingrate`; the mount's status reports `tracking_rate` (#1649).
- The Mount window shows where the scope points, with Check beside it: one quick frame, plate-solved on the rig, then how far that is from the Target. Center slews and centers on it, and Sync writes the solve into the mount's model while nothing runs. A failed check says why and opens the setting that fixes it (#1650).
- Verify Position syncs a connected, still rotator to the solved angle and reports the angle (#1650).
