### Breaking changes

- **Polar Align turns 10° at a time with the motors, never a GoTo** (Polar Align, #2048): the turn and Verify step now default to 10° and are kept between 5° and 30° (a saved 60° turn or 30° step reads as 30°); every measurement fits the axis from three solves with two RA-motor turns, as NINA's three-point polar alignment. A mount whose driver cannot move an axis at a set rate (MoveAxis) can no longer run Polar Align.

### Fixed (Polar Align)

- **Polar Align no longer swings the mount toward the pier** (#2048): the RA turns were GoTos, and at Home the firmware could pick the flipped pier side; they are now single-axis motor moves at 3°/s or the fastest speed the mount offers below it, which stop at once on Stop or any failure.
