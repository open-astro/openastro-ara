### Breaking changes

- **Polar Align turns 10° at a time with the motors, never a GoTo** (Polar Align, #2048): the turn and Verify step now default to 10° and are kept between 5° and 30° (a saved 60° turn or 30° step reads as 30°); every stage measures the axis from three solves with two RA-motor turns, as NINA's three-point polar alignment, and leaves the pole with the Dec motor. A mount whose driver cannot move an axis at a set rate (MoveAxis) can no longer run Polar Align.

### Fixed (Polar Align)

- **Polar Align no longer swings the mount toward the pier** (#2048): the move off the pole from Home and the RA turns were GoTos, and at Home the firmware could pick the flipped pier side; they are now single-axis motor moves at 3°/s (1°/s for the last 3°) that stop on the mount's own position, stop at once on Stop or any failure, and fail on a stall.
