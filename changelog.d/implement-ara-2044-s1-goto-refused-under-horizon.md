### Changed (GoTo)

- **A GoTo to a target under your horizon is refused** (#2044): `POST /api/v1/equipment/telescope/slew` checks the target's altitude against the site's horizon limit (the flat default, or the custom skyline at the target's azimuth) and answers 409 with `reason` `below-horizon-limit`, `altitude_deg` and `limit_deg`, and the mount does not move. A sync is not checked, `force` does not bypass it (it only bypasses `run_in_progress`), and a profile with no site lets the slew through. The Resume refinement and the re-center use the same check.
