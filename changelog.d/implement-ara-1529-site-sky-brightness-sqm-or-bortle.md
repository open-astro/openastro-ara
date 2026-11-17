### Changed (Planning)

- **One sky brightness input.** The wizard's Profile basics step and Settings → Safety → Site now take either a Bortle class or an SQM reading, picked with a selector, never both. Tonight's Sky contrast, filter advice and the Optimal Sub advisor all run on that one sky, and their reasons show what you entered ("SQM 21.5 sky" or "Bortle 6 sky"). In SQM mode the Bortle class is derived from the reading; switching mode clears the other value. Existing profiles load in the matching mode (#1529).
