### Fixed (Client)

- **Plan's Recent list places a target saved as part of a common name again.** "Rosette" for the Rosette Nebula gets its Recent row back: after the exact name index misses, the first catalog row whose common name contains the target name is used, from a list of lower-cased names built once with the index. Only imaged rows that can still reach the top five are resolved, so old misses cost nothing (#1959).
