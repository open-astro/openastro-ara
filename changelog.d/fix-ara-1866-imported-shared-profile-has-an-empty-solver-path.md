### Fixed

- **An imported shared profile gets the bundled solver path** (plate solving, #1866): importing a share blanked the solver path and nothing refilled it, so solves had no binary. A blank path is now filled with `/opt/openastroara/astap_cli` when the bundled solver is installed, and when it is not, the boot warning and the missing-solver check name that path instead of staying silent (`SolverPathMigrationTest.An_imported_shared_profile_gets_the_bundled_solver_path`, `A_blank_solver_path_is_reported_missing_as_the_bundled_path`).
