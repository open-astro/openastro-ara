### Changed (Simple Mode)

- **Polar align shows Rough, Fine and Verify as a stepper** (#1875): the panel's content is grouped under Setup and Error; while Verify runs and after it, only the phase line and the verdict show. "Check it" is now **Verify**, with a caption saying it moves the mount and takes three frames.
- **Done is available in the rough step once the error is inside your tolerance** (#1875): the daemon accepts Done there and records the result for "last night"; outside the tolerance it still refuses. Fine stays the green button, Done is outlined, and a caption names the tolerance.

### Added (Settings)

- **Imaging › Polar align settings** (#1736): tolerance, Send the mount Home first, the turn, the Verify step and the rough exposure are now settings, each saved at once. Each label names its range, and a value outside it is refused and not saved. Until the rig's settings have loaded, the panel says so and offers Retry. No polar-align panel saves before then, so a failed load can no longer write defaults over the rig's values.
