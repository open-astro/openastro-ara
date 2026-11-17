### Fixed (Safety)

- **Emergency Stop bounds every rung.** A device whose connection check never answers, or whose stop call blocks before it returns, is now reported as a failed step after the rung timeout and the remaining steps still run (#1985).
