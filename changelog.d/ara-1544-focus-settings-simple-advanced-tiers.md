### Added (Settings, Simple Mode)

- **Focus settings in two tiers, with one Advanced toggle** (#1544): Settings has one Advanced switch in the panel header; off, a panel shows only its Simple settings, and the choice is kept on this device. Every settings-registry entry now carries a `tier` and a `derived-by`. The Autofocus panel shows each derived value next to its override, and Equipment › Focuser shows the step size the next sweep would use (new daemon read of the derived step size).
- **Simple Mode's Focuser window gets Autofocus during plans, Keep focus sharp and AF filter** (#1544): they replace the "Refocus every" slider. Autofocus during plans off means plans never refocus by themselves (the Autofocus button still works); Relaxed, Normal and Tight each set all four triggers (Normal is today's defaults); a Custom chip shows when the triggers match no preset; the AF filter row is hidden without a filter wheel.

### Changed (Focus)

- **Bahtinov and guide focus take their exposure from the autofocus exposure** (#1544): the request's exposure is optional and the daemon falls back to the profile; the client no longer sends a duplicate default.
