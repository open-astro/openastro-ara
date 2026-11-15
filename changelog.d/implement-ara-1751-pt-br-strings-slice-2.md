### Changed (every string in pt-BR)

- Brazilian Portuguese now covers the first-run server screen, the launch profile and offline launch screens, the app shell's tabs, status-bar launchers and tooltips, and the profile setup wizard's footer, Done view, profile basics, equipment connection (AlpacaBridge address, device chooser) and review screens' section titles (#1751).

### Fixed (setup wizard)

- **The wizard footer fits a phone** (#1751): at 390 px the Next button ran off the right edge and Skip was cut to a few letters. Next now stays on the right edge on every width and Skip keeps at least 100 px of its label; below 600 px the footer drops the stage name, which the step heading already shows.
