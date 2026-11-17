### Added (Settings, Simple Mode)

- **Focus settings in two tiers, with one Advanced toggle** (#1544): Settings has one Advanced switch in the panel header, on by default and kept on this device. Off, the Autofocus panel shows only its Simple tier (autofocus during plans, Keep focus sharp and the AF filter) and Equipment › Focuser hides the derived step size; a settings search hit on a hidden Autofocus setting still shows it. Every settings-registry entry now carries a `tier`, and a `derived-by` where something else derives the value (the step size and the four autofocus triggers). The Autofocus panel shows each derived value next to its override, and Equipment › Focuser shows the step size the next sweep would use (new daemon read of the derived step size).
- **Simple Mode's Focuser window gets Autofocus during plans, Keep focus sharp and AF filter** (#1544): they replace the "Refocus every" slider. Autofocus during plans off means plans never refocus by themselves (the Autofocus button still works); Relaxed, Normal and Tight each set all four triggers (Normal is today's defaults); a Custom chip shows when the triggers match no preset; the AF filter row is hidden without a filter wheel.

### Changed (Focus)

- **Bahtinov and guide focus take their exposure from the autofocus exposure** (#1544): the request's exposure is optional and the daemon falls back to the profile; the client no longer sends a duplicate default.
