### Fixed (Simple Mode)

- **The Plan window opens without a freeze**: the Recent list looked up each imaged target with a full scan of the 17,000-row catalog on the UI thread, about 190 ms for each name the catalog does not know. It now uses the catalog's name index, built once. A name that only matches part of a common name ("Rosette") no longer places a Recent row.
- **Tonight's Best and Recent show the nickname**: their tiles, and the Monitor's Tonight's best rows, read "NGC 246 (Skull Nebula)" as the plan header does, not "NGC0246". A Recent tile picks the catalog object, so the header keeps the nickname.
- **The Plan target card shows the object's Album Art**: the Sky Map pick's picture, with its credit behind a tap, in place of the drawn placeholder. An object with no Art keeps the placeholder.
