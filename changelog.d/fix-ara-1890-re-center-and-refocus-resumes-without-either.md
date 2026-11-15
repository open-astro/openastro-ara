### Fixed (Simple Mode)

- **Re-center and refocus on resume.** Resuming a paused Simple Mode run with Re-center and refocus now plate-solves, re-centers on the paused target and refocuses before the next exposure; it went straight to imaging before. The daemon also logs the reason each time it skips the refinement (#1890).
