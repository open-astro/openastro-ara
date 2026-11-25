### Changed (Simple Mode)

- **Polar align groups its panel under Setup and Error** (#1875): while Verify runs and after it, only the phase line and the verdict show. "Check it" is now **Verify**, with a caption saying it moves the mount and takes three frames.

### Added (Settings)

- **Imaging › Polar align settings** (#1736): tolerance, the turn, the Verify step and the main camera exposure are now settings, each saved at once. Each label names its range, and a value outside it is refused and not saved. Until the rig's settings have loaded, the panel says so and offers Retry. No polar-align panel saves before then, so a failed load can no longer write defaults over the rig's values.
