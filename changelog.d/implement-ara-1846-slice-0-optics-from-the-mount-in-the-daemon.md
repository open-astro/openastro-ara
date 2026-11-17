### Added (optics)

- **Optics fill from the mount** (#1846): when a mount connects, the daemon fills an empty telescope focal length and aperture in the Optics Profile Section from the mount's reported values, as the camera connect fills the sensor fields. A stored value is never overwritten, and a mount that reports none changes nothing. It works with no app open.
