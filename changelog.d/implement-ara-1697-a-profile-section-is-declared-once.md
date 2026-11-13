### Fixed (profile sharing)

- **A shared profile no longer carries your saved targets or gear choices** (#1697): the share file now leaves out your recent and saved targets, your observer name, the telescope name, the guider camera, mount and PHD2 connection choices, the first-flip confirmation and the focuser backlash steps. The recipient sets their own. Every other profile field is listed as kept or stripped, and a test fails on a field that is neither.

### Changed (profiles)

- **Each profile section is declared once** (#1697): one table now lists every section's defaults, load repairs and share policy, and profile select, clone, load and share read it, so a section can no longer be left out of one list (the unguided block selecting a profile missed in #1264). A freshly created profile file now reads an empty calibration state, equipment identity and saved targets instead of nothing, and a pre-#1700 profile reads "no cooling fan" as an empty designation. The REST contract does not change.
