### Breaking changes
- **Polar Align turns the way the new Turn direction setting says, not away from the meridian** (Polar Align, #2081): a run turns RA east in the northern hemisphere and west in the southern (NINA's default); Ara no longer picks the way from the mount's hour angle, which a wrong mount clock or a false zero made arbitrary. **After upgrading:** nothing to do; set Settings › Imaging › Polar align › Turn direction if you want the other way.

### Added (polar alignment)
- **Turn direction** (Polar Align, #2081): Settings › Imaging › Polar align › *Turn direction* (`polar_align.east_direction`): Automatic (east in the north, west in the south), East or West. Mount drivers do not agree on which way a positive motor speed turns RA, so on some mounts East turns west; from Home either way is safe and the measurement is the same.

### Removed (polar alignment)
- **The mid-move reversal** (Polar Align, #2081): a turn whose reading went the wrong way used to be stopped and restarted the other way. Polar Align never turns the motor back by itself.
