### Changed (equipment)

- **Rotator moves share the device-command module** (#1860): in Advanced Mode the rotator panel now asks first, in a dialog, while a plan runs, as Simple Mode does. Both presentations say "Couldn't move the rotator" when a rotator never starts moving, including a move to the angle it already holds. A halt from outside the hub, or a driver whose moving state cannot be read, ends the move at the 300 s backstop as "Couldn't move the rotator".
- **Filter wheel 10 s check** (#1860): a wheel the 10 s check finds turning now gets the 45 s backstop instead of failing at once.
